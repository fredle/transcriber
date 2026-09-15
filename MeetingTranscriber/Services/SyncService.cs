using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MeetingTranscriber.Services;

/// <summary>
/// Pulls meetings recorded on other machines (same account) down into the
/// local recordings folder, so the meeting list isn't limited to what this
/// machine itself recorded. Upload (local -> cloud) already happens live from
/// RecordingSession; this is the missing other half, cloud -> local.
///
/// A folder that was created by a pull carries a marker file, distinguishing
/// it from a folder this machine recorded itself - only marked folders are
/// ever overwritten by a later pull, so a locally-recorded meeting (which is
/// already the source of truth and is uploading live) is never touched. The
/// marker also records the remote updatedAt we last synced, so a meeting
/// that hasn't changed since (the common case - most meetings are finished
/// and static) is skipped instead of re-reading its whole lines/attendees
/// subcollections on every 60-second poll, which is what made Firestore
/// read costs balloon with the number of synced meetings.
/// </summary>
public sealed class SyncService
{
    private const string SyncMarkerFileName = ".synced_from_cloud";

    private readonly BackendClient _backend;
    private readonly Action<string>? _log;

    public SyncService(BackendClient backend, Action<string>? log = null)
    {
        _backend = backend;
        _log = log;
    }

    /// <summary>
    /// Fetches the account's cloud meeting list and, for each one, downloads
    /// it if it's missing locally, or refreshes it if it was previously
    /// pulled and its remote updatedAt has moved on since (e.g. still being
    /// recorded on the other machine). A meeting whose updatedAt matches what
    /// we last synced is left alone - no lines/attendees re-read.
    /// </summary>
    public async Task PullAsync(CancellationToken cancel = default)
    {
        List<RemoteMeeting> remote;
        try
        {
            remote = await _backend.GetMeetingsAsync(cancel: cancel).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Cloud sync skipped: {ex.Message}");
            return;
        }

        foreach (var meeting in remote)
        {
            if (string.IsNullOrWhiteSpace(meeting.Id)) continue;
            cancel.ThrowIfCancellationRequested();

            try
            {
                var folder = MeetingStore.FindMeetingFolder(meeting.Id);
                if (folder == null)
                {
                    await DownloadMeetingAsync(meeting, cancel).ConfigureAwait(false);
                }
                else
                {
                    var markerPath = Path.Combine(folder, SyncMarkerFileName);
                    if (File.Exists(markerPath) && ReadSyncedUpdatedAt(markerPath) != meeting.UpdatedAt)
                        await RefreshMeetingAsync(folder, markerPath, meeting, cancel).ConfigureAwait(false);
                    // else: unchanged since the last pull, or this machine recorded it - nothing to do.
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Cloud sync of '{meeting.Title}' failed: {ex.Message}");
            }
        }
    }

    private async Task DownloadMeetingAsync(RemoteMeeting meeting, CancellationToken cancel)
    {
        var baseDir = string.IsNullOrEmpty(meeting.Group)
            ? MeetingStore.EnsureRoot()
            : Path.Combine(MeetingStore.EnsureRoot(), meeting.Group);
        Directory.CreateDirectory(baseDir);

        var folder = Path.Combine(baseDir, meeting.Id);
        if (Directory.Exists(folder)) return;   // raced with another pull or a local recording
        Directory.CreateDirectory(folder);
        var markerPath = Path.Combine(folder, SyncMarkerFileName);
        File.WriteAllText(markerPath, "");

        await WriteTranscriptAsync(folder, meeting, cancel).ConfigureAwait(false);
        await WriteAttendeesAsync(folder, meeting.Id, cancel).ConfigureAwait(false);
        await DownloadScreenshotsAsync(folder, meeting.Id, cancel).ConfigureAwait(false);
        await WriteNotesAsync(folder, meeting.Id, cancel).ConfigureAwait(false);
        WriteSyncedUpdatedAt(markerPath, meeting.UpdatedAt);
        _log?.Invoke($"Synced meeting '{meeting.Title}' from the cloud.");
    }

    private async Task RefreshMeetingAsync(string folder, string markerPath, RemoteMeeting meeting, CancellationToken cancel)
    {
        folder = FollowGroupMove(folder, meeting);
        markerPath = Path.Combine(folder, SyncMarkerFileName);
        await WriteTranscriptAsync(folder, meeting, cancel).ConfigureAwait(false);
        await WriteAttendeesAsync(folder, meeting.Id, cancel).ConfigureAwait(false);
        await DownloadScreenshotsAsync(folder, meeting.Id, cancel).ConfigureAwait(false);
        await WriteNotesAsync(folder, meeting.Id, cancel).ConfigureAwait(false);
        WriteSyncedUpdatedAt(markerPath, meeting.UpdatedAt);
    }

    private static string? ReadSyncedUpdatedAt(string markerPath)
    {
        try
        {
            var text = File.ReadAllText(markerPath);
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (IOException)
        {
            return null;   // treat as unknown - falls through to a refresh, which is safe
        }
    }

    private static void WriteSyncedUpdatedAt(string markerPath, string? updatedAt)
    {
        try
        {
            File.WriteAllText(markerPath, updatedAt ?? "");
        }
        catch (IOException)
        {
            // best-effort - worst case the next pull refreshes this meeting again
        }
    }

    /// <summary>
    /// A synced copy's folder location is expected to track its remote
    /// group - if it was moved into (or out of) a folder on another machine,
    /// follow that move here too. Any failure (e.g. a name collision at the
    /// destination) just leaves the copy where it is; the next pull tries again.
    /// </summary>
    private static string FollowGroupMove(string folder, RemoteMeeting meeting)
    {
        var parentDir = Path.GetDirectoryName(folder);
        if (parentDir == null) return folder;

        var currentGroup = string.Equals(Path.GetFullPath(parentDir), Path.GetFullPath(MeetingStore.Root), StringComparison.OrdinalIgnoreCase)
            ? ""
            : Path.GetFileName(parentDir);
        if (string.Equals(currentGroup, meeting.Group, StringComparison.Ordinal)) return folder;

        try
        {
            MeetingStore.MoveMeeting(folder, meeting.Group);
            return MeetingStore.FindMeetingFolder(meeting.Id) ?? folder;
        }
        catch (Exception)
        {
            return folder;
        }
    }

    /// <summary>
    /// Mirrors the remote notes onto this synced copy, including clearing
    /// them locally if they were cleared remotely - the cloud is the source
    /// of truth for a synced copy, same as the transcript above.
    /// </summary>
    private async Task WriteNotesAsync(string folder, string meetingId, CancellationToken cancel)
    {
        RemoteMeeting? full;
        try
        {
            full = await _backend.GetMeetingAsync(meetingId, cancel).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return;   // notes are a nice-to-have on a synced copy - never block the transcript on them
        }

        if (string.IsNullOrEmpty(full?.Notes))
            MeetingStore.DeleteNotes(folder);
        else
            MeetingStore.SaveNotes(folder, Convert.FromBase64String(full.Notes));
    }

    private async Task WriteTranscriptAsync(string folder, RemoteMeeting meeting, CancellationToken cancel)
    {
        var lines = await _backend.GetLinesAsync(meeting.Id, cancel).ConfigureAwait(false);

        var outLines = new List<string>
        {
            JsonSerializer.Serialize(new
            {
                session_start = meeting.Started ?? DateTime.Now.ToString("o"),
                session_id = meeting.Id.StartsWith("recording_", StringComparison.Ordinal)
                    ? meeting.Id["recording_".Length..]
                    : meeting.Id,
                type = "session_metadata",
                meeting_title = meeting.Title,
                engine = meeting.Engine,
            }),
        };
        outLines.AddRange(lines.Select(l => JsonSerializer.Serialize(new
        {
            timestamp = l.Timestamp ?? DateTime.Now.ToString("o"),
            speaker = $"[{l.Speaker}]",
            speaker_label = l.SpeakerLabel,
            text = l.Text,
        })));

        var path = Path.Combine(folder, MeetingStore.TranscriptFileName);
        var tmp = path + ".tmp";
        await File.WriteAllLinesAsync(tmp, outLines, cancel).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    private async Task WriteAttendeesAsync(string folder, string meetingId, CancellationToken cancel)
    {
        var events = await _backend.GetAttendeeEventsAsync(meetingId, cancel).ConfigureAwait(false);
        if (events.Count == 0) return;

        var outLines = events.Select(e => JsonSerializer.Serialize(new
        {
            name = e.Name,
            joined = e.Joined,
            timestamp = e.Timestamp ?? DateTime.Now.ToString("o"),
        }));

        var path = Path.Combine(folder, MeetingStore.AttendeesFileName);
        var tmp = path + ".tmp";
        await File.WriteAllLinesAsync(tmp, outLines, cancel).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    private async Task DownloadScreenshotsAsync(string folder, string meetingId, CancellationToken cancel)
    {
        List<RemoteScreenshot> screenshots;
        try
        {
            screenshots = await _backend.GetScreenshotsAsync(meetingId, cancel).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return;   // screenshots are a nice-to-have on a synced copy - never block the transcript on them
        }

        foreach (var shot in screenshots)
        {
            cancel.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(shot.ObjectPath);
            if (string.IsNullOrEmpty(id)) continue;

            var path = Path.Combine(folder, $"screenshot_cloud_{id}.png");
            if (File.Exists(path)) continue;   // already pulled this one down

            var bytes = await _backend.DownloadAsync(shot.Url, cancel).ConfigureAwait(false);
            await File.WriteAllBytesAsync(path, bytes, cancel).ConfigureAwait(false);
        }
    }
}

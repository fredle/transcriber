using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeetingTranscriber.Services;

public sealed class TranscriptLine
{
    public required int SourceIndex { get; init; }   // line number within the .jsonl
    public required string Speaker { get; init; }    // "ME" or "OTHER"
    public string? SpeakerLabel { get; init; }       // diarised label, e.g. "A"
    public required string Text { get; init; }
    public DateTime? Timestamp { get; init; }

    /// <summary>
    /// Stable identity for this line's speaker within the meeting: "ME", a
    /// diarised label like "A", or "PENDING" when diarisation hasn't settled
    /// on one yet. Used both to look up a custom name and as the reassignment
    /// target when merging a line into an existing speaker.
    /// </summary>
    public string SpeakerKey => Speaker == "ME"
        ? "ME"
        : string.IsNullOrEmpty(SpeakerLabel) ? "PENDING" : SpeakerLabel;

    /// <summary>Custom name for SpeakerKey, if the user has set one. Populated by MeetingStore.ReadTranscript.</summary>
    public string? SpeakerName { get; set; }

    private string DefaultSpeakerLabel => Speaker == "ME"
        ? "Me"
        : SpeakerKey == "PENDING" ? "Other" : $"Speaker {SpeakerKey}";

    public string SpeakerDisplay => SpeakerName ?? DefaultSpeakerLabel;

    public string Display
    {
        get
        {
            var stamp = Timestamp?.ToString("HH:mm:ss") ?? "--:--:--";
            return $"[{stamp}] {SpeakerDisplay}: {Text}";
        }
    }
}

public sealed class Meeting
{
    public required string Folder { get; init; }
    public required string Path { get; init; }
    public required string Title { get; init; }
    public DateTime? Started { get; init; }
    public required string Engine { get; init; }
    public int Lines { get; init; }

    /// <summary>Name of the organizational folder this meeting lives in, or "" if unfiled.</summary>
    public string Group { get; init; } = "";
    public string GroupLabel => Group.Length == 0 ? "Unfiled" : Group;

    public string When
    {
        get
        {
            if (Started is not { } s) return "unknown date";
            var days = (DateTime.Now.Date - s.Date).Days;
            if (days == 0) return $"Today {s:HH:mm}";
            if (days == 1) return $"Yesterday {s:HH:mm}";
            return s.Year == DateTime.Now.Year ? $"{s:dd MMM HH:mm}" : $"{s:dd MMM yyyy}";
        }
    }

    public string Detail => $"{When}  ·  {Lines} lines";
}

/// <summary>
/// Reads and edits the recordings on disk. The on-disk format is unchanged
/// from the original tool - one JSON object per line, the first carrying the
/// session metadata - so existing recordings still open.
/// </summary>
public static class MeetingStore
{
    public const string TranscriptFileName = "transcriptions.jsonl";
    public const string NotesFileName = "notes.rtf";
    public const string SpeakerNamesFileName = "speakers.json";

    private static string? _root;

    /// <summary>Where recordings live. Override before first use to relocate.</summary>
    public static string Root
    {
        get => _root ??= ResolveDefaultRoot();
        set => _root = value;
    }

    /// <summary>
    /// Documents\Teeline, migrating an older Documents\Kettle or, before
    /// that, Documents\MeetingTranscriber folder from prior renames so
    /// existing recordings are never orphaned.
    /// </summary>
    private static string ResolveDefaultRoot()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var current = Path.Combine(documents, "Teeline");
        LegacyMigration.MigrateFolder(Path.Combine(documents, "Kettle"), current);
        LegacyMigration.MigrateFolder(Path.Combine(documents, "MeetingTranscriber"), current);
        return current;
    }

    public static string EnsureRoot()
    {
        Directory.CreateDirectory(Root);
        return Root;
    }

    public static List<Meeting> GetRecent(int limit = 100)
    {
        var meetings = new List<Meeting>();
        if (!Directory.Exists(Root)) return meetings;

        CollectMeetings(Root, "", meetings);
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith("recording_", StringComparison.Ordinal)) continue;
            CollectMeetings(dir, name, meetings);
        }

        meetings.Sort((a, b) => Nullable.Compare(b.Started, a.Started));
        if (meetings.Count > limit) meetings.RemoveRange(limit, meetings.Count - limit);
        return meetings;
    }

    private static void CollectMeetings(string dir, string group, List<Meeting> meetings)
    {
        foreach (var recDir in Directory.EnumerateDirectories(dir, "recording_*"))
        {
            var folder = Path.GetFileName(recDir);
            var (meta, lines) = ReadSummary(recDir);

            string title = "Untitled meeting";
            string engine = "whisper";   // predates the engine field
            DateTime? started = null;

            if (meta != null)
            {
                if (meta.Value.TryGetProperty("meeting_title", out var t) &&
                    t.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(t.GetString()))
                    title = t.GetString()!.Trim();

                if (meta.Value.TryGetProperty("engine", out var e) && e.ValueKind == JsonValueKind.String)
                    engine = e.GetString() ?? engine;

                if (meta.Value.TryGetProperty("session_start", out var s) &&
                    s.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(s.GetString(), out var parsed))
                    started = parsed;
            }

            started ??= ParseFolderStamp(folder);

            meetings.Add(new Meeting
            {
                Folder = folder,
                Path = recDir,
                Title = title,
                Started = started,
                Engine = engine,
                Lines = lines,
                Group = group,
            });
        }
    }

    /// <summary>Organizational folders under Root, used to group meetings. Does not include recording folders themselves.</summary>
    public static List<string> GetFolders()
    {
        if (!Directory.Exists(Root)) return new List<string>();
        var folders = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith("recording_", StringComparison.Ordinal)) continue;
            folders.Add(name);
        }
        folders.Sort(StringComparer.CurrentCultureIgnoreCase);
        return folders;
    }

    public static void CreateFolder(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            throw new ArgumentException("Enter a folder name.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Folder name contains characters that aren't allowed.");
        if (name.StartsWith("recording_", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("That name is reserved for recordings.");

        var path = Path.Combine(EnsureRoot(), name);
        if (Directory.Exists(path))
            throw new ArgumentException("A folder with that name already exists.");
        Directory.CreateDirectory(path);
    }

    /// <summary>Move a recording's folder to an organizational folder ("" moves it back to the root/unfiled).</summary>
    public static void MoveMeeting(string meetingPath, string targetFolder)
    {
        var leaf = Path.GetFileName(meetingPath);
        var destDir = targetFolder.Length == 0 ? EnsureRoot() : Path.Combine(EnsureRoot(), targetFolder);
        Directory.CreateDirectory(destDir);

        var destPath = Path.Combine(destDir, leaf);
        if (string.Equals(Path.GetFullPath(destPath), Path.GetFullPath(meetingPath), StringComparison.OrdinalIgnoreCase))
            return;
        if (Directory.Exists(destPath))
            throw new IOException("A recording with that name already exists there.");

        Directory.Move(meetingPath, destPath);
    }

    /// <summary>
    /// Read a transcript without fighting the writer. A recording in progress
    /// holds its own transcript open for writing, and the default share mode
    /// used by File.ReadLines conflicts with that - which previously threw
    /// straight out of a UI event handler and took the process down.
    /// </summary>
    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) != null)
            yield return line;
    }

    private static DateTime? ParseFolderStamp(string folder)
    {
        const string prefix = "recording_";
        if (!folder.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var stamp = folder[prefix.Length..];
        // Rollovers within one second get a "_2" suffix.
        var underscore = stamp.IndexOf('_', 9);
        if (underscore > 0) stamp = stamp[..underscore];
        return DateTime.TryParseExact(stamp, "yyyyMMdd_HHmmss", null,
            System.Globalization.DateTimeStyles.None, out var dt) ? dt : null;
    }

    private static (JsonElement? meta, int lines) ReadSummary(string dir)
    {
        var path = Path.Combine(dir, TranscriptFileName);
        if (!File.Exists(path)) return (null, 0);

        JsonElement? meta = null;
        var count = 0;
        try
        {
            var index = 0;
            foreach (var raw in ReadLinesShared(path))
            {
                var line = raw.Trim();
                if (line.Length == 0) { index++; continue; }
                if (index == 0)
                {
                    if (TryParse(line, out var first) &&
                        first.TryGetProperty("type", out var type) &&
                        type.GetString() == "session_metadata")
                        meta = first;
                    else
                        count++;
                }
                else count++;
                index++;
            }
        }
        catch (IOException) { /* mid-write; report what we have */ }
        return (meta, count);
    }

    public static List<TranscriptLine> ReadTranscript(string dir)
    {
        var result = new List<TranscriptLine>();
        var path = Path.Combine(dir, TranscriptFileName);
        if (!File.Exists(path)) return result;

        var index = -1;
        foreach (var raw in ReadLinesShared(path))
        {
            index++;
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (!TryParse(line, out var entry)) continue;
            if (entry.TryGetProperty("type", out var type) && type.GetString() == "session_metadata")
                continue;

            var text = entry.TryGetProperty("text", out var t) ? (t.GetString() ?? "").Trim() : "";
            if (text.Length == 0) continue;

            var speakerRaw = entry.TryGetProperty("speaker", out var s) ? s.GetString() : null;
            DateTime? stamp = entry.TryGetProperty("timestamp", out var ts) &&
                              DateTime.TryParse(ts.GetString(), out var parsed) ? parsed : null;

            result.Add(new TranscriptLine
            {
                SourceIndex = index,
                Speaker = speakerRaw == "[ME]" ? "ME" : "OTHER",
                SpeakerLabel = entry.TryGetProperty("speaker_label", out var sl) ? sl.GetString() : null,
                Text = text,
                Timestamp = stamp,
            });
        }

        if (result.Count > 0)
        {
            var names = LoadSpeakerNames(dir);
            if (names.Count > 0)
            {
                foreach (var line in result)
                    if (names.TryGetValue(line.SpeakerKey, out var name))
                        line.SpeakerName = name;
            }
        }
        return result;
    }

    /// <summary>Custom speaker names for a meeting, keyed by TranscriptLine.SpeakerKey.</summary>
    public static Dictionary<string, string> LoadSpeakerNames(string dir)
    {
        var path = Path.Combine(dir, SpeakerNamesFileName);
        if (!File.Exists(path)) return new Dictionary<string, string>();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    public static void SaveSpeakerNames(string dir, Dictionary<string, string> names)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, SpeakerNamesFileName);
        var tmp = path + ".tmp";
        var clean = names
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Trim());
        File.WriteAllText(tmp, JsonSerializer.Serialize(clean));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Reassign transcript lines to a different speaker. speakerLabel is null
    /// for the "ME" channel and the diarised label (e.g. "A") for "OTHER".
    /// Rewrites only the touched lines' speaker fields, via a temp file so an
    /// interrupted write cannot leave a half-truncated transcript.
    /// </summary>
    public static void SetLineSpeaker(string dir, IEnumerable<int> sourceIndices, string speakerChannel, string? speakerLabel)
    {
        var path = Path.Combine(dir, TranscriptFileName);
        var lines = new List<string>(ReadLinesShared(path));

        foreach (var index in sourceIndices)
        {
            if (index < 0 || index >= lines.Count)
                throw new InvalidOperationException("Transcript changed on disk.");

            var node = JsonNode.Parse(lines[index])?.AsObject()
                ?? throw new InvalidOperationException("Transcript changed on disk.");
            node["speaker"] = $"[{speakerChannel}]";
            node["speaker_label"] = speakerLabel;
            lines[index] = node.ToJsonString();
        }

        var tmp = path + ".tmp";
        File.WriteAllLines(tmp, lines);
        File.Move(tmp, path, overwrite: true);
    }

    private static bool TryParse(string line, out JsonElement element)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            element = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }

    /// <summary>
    /// Delete transcript lines by their index within the file. Indices are
    /// removed highest-first so earlier removals cannot shift later ones, and
    /// the rewrite goes through a temp file so an interrupted write cannot
    /// leave a half-truncated transcript.
    /// </summary>
    public static void DeleteLines(string dir, IEnumerable<int> sourceIndices)
    {
        var path = Path.Combine(dir, TranscriptFileName);
        var lines = new List<string>(ReadLinesShared(path));

        var ordered = new List<int>(sourceIndices);
        ordered.Sort((a, b) => b.CompareTo(a));
        foreach (var index in ordered)
        {
            if (index < 0 || index >= lines.Count)
                throw new InvalidOperationException("Transcript changed on disk.");
            lines.RemoveAt(index);
        }

        var tmp = path + ".tmp";
        File.WriteAllLines(tmp, lines);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Rich-text notes for a meeting, or null if none have been saved yet.</summary>
    public static byte[]? LoadNotes(string dir)
    {
        var path = Path.Combine(dir, NotesFileName);
        if (!File.Exists(path)) return null;
        try { return File.ReadAllBytes(path); }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// Write RTF notes for a meeting, via a temp file so an interrupted write
    /// can't corrupt them. A meeting whose folder has gone - deleted, or moved
    /// out from under us - is skipped rather than recreated: creating the
    /// directory here would resurrect a just-deleted meeting as an empty one.
    /// </summary>
    public static void SaveNotes(string dir, byte[] rtfBytes)
    {
        if (!Directory.Exists(dir)) return;
        var path = Path.Combine(dir, NotesFileName);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, rtfBytes);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Delete a whole recording to the Recycle Bin, so a mistake is
    /// recoverable from Explorer rather than gone.
    /// </summary>
    public static void DeleteRecording(string dir)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            // Double-null terminated list of paths.
            pFrom = dir + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT,
        };
        var result = SHFileOperation(ref op);
        if (result != 0 || op.fAnyOperationsAborted)
            throw new IOException($"Shell delete failed (code {result}).");
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_SILENT = 0x0004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}

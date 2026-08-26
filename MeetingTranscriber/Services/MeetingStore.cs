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

/// <summary>One join or leave sighting of a remote participant, as recorded live during a recording.</summary>
public sealed class AttendeeEvent
{
    public required string Name { get; init; }
    public required bool Joined { get; init; }   // true = join, false = leave
    public required DateTime Timestamp { get; init; }
}

/// <summary>
/// One continuous stretch a participant was present for. A name can have
/// more than one of these if they left and rejoined; a null Left means they
/// were still present as of the last recorded sighting.
/// </summary>
public sealed class AttendeeSummary
{
    public required string Name { get; init; }
    public required DateTime Joined { get; init; }
    public DateTime? Left { get; init; }

    public string Display => Left is { } left
        ? $"{Name} — {Joined:HH:mm}–{left:HH:mm}"
        : $"{Name} — joined {Joined:HH:mm}, still on call";
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
    public const string AttendeesFileName = "attendees.jsonl";

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
    /// Change a meeting's stored title, by rewriting the meeting_title field
    /// on its session_metadata line. A recording from before that line
    /// existed gets one inserted instead.
    /// </summary>
    public static void RenameMeeting(string dir, string title)
    {
        title = title.Trim();
        if (title.Length == 0)
            throw new ArgumentException("Enter a meeting name.");

        var path = Path.Combine(dir, TranscriptFileName);
        var lines = new List<string>(ReadLinesShared(path));

        var index = lines.FindIndex(l => l.Trim().Length > 0);
        var node = index >= 0 ? JsonNode.Parse(lines[index])?.AsObject() : null;
        if (node != null && node["type"]?.GetValue<string>() == "session_metadata")
        {
            node["meeting_title"] = title;
            lines[index] = node.ToJsonString();
        }
        else
        {
            lines.Insert(0, JsonSerializer.Serialize(new
            {
                session_start = (ParseFolderStamp(Path.GetFileName(dir)) ?? DateTime.Now).ToString("o"),
                type = "session_metadata",
                meeting_title = title,
                engine = "whisper",
            }));
        }

        var tmp = path + ".tmp";
        File.WriteAllLines(tmp, lines);
        File.Move(tmp, path, overwrite: true);
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

    /// <summary>
    /// Whether a recording has no transcript lines and no notes, so keeping
    /// it around would only clutter the meeting list.
    /// </summary>
    public static bool IsEmpty(string dir)
    {
        if (ReadTranscript(dir).Count > 0) return false;
        return !File.Exists(Path.Combine(dir, NotesFileName));
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

    /// <summary>Remove a meeting's saved notes, e.g. once the user has cleared them back to empty.</summary>
    public static void DeleteNotes(string dir)
    {
        var path = Path.Combine(dir, NotesFileName);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// Save a screenshot into a meeting's folder, timestamped so several can
    /// coexist. Returns the saved path, or null if the folder has gone.
    /// </summary>
    public static string? SaveScreenshot(string dir, byte[] pngBytes)
    {
        if (!Directory.Exists(dir)) return null;
        var path = Path.Combine(dir, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        File.WriteAllBytes(path, pngBytes);
        return path;
    }

    /// <summary>Screenshots saved for a meeting, oldest first (the timestamped filenames sort chronologically).</summary>
    public static List<string> GetScreenshots(string dir)
    {
        if (!Directory.Exists(dir)) return new List<string>();
        var files = Directory.GetFiles(dir, "screenshot_*.png");
        Array.Sort(files, StringComparer.Ordinal);
        return files.ToList();
    }

    /// <summary>
    /// Append one join/leave sighting of a remote participant. Written live
    /// during recording, so this is an append-only log (like the transcript)
    /// rather than a rewritten whole-file snapshot (like speakers.json) - a
    /// crash mid-meeting still leaves prior sightings intact. No-ops if the
    /// folder has gone.
    /// </summary>
    public static void AppendAttendeeEvent(string dir, string name, bool joined, DateTime timestamp)
    {
        if (!Directory.Exists(dir)) return;
        var path = Path.Combine(dir, AttendeesFileName);
        var entry = new { name, joined, timestamp = timestamp.ToString("o") };
        using var writer = new StreamWriter(path, append: true);
        writer.WriteLine(JsonSerializer.Serialize(entry));
    }

    /// <summary>Raw join/leave sightings for a meeting, in the order they were recorded.</summary>
    public static List<AttendeeEvent> GetAttendeeEvents(string dir)
    {
        var result = new List<AttendeeEvent>();
        var path = Path.Combine(dir, AttendeesFileName);
        if (!File.Exists(path)) return result;

        foreach (var raw in ReadLinesShared(path))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (!TryParse(line, out var entry)) continue;

            var name = entry.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var joined = entry.TryGetProperty("joined", out var j) && j.GetBoolean();
            DateTime? stamp = entry.TryGetProperty("timestamp", out var ts) &&
                               DateTime.TryParse(ts.GetString(), out var parsed) ? parsed : null;
            if (stamp == null) continue;

            result.Add(new AttendeeEvent { Name = name, Joined = joined, Timestamp = stamp.Value });
        }
        return result;
    }

    /// <summary>Attendee presence segments for a meeting, aggregated from the raw join/leave log.</summary>
    public static List<AttendeeSummary> GetAttendeeSummary(string dir)
    {
        var open = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AttendeeSummary>();

        foreach (var e in GetAttendeeEvents(dir))
        {
            if (e.Joined)
            {
                open[e.Name] = e.Timestamp;
            }
            else if (open.TryGetValue(e.Name, out var joinedAt))
            {
                result.Add(new AttendeeSummary { Name = e.Name, Joined = joinedAt, Left = e.Timestamp });
                open.Remove(e.Name);
            }
        }
        foreach (var (name, joinedAt) in open)
            result.Add(new AttendeeSummary { Name = name, Joined = joinedAt, Left = null });

        return result.OrderBy(a => a.Joined).ToList();
    }

    /// <summary>
    /// Merge several recordings into one new recording, interleaving their
    /// transcript lines chronologically by wall-clock timestamp. Diarised
    /// speaker labels are remapped per source meeting so an unrelated
    /// "Speaker A" from one meeting is never conflated with "Speaker A" from
    /// another. Notes aren't handled here since merging RTF needs WPF's
    /// FlowDocument, which this class doesn't otherwise depend on - the
    /// caller merges those separately and calls SaveNotes on the result.
    /// The source recordings are moved to the Recycle Bin once the merge
    /// succeeds, the same as a regular delete.
    /// </summary>
    public static string MergeMeetings(IReadOnlyList<Meeting> meetings, string title)
    {
        if (meetings.Count < 2)
            throw new ArgumentException("Select at least two meetings to merge.");

        var ordered = meetings.OrderBy(m => m.Started ?? DateTime.MaxValue).ToList();

        // Collect every non-metadata line from every source, tagged with
        // which meeting it came from so OTHER speaker labels can be
        // remapped without colliding across sources.
        var tagged = new List<(JsonObject Node, int MeetingIndex, DateTime Sort)>();
        for (var mi = 0; mi < ordered.Count; mi++)
        {
            var path = Path.Combine(ordered[mi].Path, TranscriptFileName);
            if (!File.Exists(path)) continue;

            var index = -1;
            foreach (var raw in ReadLinesShared(path))
            {
                index++;
                var line = raw.Trim();
                if (line.Length == 0) continue;

                JsonObject? node;
                try { node = JsonNode.Parse(line)?.AsObject(); }
                catch (JsonException) { continue; }
                if (node == null) continue;
                if (node["type"]?.GetValue<string>() == "session_metadata") continue;

                var stampText = node["timestamp"]?.GetValue<string>();
                var sort = DateTime.TryParse(stampText, out var parsed)
                    ? parsed
                    : (ordered[mi].Started ?? DateTime.MinValue).AddMilliseconds(index);
                tagged.Add((node, mi, sort));
            }
        }

        tagged.Sort((a, b) => a.Sort.CompareTo(b.Sort));

        // Assign a globally unique diarisation letter to each (source
        // meeting, original label) pair.
        var labelMap = new Dictionary<(int, string), string>();
        var usedLetters = new HashSet<string>();
        string NextLetter()
        {
            var letter = Enumerable.Range('A', 26).Select(c => ((char)c).ToString())
                .FirstOrDefault(l => !usedLetters.Contains(l))
                ?? Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
            usedLetters.Add(letter);
            return letter;
        }

        foreach (var (node, mi, _) in tagged)
        {
            var speaker = node["speaker"]?.GetValue<string>();
            var label = node["speaker_label"]?.GetValue<string>();
            if (speaker != "[OTHER]" || string.IsNullOrEmpty(label) || label == "PENDING") continue;

            var key = (mi, label);
            if (!labelMap.TryGetValue(key, out var mapped))
            {
                mapped = NextLetter();
                labelMap[key] = mapped;
            }
            node["speaker_label"] = mapped;
        }

        // Merge custom speaker names the same way; first meeting to name a
        // (remapped) speaker wins if two sources somehow disagree.
        var mergedNames = new Dictionary<string, string>();
        for (var mi = 0; mi < ordered.Count; mi++)
        {
            foreach (var (key, name) in LoadSpeakerNames(ordered[mi].Path))
            {
                if (key == "PENDING") continue;
                var newKey = key == "ME" ? "ME" : labelMap.TryGetValue((mi, key), out var m) ? m : key;
                if (!mergedNames.ContainsKey(newKey)) mergedNames[newKey] = name;
            }
        }

        var start = ordered[0].Started ?? DateTime.Now;
        var folder = CreateRecordingFolder(ordered[0].Group, start);

        var outLines = new List<string>
        {
            JsonSerializer.Serialize(new
            {
                session_start = start.ToString("o"),
                session_id = Path.GetFileName(folder)["recording_".Length..],
                type = "session_metadata",
                meeting_title = title,
                engine = ordered[0].Engine,
            }),
        };
        outLines.AddRange(tagged.Select(t => t.Node.ToJsonString()));
        File.WriteAllLines(Path.Combine(folder, TranscriptFileName), outLines);

        if (mergedNames.Count > 0) SaveSpeakerNames(folder, mergedNames);

        foreach (var m in ordered) DeleteRecording(m.Path);

        return folder;
    }

    private static string CreateRecordingFolder(string group, DateTime start)
    {
        var baseDir = group.Length == 0 ? EnsureRoot() : Path.Combine(EnsureRoot(), group);
        Directory.CreateDirectory(baseDir);
        var stamp = start.ToString("yyyyMMdd_HHmmss");
        var folder = Path.Combine(baseDir, $"recording_{stamp}");
        var suffix = 1;
        while (Directory.Exists(folder))
        {
            suffix++;
            folder = Path.Combine(baseDir, $"recording_{stamp}_{suffix}");
        }
        Directory.CreateDirectory(folder);
        return folder;
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

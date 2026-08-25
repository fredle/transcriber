using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MeetingTranscriber.Services;

public sealed class TranscriptLine
{
    public required int SourceIndex { get; init; }   // line number within the .jsonl
    public required string Speaker { get; init; }    // "ME" or "OTHER"
    public string? SpeakerLabel { get; init; }       // diarised label, e.g. "A"
    public required string Text { get; init; }
    public DateTime? Timestamp { get; init; }

    public string Display
    {
        get
        {
            var stamp = Timestamp?.ToString("HH:mm:ss") ?? "--:--:--";
            var body = Speaker == "OTHER" && !string.IsNullOrEmpty(SpeakerLabel) && SpeakerLabel != "PENDING"
                ? $"[{SpeakerLabel}] {Text}"
                : Text;
            return $"[{stamp}] {Speaker}: {body}";
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
    public string EngineLabel => Engine == "assemblyai" ? "AssemblyAI" : "Whisper";
}

/// <summary>
/// Reads and edits the recordings on disk. The on-disk format is unchanged
/// from the original tool - one JSON object per line, the first carrying the
/// session metadata - so existing recordings still open.
/// </summary>
public static class MeetingStore
{
    public const string TranscriptFileName = "transcriptions.jsonl";

    private static string? _root;

    /// <summary>Where recordings live. Override before first use to relocate.</summary>
    public static string Root
    {
        get => _root ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "MeetingTranscriber");
        set => _root = value;
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

        foreach (var dir in Directory.EnumerateDirectories(Root, "recording_*"))
        {
            var folder = Path.GetFileName(dir);
            var (meta, lines) = ReadSummary(dir);

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
                Path = dir,
                Title = title,
                Started = started,
                Engine = engine,
                Lines = lines,
            });
        }

        meetings.Sort((a, b) => Nullable.Compare(b.Started, a.Started));
        if (meetings.Count > limit) meetings.RemoveRange(limit, meetings.Count - limit);
        return meetings;
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
        return result;
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

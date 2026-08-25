using System;
using System.IO;

namespace MeetingTranscriber.Services;

/// <summary>
/// One-time migration from an earlier name the app shipped under
/// (MeetingTranscriber, then Kettle, now Teeline) to wherever it lives now,
/// for the folders that hold real user data: the recordings root and the
/// settings/AppData directory. Runs at most once per machine - once the
/// legacy folder is gone, every later call is a cheap no-op, so callers can
/// invoke this unconditionally on every startup.
/// </summary>
internal static class LegacyMigration
{
    public static void MigrateFolder(string legacyPath, string currentPath)
    {
        try
        {
            if (Directory.Exists(currentPath) || !Directory.Exists(legacyPath)) return;
            var parent = Path.GetDirectoryName(currentPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            Directory.Move(legacyPath, currentPath);
        }
        catch (IOException) { /* leave data where it is rather than fail startup */ }
        catch (UnauthorizedAccessException) { }
    }
}

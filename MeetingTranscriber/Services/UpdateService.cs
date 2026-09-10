using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace MeetingTranscriber.Services;

/// <summary>
/// Checks GitHub Releases for a newer Teeline build and downloads it in the
/// background. Deliberately never applies an update on its own: a meeting
/// could be recording, and applying an update restarts the process, so that
/// only happens when the caller explicitly asks (see MainWindow's "Restart
/// to update" tray item), after confirming nothing is in progress.
/// </summary>
public sealed class UpdateService
{
    private readonly UpdateManager _manager = new(
        new GithubSource("https://github.com/fredle/transcriber", null, false));

    private UpdateInfo? _pendingUpdate;

    /// <summary>False for a dev build run straight from source, or a portable/zip copy - there's nothing installed to update in place.</summary>
    public bool IsInstalled => _manager.IsInstalled;

    public bool UpdateReady => _pendingUpdate != null;
    public string? PendingVersion => _pendingUpdate?.TargetFullRelease.Version.ToString();

    /// <summary>Best-effort check-and-download. Returns true if an update is now ready to apply.</summary>
    public async Task<bool> CheckAndDownloadAsync()
    {
        if (!IsInstalled || _pendingUpdate != null) return _pendingUpdate != null;

        var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info == null) return false;

        await _manager.DownloadUpdatesAsync(info).ConfigureAwait(false);
        _pendingUpdate = info;
        return true;
    }

    /// <summary>Exits the process, applies the downloaded update, and relaunches. Only call when it's safe to restart.</summary>
    public void ApplyAndRestart()
    {
        if (_pendingUpdate != null) _manager.ApplyUpdatesAndRestart(_pendingUpdate.TargetFullRelease);
    }
}

using System;
using System.Drawing;
using System.Windows.Forms;

namespace MeetingTranscriber.Services;

/// <summary>
/// Notification-area presence, so the app can keep watching for Teams calls
/// with no window on screen. Closing the window hides it here rather than
/// exiting; the process only ends via Exit on this menu.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _showItem;
    private readonly ToolStripMenuItem _transcribeItem;
    private readonly ToolStripMenuItem _autoStartItem;

    public event Action? ShowRequested;
    public event Action? ToggleTranscribeRequested;
    public event Action? OpenFolderRequested;
    public event Action? ExitRequested;
    public event Action<bool>? AutoStartChanged;

    public TrayIcon(bool autoStart)
    {
        _showItem = new ToolStripMenuItem("Open Teeline",
            null, (_, _) => ShowRequested?.Invoke()) { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) };
        _transcribeItem = new ToolStripMenuItem("Start transcribing",
            null, (_, _) => ToggleTranscribeRequested?.Invoke());
        _autoStartItem = new ToolStripMenuItem("Start automatically on a Teams call")
        {
            CheckOnClick = true,
            Checked = autoStart,
        };
        _autoStartItem.CheckedChanged += (_, _) => AutoStartChanged?.Invoke(_autoStartItem.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_showItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_transcribeItem);
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open transcripts folder",
            null, (_, _) => OpenFolderRequested?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke()));

        _icon = new NotifyIcon
        {
            // The app's own icon, taken from the executable, so the tray
            // matches the taskbar and the shortcut.
            Icon = LoadAppIcon(),
            Text = "Teeline",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var extracted = Icon.ExtractAssociatedIcon(path);
                if (extracted != null) return extracted;
            }
        }
        catch (Exception)
        {
            // Fall through to the stock icon rather than failing to start.
        }
        return SystemIcons.Application;
    }

    /// <summary>Reflect state in the tooltip and the menu's wording.</summary>
    public void Update(bool transcribing, bool inCall, string? meetingTitle)
    {
        _transcribeItem.Text = transcribing ? "Stop transcribing" : "Start transcribing";

        var status = transcribing ? "Transcribing" : inCall ? "Teams call in progress" : "Idle";
        if (!string.IsNullOrWhiteSpace(meetingTitle)) status += $" - {meetingTitle}";
        // The tray tooltip is capped at 63 characters; longer text is dropped
        // entirely by the shell rather than truncated.
        var text = $"Teeline: {status}";
        _icon.Text = text.Length > 63 ? text[..60] + "..." : text;
    }

    /// <summary>
    /// Show a small, self-dismissing popup near the tray. Windows renders
    /// NotifyIcon balloon tips as full Action Center toasts and ignores their
    /// requested timeout, so a custom <see cref="Toast"/> is used instead to
    /// keep notifications compact and brief. When <paramref name="onClick"/>
    /// is given, clicking it runs that instead of just reopening the window -
    /// e.g. the "Teams call detected" prompt starts transcribing right there.
    /// </summary>
    public void Notify(string title, string message, Action? onClick = null)
    {
        try
        {
            new Toast(title, message, onClick ?? ShowRequested).Reveal();
        }
        catch (Exception)
        {
            // Notifications are advisory; never let one break the app.
        }
    }

    public void SetAutoStart(bool value)
    {
        if (_autoStartItem.Checked != value) _autoStartItem.Checked = value;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

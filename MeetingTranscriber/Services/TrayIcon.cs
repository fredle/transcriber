using System;
using System.Drawing;
using System.Drawing.Drawing2D;
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
    private readonly ToolStripMenuItem _autoStopItem;
    private readonly Icon _idleIcon;
    private readonly Icon _recordingIcon;
    private bool? _shownAsRecording;

    public event Action? ShowRequested;
    public event Action? ToggleTranscribeRequested;
    public event Action? OpenFolderRequested;
    public event Action? ExitRequested;
    public event Action<bool>? AutoStartChanged;
    public event Action<bool>? AutoStopChanged;

    public TrayIcon(bool autoStart, bool autoStop)
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
        _autoStopItem = new ToolStripMenuItem("Stop automatically when the call ends")
        {
            CheckOnClick = true,
            Checked = autoStop,
        };
        _autoStopItem.CheckedChanged += (_, _) => AutoStopChanged?.Invoke(_autoStopItem.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_showItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_transcribeItem);
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(_autoStopItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open transcripts folder",
            null, (_, _) => OpenFolderRequested?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke()));

        _idleIcon = LoadAppIcon();
        _recordingIcon = BuildRecordingIcon(_idleIcon);

        _icon = new NotifyIcon
        {
            // The app's own icon, taken from the executable, so the tray
            // matches the taskbar and the shortcut.
            Icon = _idleIcon,
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

    /// <summary>
    /// Badge the base icon with a red recording dot in the bottom-right
    /// corner, so transcribing-in-progress is visible at a glance in the
    /// notification area without having to hover for the tooltip.
    /// </summary>
    private static Icon BuildRecordingIcon(Icon baseIcon)
    {
        var size = baseIcon.Width;
        using var bitmap = baseIcon.ToBitmap();
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var dotSize = Math.Max(6, size / 2);
        var rect = new RectangleF(size - dotSize, size - dotSize, dotSize, dotSize);
        graphics.FillEllipse(Brushes.White, rect);
        rect.Inflate(-dotSize * 0.12f, -dotSize * 0.12f);
        graphics.FillEllipse(Brushes.Red, rect);

        var handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }

    /// <summary>Reflect state in the tooltip, the menu's wording, and the icon itself.</summary>
    public void Update(bool transcribing, bool inCall, string? meetingTitle)
    {
        _transcribeItem.Text = transcribing ? "Stop transcribing" : "Start transcribing";

        if (_shownAsRecording != transcribing)
        {
            _icon.Icon = transcribing ? _recordingIcon : _idleIcon;
            _shownAsRecording = transcribing;
        }

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

    public void SetAutoStop(bool value)
    {
        if (_autoStopItem.Checked != value) _autoStopItem.Checked = value;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _idleIcon.Dispose();
        _recordingIcon.Dispose();
    }
}

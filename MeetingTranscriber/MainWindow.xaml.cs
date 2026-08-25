using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using MeetingTranscriber.Services;

namespace MeetingTranscriber;

public partial class MainWindow : Window
{
    // Pulled from Theme.xaml so the transcript colours always match the rest
    // of the palette rather than drifting from it.
    private static Brush Themed(string key) =>
        (Brush)Application.Current.FindResource(key);

    private Brush MicBrush => Themed("Mic");
    private Brush SpkBrush => Themed("Spk");
    private Brush DimBrush => Themed("Dim");

    private readonly Settings _settings = Settings.Load();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    private RecordingSession? _session;
    private bool _busy;
    private Meeting? _openMeeting;
    private List<TranscriptLine> _openLines = new();

    public MainWindow()
    {
        InitializeComponent();

        ApiKeyBox.Password = _settings.ApiKey;
        LoadDevices();
        RefreshMeetings();

        _uiTimer.Tick += OnUiTick;
        _uiTimer.Start();

        Log($"Recordings folder: {MeetingStore.Root}");
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
            Log("No AssemblyAI key set - enter one above and press Save key.");
    }

    // ── Devices ───────────────────────────────────────────────────────────

    private void LoadDevices()
    {
        var mics = AudioDevices.GetMicrophones();
        var speakers = AudioDevices.GetSpeakers();

        MicCombo.ItemsSource = mics;
        SpeakerCombo.ItemsSource = speakers;

        // Default to whatever Teams is using, unless a choice was saved.
        MicCombo.SelectedItem =
            mics.FirstOrDefault(d => d.Id == _settings.MicDeviceId)
            ?? mics.FirstOrDefault(d => d.IsDefaultComms)
            ?? mics.FirstOrDefault();
        SpeakerCombo.SelectedItem =
            speakers.FirstOrDefault(d => d.Id == _settings.SpeakerDeviceId)
            ?? speakers.FirstOrDefault(d => d.IsDefaultComms)
            ?? speakers.FirstOrDefault();

        var commsMic = mics.FirstOrDefault(d => d.IsDefaultComms);
        var commsSpk = speakers.FirstOrDefault(d => d.IsDefaultComms);
        MicHint.Text = commsMic is null ? "Teams device not detected" : $"Teams using: {commsMic.Name}";
        SpkHint.Text = commsSpk is null ? "Teams device not detected" : $"Teams using: {commsSpk.Name}";
    }

    private void OnRefreshDevices(object sender, RoutedEventArgs e) => LoadDevices();

    private void OnSaveKey(object sender, RoutedEventArgs e)
    {
        _settings.ApiKey = ApiKeyBox.Password.Trim();
        _settings.Save();
        Log(_settings.ApiKey.Length > 0 ? "API key saved." : "API key cleared.");
    }

    // ── Recording ─────────────────────────────────────────────────────────

    private async void OnToggleRecording(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        RecordButton.IsEnabled = false;
        try
        {
            if (_session == null) await StartRecordingAsync();
            else await StopRecordingAsync();
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Recording error", MessageBoxButton.OK, MessageBoxImage.Error);
            _session = null;
            SetRecordingUi(false);
        }
        finally
        {
            RecordButton.IsEnabled = true;
            _busy = false;
        }
    }

    private async System.Threading.Tasks.Task StartRecordingAsync()
    {
        var key = ApiKeyBox.Password.Trim();
        if (key.Length == 0)
        {
            MessageBox.Show(this, "Enter your AssemblyAI API key first.", "No API key",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MicCombo.SelectedItem is not AudioDevice mic || SpeakerCombo.SelectedItem is not AudioDevice speaker)
        {
            MessageBox.Show(this, "Select a microphone and a speaker.", "No devices",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.ApiKey = key;
        _settings.MicDeviceId = mic.Id;
        _settings.SpeakerDeviceId = speaker.Id;
        _settings.Save();

        LiveTranscript.Document.Blocks.Clear();

        var session = new RecordingSession(key, mic, speaker);
        session.TranscriptLine += (spk, _, text) => Dispatcher.Invoke(() => AppendTranscript(spk, text));
        session.Log += m => Dispatcher.Invoke(() => Log(m));
        session.NewSession += (folder, title) => Dispatcher.Invoke(() =>
        {
            AppendDivider($"New meeting - {title}");
            RefreshMeetings();
        });

        await session.StartAsync();
        _session = session;
        SetRecordingUi(true);
    }

    private async System.Threading.Tasks.Task StopRecordingAsync()
    {
        RecordButton.Content = "Finishing...";
        var session = _session!;
        _session = null;
        await session.StopAsync();
        await session.DisposeAsync();
        SetRecordingUi(false);
        RefreshMeetings();
    }

    private void SetRecordingUi(bool recording)
    {
        RecordButton.Content = recording ? "Stop Recording" : "Start Recording";
        RecordButton.Background = new SolidColorBrush(
            (Color)Application.Current.FindResource(recording ? "StopColor" : "StartColor"));
        MicCombo.IsEnabled = SpeakerCombo.IsEnabled = !recording;
    }

    // ── Periodic UI refresh ───────────────────────────────────────────────

    private int _callCheckTick;

    private void OnUiTick(object? sender, EventArgs e)
    {
        MicLevelBar.Value = Math.Min(1.0, (_session?.MicLevel ?? 0f) * 6);
        SpkLevelBar.Value = Math.Min(1.0, (_session?.SpeakerLevel ?? 0f) * 6);

        // Enumerating audio sessions is comparatively costly - once every
        // couple of seconds is plenty for a status label.
        if (++_callCheckTick < 10) return;
        _callCheckTick = 0;

        var inCall = TeamsMonitor.IsInCall();
        var title = inCall ? TeamsMonitor.GetMeetingTitle() : null;
        CallStatus.Text = inCall
            ? (title is null ? "On a Teams call" : $"On a Teams call - {title}")
            : "Not currently on a Teams call";
        CallStatus.Foreground = inCall
            ? new SolidColorBrush((Color)Application.Current.FindResource("InCallColor"))
            : DimBrush;
    }

    // ── Transcript rendering ──────────────────────────────────────────────

    private void AppendTranscript(string speaker, string text)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
        paragraph.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss}] ") { Foreground = DimBrush });
        paragraph.Inlines.Add(new Run($"{speaker}: ")
        {
            Foreground = speaker == "ME" ? MicBrush : SpkBrush,
            FontWeight = FontWeights.SemiBold,
        });
        paragraph.Inlines.Add(new Run(text));
        LiveTranscript.Document.Blocks.Add(paragraph);
        LiveTranscript.ScrollToEnd();
    }

    private void AppendDivider(string label)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 8, 0, 4) };
        paragraph.Inlines.Add(new Run($"-- {label} --") { Foreground = DimBrush, FontStyle = FontStyles.Italic });
        LiveTranscript.Document.Blocks.Add(paragraph);
        LiveTranscript.ScrollToEnd();
    }

    private void Log(string message)
    {
        LogBox.AppendText($"{message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    // ── Meeting history ───────────────────────────────────────────────────

    private void RefreshMeetings()
    {
        var selected = (MeetingList.SelectedItem as Meeting)?.Folder;
        MeetingList.ItemsSource = MeetingStore.GetRecent();
        if (selected != null)
        {
            MeetingList.SelectedItem = MeetingList.Items
                .OfType<Meeting>().FirstOrDefault(m => m.Folder == selected);
        }
    }

    private void OnRefreshMeetings(object sender, RoutedEventArgs e) => RefreshMeetings();

    private void OnMeetingSelected(object sender, SelectionChangedEventArgs e)
    {
        if (MeetingList.SelectedItem is not Meeting meeting)
        {
            ResetViewer();
            return;
        }
        _openMeeting = meeting;
        _openLines = MeetingStore.ReadTranscript(meeting.Path);
        TranscriptList.ItemsSource = _openLines;
        ViewerTitle.Text = meeting.Title;
        OpenFolderButton.Visibility = Visibility.Visible;
        DeleteLinesButton.Visibility = Visibility.Collapsed;

        if (_openLines.Count == 0)
            Log($"{meeting.Folder}: no transcript lines.");
    }

    private void ResetViewer()
    {
        _openMeeting = null;
        _openLines = new List<TranscriptLine>();
        TranscriptList.ItemsSource = null;
        ViewerTitle.Text = "No meeting selected";
        OpenFolderButton.Visibility = Visibility.Collapsed;
        DeleteLinesButton.Visibility = Visibility.Collapsed;
    }

    private void OnTranscriptSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var count = TranscriptList.SelectedItems.Count;
        DeleteLinesButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteLinesButton.Content = count == 1 ? "Delete line" : $"Delete {count} lines";
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_openMeeting == null) return;
        try { Process.Start(new ProcessStartInfo(_openMeeting.Path) { UseShellExecute = true }); }
        catch (Exception ex) { Log($"Could not open folder: {ex.Message}"); }
    }

    private void OnDeleteRecording(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Meeting meeting) return;

        var confirm = MessageBox.Show(this,
            $"Delete this recording?\n\n{meeting.Title}\n{meeting.Detail}\nFolder: {meeting.Folder}\n\n" +
            "It goes to the Recycle Bin, so it can be restored.",
            "Delete recording", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            MeetingStore.DeleteRecording(meeting.Path);
            Log($"Deleted {meeting.Folder} (moved to Recycle Bin).");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not delete {meeting.Folder}:\n\n{ex.Message}",
                "Delete failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (_openMeeting?.Path == meeting.Path) ResetViewer();
        RefreshMeetings();
    }

    private void OnDeleteLines(object sender, RoutedEventArgs e)
    {
        if (_openMeeting == null) return;
        var selected = TranscriptList.SelectedItems.OfType<TranscriptLine>().ToList();
        if (selected.Count == 0) return;

        var preview = string.Join(Environment.NewLine,
            selected.Take(5).Select(l => l.Display.Length > 100 ? l.Display[..97] + "..." : l.Display));
        if (selected.Count > 5) preview += $"{Environment.NewLine}...and {selected.Count - 5} more";

        var heading = selected.Count == 1
            ? "Delete this transcript line?"
            : $"Delete {selected.Count} transcript lines?";
        var confirm = MessageBox.Show(this,
            $"{heading}\n\n{preview}\n\nThis edits the saved transcript and cannot be undone.",
            "Delete lines", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            MeetingStore.DeleteLines(_openMeeting.Path, selected.Select(l => l.SourceIndex));
            Log($"Deleted {selected.Count} line(s) from {_openMeeting.Folder}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not update the transcript:\n\n{ex.Message}",
                "Delete failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Re-read from disk so indices match the edited file.
        _openLines = MeetingStore.ReadTranscript(_openMeeting.Path);
        TranscriptList.ItemsSource = _openLines;
        DeleteLinesButton.Visibility = Visibility.Collapsed;
        RefreshMeetings();
    }

    protected override async void OnClosed(EventArgs e)
    {
        _uiTimer.Stop();
        if (_session != null)
        {
            try { await _session.DisposeAsync(); } catch (Exception) { }
            _session = null;
        }
        base.OnClosed(e);
    }
}

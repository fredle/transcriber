using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
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
    private readonly DispatcherTimer _notesSaveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _meetingNotesSaveTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private RecordingSession? _session;
    private TrayIcon? _tray;
    private bool _busy;
    private bool _exiting;          // set only by Exit on the tray menu
    private bool _warnedAboutTray;  // the "still running" hint is shown once
    private bool _wasInCall;        // to spot the call starting, not merely being on one
    private Meeting? _openMeeting;
    private List<TranscriptLine> _openLines = new();

    // The meeting folder live notes are currently being saved into, or null
    // when nothing is being transcribed.
    private string? _notesFolder;

    // null = show every meeting, "" = unfiled only, else the folder name.
    private string? _selectedFolderKey;

    public MainWindow()
    {
        InitializeComponent();

        ApiKeyBox.Password = _settings.ApiKey;

        // Enumerating audio endpoints and scanning the transcript folders are
        // the slow part of startup. Doing them here would hold the window off
        // the screen; queued at Loaded priority they run once it is painted,
        // so the app appears immediately and fills in a moment later.
        Loaded += OnFirstLoad;

        SetUpTray();

        _uiTimer.Tick += OnUiTick;
        _uiTimer.Start();

        _notesSaveTimer.Tick += (_, _) =>
        {
            _notesSaveTimer.Stop();
            FlushNotes();
        };
        _meetingNotesSaveTimer.Tick += (_, _) =>
        {
            _meetingNotesSaveTimer.Stop();
            FlushMeetingNotes();
        };

        Log($"Transcriptions folder: {MeetingStore.Root}");
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
            Log("No AssemblyAI key set - enter one above and press Save key.");
    }

    /// <summary>
    /// ContentRendered is the first frame actually on screen, so this is the
    /// moment the splash has stopped being useful.
    /// </summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        App.CloseSplash();
    }

    private void OnFirstLoad(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoad;
        // Belt and braces: if a render is never reported, the splash must not
        // be left stranded on top of a usable window.
        Dispatcher.BeginInvoke(new Action(App.CloseSplash), DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                LoadDevices();
                RefreshFolders();
                RefreshMeetings();
            }
            catch (Exception ex)
            {
                Log($"Startup problem: {ex.Message}");
            }
        }), DispatcherPriority.Background);
    }

    // ── Notification area ─────────────────────────────────────────────────

    private void SetUpTray()
    {
        _tray = new TrayIcon(_settings.AutoStartOnCall);
        _tray.ShowRequested += RestoreFromTray;
        _tray.ToggleTranscribeRequested += () => OnToggleRecording(this, new RoutedEventArgs());
        _tray.OpenFolderRequested += () =>
        {
            try { Process.Start(new ProcessStartInfo(MeetingStore.EnsureRoot()) { UseShellExecute = true }); }
            catch (Exception ex) { Log($"Could not open folder: {ex.Message}"); }
        };
        _tray.AutoStartChanged += value =>
        {
            _settings.AutoStartOnCall = value;
            _settings.Save();
            Log(value
                ? "Will start transcribing automatically when a Teams call begins."
                : "Automatic start on a Teams call is off.");
        };
        _tray.ExitRequested += () =>
        {
            _exiting = true;
            Close();
        };
    }

    /// <summary>
    /// Put the window back on screen and in front. Also how a second launch of
    /// the app surfaces this one instead of starting another (see App.OnStartup),
    /// which is why it forces the foreground rather than only calling Activate:
    /// Activate alone leaves a hidden or minimised window flashing in the
    /// taskbar when the request came from another process.
    /// </summary>
    internal void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) SetForegroundWindow(handle);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// Closing the window leaves the app running in the notification area so
    /// it can keep watching for calls; only Exit on the tray menu really quits.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_exiting && _settings.MinimiseToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_warnedAboutTray)
            {
                _warnedAboutTray = true;
                _tray?.Notify("Still running",
                    "Teeline is in the notification area, watching for Teams calls. " +
                    "Use Exit on its menu to quit.");
            }
            return;
        }
        base.OnClosing(e);
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
            MessageBox.Show(this, ex.Message, "Transcribing error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            FlushNotes();   // persist notes into the meeting that just ended
            AppendDivider($"New meeting - {title}");
            _notesFolder = folder;
            LoadNotesInto(folder);
            RefreshMeetings();
            UpdateMeetingNotesEditability();
        });

        await session.StartAsync();
        _session = session;
        _notesFolder = session.Folder;
        LoadNotesInto(session.Folder);
        SetRecordingUi(true);
        UpdateMeetingNotesEditability();
    }

    private async System.Threading.Tasks.Task StopRecordingAsync()
    {
        RecordButton.Content = "Finishing...";
        var session = _session!;
        _session = null;
        await session.StopAsync();
        await session.DisposeAsync();
        FlushNotes();
        _notesFolder = null;
        SetRecordingUi(false);
        RefreshMeetings();
        if (_openMeeting != null) LoadMeetingNotes(_openMeeting);
        UpdateMeetingNotesEditability();
    }

    private void SetRecordingUi(bool recording)
    {
        RecordButton.Content = recording ? "Stop Transcribing" : "Start Transcribing";
        RecordButton.Background = new SolidColorBrush(
            (Color)Application.Current.FindResource(recording ? "StopColor" : "StartColor"));
        MicCombo.IsEnabled = SpeakerCombo.IsEnabled = !recording;
        NotesBox.IsEnabled = recording;
        NotesHint.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        if (!recording) NotesBox.Document.Blocks.Clear();
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

        _tray?.Update(_session != null, inCall, title);
        HandleCallTransition(inCall, title);
        _wasInCall = inCall;
    }

    /// <summary>
    /// React to a call *starting*, which is the moment worth acting on. This
    /// keeps working while the window is hidden, which is the point of living
    /// in the notification area.
    /// </summary>
    private void HandleCallTransition(bool inCall, string? title)
    {
        if (!inCall || _wasInCall || _session != null) return;

        if (_settings.AutoStartOnCall)
        {
            Log("Teams call started - beginning transcription automatically.");
            _tray?.Notify("Transcribing", title is null
                ? "A Teams call started, so transcription has begun."
                : $"Transcribing \"{title}\".");
            OnToggleRecording(this, new RoutedEventArgs());
        }
        else
        {
            // Prompting rather than recording uninvited: starting to record a
            // meeting should stay a deliberate act.
            _tray?.Notify("Teams call detected", title is null
                ? "Click here to start transcribing."
                : $"Click here to start transcribing \"{title}\".",
                onClick: () =>
                {
                    RestoreFromTray();
                    OnToggleRecording(this, new RoutedEventArgs());
                });
        }
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

    // ── Live notes ────────────────────────────────────────────────────────

    private bool _notesMaximized;

    /// <summary>
    /// Collapse everything on the Transcribe tab except Notes, so it fills
    /// almost the whole window - handy for taking notes during a live call
    /// without the devices/levels/transcript panels crowding the view.
    /// </summary>
    private void OnToggleNotesMaximize(object sender, RoutedEventArgs e)
    {
        _notesMaximized = !_notesMaximized;
        var visibility = _notesMaximized ? Visibility.Collapsed : Visibility.Visible;

        CallStatus.Visibility = visibility;
        DevicesRow.Visibility = visibility;
        LevelsRow.Visibility = visibility;
        KeyRow.Visibility = visibility;
        RecordButton.Visibility = visibility;
        LiveTranscriptBorder.Visibility = visibility;
        LiveTranscriptCol.Width = _notesMaximized ? new GridLength(0) : new GridLength(3, GridUnitType.Star);

        NotesMaximizeButton.Content = _notesMaximized ? "Restore" : "Maximize";
    }

    private void OnNotesChanged(object sender, TextChangedEventArgs e)
    {
        if (_notesFolder == null) return;
        _notesSaveTimer.Stop();
        _notesSaveTimer.Start();
    }

    private void LoadNotesInto(string folder)
    {
        NotesBox.Document.Blocks.Clear();
        var rtf = MeetingStore.LoadNotes(folder);
        if (rtf == null) return;
        using var stream = new MemoryStream(rtf);
        new TextRange(NotesBox.Document.ContentStart, NotesBox.Document.ContentEnd)
            .Load(stream, DataFormats.Rtf);
    }

    private void FlushNotes()
    {
        _notesSaveTimer.Stop();
        if (_notesFolder == null) return;
        try
        {
            using var stream = new MemoryStream();
            new TextRange(NotesBox.Document.ContentStart, NotesBox.Document.ContentEnd)
                .Save(stream, DataFormats.Rtf);
            MeetingStore.SaveNotes(_notesFolder, stream.ToArray());
        }
        catch (Exception ex)
        {
            Log($"Could not save notes: {ex.Message}");
        }
    }

    // ── Notes for a meeting selected in Recent Meetings ─────────────────────

    private void OnMeetingNotesChanged(object sender, TextChangedEventArgs e)
    {
        if (_openMeeting == null || !MeetingNotesBox.IsEnabled) return;
        _meetingNotesSaveTimer.Stop();
        _meetingNotesSaveTimer.Start();
    }

    private void LoadMeetingNotes(Meeting meeting)
    {
        MeetingNotesBox.Document.Blocks.Clear();
        var rtf = MeetingStore.LoadNotes(meeting.Path);
        if (rtf == null) return;
        using var stream = new MemoryStream(rtf);
        new TextRange(MeetingNotesBox.Document.ContentStart, MeetingNotesBox.Document.ContentEnd)
            .Load(stream, DataFormats.Rtf);
    }

    private void FlushMeetingNotes()
    {
        _meetingNotesSaveTimer.Stop();
        if (_openMeeting == null || !MeetingNotesBox.IsEnabled) return;
        try
        {
            using var stream = new MemoryStream();
            new TextRange(MeetingNotesBox.Document.ContentStart, MeetingNotesBox.Document.ContentEnd)
                .Save(stream, DataFormats.Rtf);
            MeetingStore.SaveNotes(_openMeeting.Path, stream.ToArray());
        }
        catch (Exception ex)
        {
            Log($"Could not save notes: {ex.Message}");
        }
    }

    /// <summary>
    /// The meeting currently open in the viewer might be the one actively
    /// being recorded, whose notes are already live-edited on the Transcribe
    /// tab; editing the same file from both places at once could clobber
    /// whichever side saves last, so this box goes read-only for it instead.
    /// </summary>
    private void UpdateMeetingNotesEditability()
    {
        if (_openMeeting == null) return;
        var recordingThisOne = _notesFolder != null &&
            string.Equals(Path.GetFullPath(_notesFolder), Path.GetFullPath(_openMeeting.Path),
                StringComparison.OrdinalIgnoreCase);

        MeetingNotesBox.IsEnabled = !recordingThisOne;
        MeetingNotesHint.Visibility = recordingThisOne ? Visibility.Visible : Visibility.Collapsed;
        MeetingNotesHint.Text = "This meeting is currently recording - edit its notes on the Transcribe tab.";
    }

    private static readonly string LogFilePath = Path.Combine(MeetingStore.EnsureRoot(), "app.log");

    private void Log(string message)
    {
        try { File.AppendAllText(LogFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}"); }
        catch (Exception) { }
    }

    // ── Meeting history ───────────────────────────────────────────────────

    private void RefreshMeetings()
    {
        var selected = (MeetingList.SelectedItem as Meeting)?.Folder;
        var all = MeetingStore.GetRecent();
        MeetingList.ItemsSource = _selectedFolderKey == null
            ? all
            : all.Where(m => m.Group == _selectedFolderKey).ToList();
        if (selected != null)
        {
            MeetingList.SelectedItem = MeetingList.Items
                .OfType<Meeting>().FirstOrDefault(m => m.Folder == selected);
        }
    }

    private void OnRefreshMeetings(object sender, RoutedEventArgs e) => RefreshMeetings();

    // ── Folders ───────────────────────────────────────────────────────────

    private sealed class FolderFilterItem
    {
        public required string Label { get; init; }
        public required string? Key { get; init; }   // null = All, "" = Unfiled, else folder name
    }

    private void RefreshFolders()
    {
        var items = new List<FolderFilterItem>
        {
            new() { Label = "All", Key = null },
            new() { Label = "Unfiled", Key = "" },
        };
        items.AddRange(MeetingStore.GetFolders().Select(f => new FolderFilterItem { Label = f, Key = f }));

        FolderList.ItemsSource = items;
        FolderList.SelectedItem = items.FirstOrDefault(i => i.Key == _selectedFolderKey) ?? items[0];
    }

    private void OnFolderSelected(object sender, SelectionChangedEventArgs e)
    {
        _selectedFolderKey = (FolderList.SelectedItem as FolderFilterItem)?.Key;
        RefreshMeetings();
    }

    private void OnCreateFolder(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Prompt(this, "New folder", "Folder name:");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            MeetingStore.CreateFolder(name);
            Log($"Created folder \"{name.Trim()}\".");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not create folder",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        RefreshFolders();
    }

    private void OnMoveMeetingClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Meeting meeting) return;
        var button = (Button)sender;

        var menu = new ContextMenu();

        var unfiled = new MenuItem { Header = "Unfiled", IsEnabled = meeting.Group.Length != 0 };
        unfiled.Click += (_, _) => MoveMeetingTo(meeting, "");
        menu.Items.Add(unfiled);

        var folders = MeetingStore.GetFolders();
        if (folders.Count > 0) menu.Items.Add(new Separator());
        foreach (var folder in folders)
        {
            var item = new MenuItem { Header = folder, IsEnabled = meeting.Group != folder };
            item.Click += (_, _) => MoveMeetingTo(meeting, folder);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var newFolder = new MenuItem { Header = "New folder..." };
        newFolder.Click += (_, _) => OnMoveToNewFolder(meeting);
        menu.Items.Add(newFolder);

        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void OnMoveToNewFolder(Meeting meeting)
    {
        var name = InputDialog.Prompt(this, "New folder", "Folder name:");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            MeetingStore.CreateFolder(name);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not create folder",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        MoveMeetingTo(meeting, name.Trim());
    }

    private void MoveMeetingTo(Meeting meeting, string target)
    {
        try
        {
            MeetingStore.MoveMeeting(meeting.Path, target);
            Log($"Moved {meeting.Folder} to {(target.Length == 0 ? "Unfiled" : target)}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not move {meeting.Folder}:\n\n{ex.Message}",
                "Move failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        RefreshFolders();
        RefreshMeetings();
    }

    private void OnMeetingSelected(object sender, SelectionChangedEventArgs e)
    {
        FlushMeetingNotes();   // persist any pending edits before switching meetings

        if (MeetingList.SelectedItem is not Meeting meeting)
        {
            ResetViewer();
            return;
        }
        _openMeeting = meeting;
        ViewerTitle.Text = meeting.Title;
        OpenFolderButton.Visibility = Visibility.Visible;
        SpeakersButton.Visibility = Visibility.Visible;
        AskButton.Visibility = Visibility.Visible;
        DeleteLinesButton.Visibility = Visibility.Collapsed;
        ChangeSpeakerButton.Visibility = Visibility.Collapsed;

        try
        {
            _openLines = MeetingStore.ReadTranscript(meeting.Path);
        }
        catch (Exception ex)
        {
            // Never let a read problem escape into WPF's event plumbing: an
            // unhandled exception here terminates the process, which would
            // abandon a recording in progress.
            _openLines = new List<TranscriptLine>();
            Log($"Could not read {meeting.Folder}: {ex.Message}");
        }
        TranscriptList.ItemsSource = _openLines;

        if (_openLines.Count == 0)
            Log($"{meeting.Folder}: no transcript lines yet.");

        MeetingNotesHint.Visibility = Visibility.Collapsed;
        LoadMeetingNotes(meeting);
        UpdateMeetingNotesEditability();
    }

    /// <summary>
    /// Clear the viewer. <paramref name="flushNotes"/> is false when the open
    /// meeting's folder is going away: saving notes into it at that point
    /// would recreate the folder that was just deleted.
    /// </summary>
    private void ResetViewer(bool flushNotes = true)
    {
        if (flushNotes) FlushMeetingNotes();
        else _meetingNotesSaveTimer.Stop();
        _openMeeting = null;
        _openLines = new List<TranscriptLine>();
        TranscriptList.ItemsSource = null;
        ViewerTitle.Text = "No meeting selected";
        OpenFolderButton.Visibility = Visibility.Collapsed;
        SpeakersButton.Visibility = Visibility.Collapsed;
        AskButton.Visibility = Visibility.Collapsed;
        DeleteLinesButton.Visibility = Visibility.Collapsed;
        ChangeSpeakerButton.Visibility = Visibility.Collapsed;
        MeetingNotesBox.Document.Blocks.Clear();
        MeetingNotesBox.IsEnabled = false;
        MeetingNotesHint.Text = "Select a meeting to view or edit its notes.";
        MeetingNotesHint.Visibility = Visibility.Visible;
    }

    private void OnTranscriptSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var count = TranscriptList.SelectedItems.Count;
        DeleteLinesButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteLinesButton.Content = count == 1 ? "Delete line" : $"Delete {count} lines";
        ChangeSpeakerButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
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

        // The live recording holds its transcript open, so a shell delete of it
        // fails anyway - and the session would keep writing into a folder in
        // the Recycle Bin.
        if (_session != null && SamePath(_session.Folder, meeting.Path))
        {
            MessageBox.Show(this,
                "This meeting is still being recorded. Stop transcribing first, then delete it.",
                "Recording in progress", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"Delete this transcription?\n\n{meeting.Title}\n{meeting.Detail}\nFolder: {meeting.Folder}\n\n" +
            "It goes to the Recycle Bin, so it can be restored.",
            "Delete transcription", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
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

        if (_openMeeting != null && SamePath(_openMeeting.Path, meeting.Path))
            ResetViewer(flushNotes: false);
        RefreshMeetings();
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

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
        ReloadOpenTranscript();
        DeleteLinesButton.Visibility = Visibility.Collapsed;
        RefreshMeetings();
    }

    /// <summary>Re-read the open meeting's transcript, e.g. after edits change source indices or speaker names.</summary>
    private void ReloadOpenTranscript()
    {
        if (_openMeeting == null) return;
        _openLines = MeetingStore.ReadTranscript(_openMeeting.Path);
        TranscriptList.ItemsSource = _openLines;
    }

    private void OnManageSpeakers(object sender, RoutedEventArgs e)
    {
        if (_openMeeting == null) return;

        var keys = _openLines.Select(l => l.SpeakerKey).Distinct().ToList();
        if (keys.Count == 0)
        {
            MessageBox.Show(this, "This meeting has no transcript lines yet.", "No speakers",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var names = MeetingStore.LoadSpeakerNames(_openMeeting.Path);
        var rows = keys
            .OrderBy(k => k == "ME" ? "" : k)
            .Select(k => new SpeakerNameRow
            {
                Key = k,
                Label = DefaultSpeakerLabel(k),
                Name = names.TryGetValue(k, out var n) ? n : "",
            })
            .ToList();

        var dialog = new SpeakerNamesDialog(this, rows);
        if (dialog.ShowDialog() != true) return;

        try
        {
            MeetingStore.SaveSpeakerNames(_openMeeting.Path, dialog.Result);
            Log($"Updated speaker names for {_openMeeting.Folder}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save speaker names:\n\n{ex.Message}", "Save failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        ReloadOpenTranscript();
    }

    private void OnAskAboutMeeting(object sender, RoutedEventArgs e)
    {
        if (_openMeeting == null) return;
        if (_openLines.Count == 0)
        {
            MessageBox.Show(this, "This meeting has no transcript lines yet.", "Nothing to ask about",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // A plain, non-owned Show(): asking questions can take a while per
        // answer, and the user should be free to keep browsing other
        // meetings while one is in flight rather than being blocked by it.
        var transcriptText = string.Join(Environment.NewLine, _openLines.Select(l => l.Display));
        var dialog = new AskMeetingDialog(this, _openMeeting.Title, transcriptText);
        dialog.Show();
    }

    private void OnChangeSpeaker(object sender, RoutedEventArgs e)
    {
        if (_openMeeting == null) return;
        var selected = TranscriptList.SelectedItems.OfType<TranscriptLine>().ToList();
        if (selected.Count == 0) return;

        var names = MeetingStore.LoadSpeakerNames(_openMeeting.Path);
        var options = _openLines
            .Select(l => l.SpeakerKey)
            .Distinct()
            .OrderBy(k => k == "ME" ? "" : k)
            .Select(k => new SpeakerOption
            {
                Key = k,
                DisplayName = names.TryGetValue(k, out var n) ? n : DefaultSpeakerLabel(k),
            })
            .ToList();

        var dialog = new ChangeSpeakerDialog(this, options);
        if (dialog.ShowDialog() != true) return;

        string channel;
        string? label;
        if (dialog.ResultKey != null)
        {
            channel = dialog.ResultKey == "ME" ? "ME" : "OTHER";
            label = dialog.ResultKey == "ME" ? null : dialog.ResultKey;
        }
        else
        {
            // A brand-new speaker: allocate a fresh diarisation letter and
            // remember its name straight away.
            var used = _openLines
                .Select(l => l.SpeakerKey)
                .Where(k => k != "ME" && k != "PENDING")
                .ToHashSet();
            label = Enumerable.Range('A', 26).Select(c => ((char)c).ToString())
                .FirstOrDefault(l => !used.Contains(l))
                ?? Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
            channel = "OTHER";

            names[label] = dialog.NewSpeakerName!;
            try
            {
                MeetingStore.SaveSpeakerNames(_openMeeting.Path, names);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not save the new speaker's name:\n\n{ex.Message}", "Save failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        try
        {
            MeetingStore.SetLineSpeaker(_openMeeting.Path, selected.Select(l => l.SourceIndex), channel, label);
            Log($"Reassigned {selected.Count} line(s) in {_openMeeting.Folder}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not update the transcript:\n\n{ex.Message}", "Update failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        ReloadOpenTranscript();
        ChangeSpeakerButton.Visibility = Visibility.Collapsed;
    }

    private static string DefaultSpeakerLabel(string speakerKey) => speakerKey switch
    {
        "ME" => "Me",
        "PENDING" => "Other (unlabelled)",
        _ => $"Speaker {speakerKey}",
    };

    protected override async void OnClosed(EventArgs e)
    {
        _uiTimer.Stop();
        _tray?.Dispose();
        _tray = null;
        FlushNotes();
        FlushMeetingNotes();
        if (_session != null)
        {
            try { await _session.DisposeAsync(); } catch (Exception) { }
            _session = null;
        }
        base.OnClosed(e);
    }
}

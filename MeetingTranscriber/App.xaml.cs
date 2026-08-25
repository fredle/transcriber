using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using MeetingTranscriber.Services;

namespace MeetingTranscriber;

public partial class App : Application
{
    private static readonly string CrashLog = Path.Combine(ResolveAppDataDir(), "crash.log");

    /// <summary>
    /// %AppData%\Teeline, migrating an older %AppData%\Kettle or, before that,
    /// %AppData%\MeetingTranscriber folder if one exists - the app has been
    /// renamed twice, so both are checked in order.
    /// </summary>
    private static string ResolveAppDataDir()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var current = Path.Combine(appData, "Teeline");
        LegacyMigration.MigrateFolder(Path.Combine(appData, "Kettle"), current);
        LegacyMigration.MigrateFolder(Path.Combine(appData, "MeetingTranscriber"), current);
        return current;
    }

    // Per-session names: two users signed in at once each get their own
    // instance, but one user cannot end up with two. Deliberately left as
    // "MeetingTranscriber" even after the rename to Kettle: an old build
    // still running under its original name uses this same identifier, so
    // keeping it lets a freshly-launched Kettle detect and wake it instead of
    // both fighting over the same audio devices and transcript files.
    private const string InstanceMutexName = @"Local\MeetingTranscriber.SingleInstance";
    private const string ShowSignalName = @"Local\MeetingTranscriber.ShowWindow";

    private SplashScreen? _splash;
    private bool _splashClosed;
    private Mutex? _instanceLock;
    private EventWaitHandle? _showSignal;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The app lives in the notification area, so launching it again -
        // from the Start menu, a shortcut, or its folder - almost always means
        // "show me the one that is already running", not "start a second one".
        // A second process would fight the first for the audio devices and the
        // transcript files, so it wakes the original and gets out of the way.
        _instanceLock = new Mutex(initiallyOwned: true, InstanceMutexName, out var isOnlyInstance);
        if (!isOnlyInstance)
        {
            WakeRunningInstance();
            Shutdown();
            return;
        }
        ListenForShowRequests();

        // Shown before anything else so there is instant feedback, and closed
        // by the main window the moment it has actually rendered.
        try
        {
            _splash = new SplashScreen("assets/splash.png");
            _splash.Show(autoClose: false, topMost: true);
        }
        catch (Exception)
        {
            _splash = null;   // never let the splash stop the app starting
        }

        // A recording can be in progress, so an unexpected error must never be
        // allowed to take the process down and lose the session. UI-thread
        // faults are reported and swallowed; anything already written to the
        // transcript is safe on disk because every line is flushed as it lands.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Record(args.ExceptionObject as Exception, "background");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Record(args.Exception, "task");
            args.SetObserved();
        };

        // Shown here rather than through StartupUri: the single-instance check
        // above has to settle before any window is built.
        _window = new MainWindow();
        MainWindow = _window;
        _window.Show();
    }

    /// <summary>
    /// Watch for another launch asking us to come to the front. The handle is
    /// created by the surviving instance only, so a later launch that cannot
    /// open it knows nothing is running and starts normally.
    /// </summary>
    private void ListenForShowRequests()
    {
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        var listener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!_showSignal.WaitOne()) continue;
                    Dispatcher.BeginInvoke(new Action(() => _window?.RestoreFromTray()));
                }
                catch (Exception)
                {
                    return;   // shutting down, or the handle has gone
                }
            }
        })
        {
            IsBackground = true,
            Name = "single-instance-listener",
        };
        listener.Start();
    }

    /// <summary>
    /// Bring the already-running instance forward, then let this process die.
    /// </summary>
    private static void WakeRunningInstance()
    {
        try
        {
            // We are the foreground process for the moment, so hand that right
            // over: without it the shell blocks the other instance's window
            // from rising and just flashes its taskbar button instead.
            AllowSetForegroundWindow(ASFW_ANY);
            using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Running, but not yet listening - nothing useful to do.
        }
        catch (UnauthorizedAccessException) { }
    }

    private const uint ASFW_ANY = 0xFFFFFFFF;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    /// <summary>
    /// Dismiss the splash with no fade. Safe to call more than once, and from
    /// a fallback timer in case the window never reports a render.
    /// </summary>
    public static void CloseSplash()
    {
        if (Current is not App app || app._splashClosed) return;
        app._splashClosed = true;
        try { app._splash?.Close(TimeSpan.Zero); }
        catch (Exception) { }
        app._splash = null;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Record(e.Exception, "ui");
        e.Handled = true;
        MessageBox.Show(
            $"Something went wrong, but the app is still running and any recording is still going.\n\n" +
            $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\nLogged to:\n{CrashLog}",
            "Teeline", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void Record(Exception? ex, string origin)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLog)!);
            File.AppendAllText(CrashLog,
                $"{DateTime.Now:o} [{origin}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { /* nothing useful left to do */ }
    }
}

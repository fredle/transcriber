using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace MeetingTranscriber;

public partial class App : Application
{
    private static readonly string CrashLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MeetingTranscriber", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Record(e.Exception, "ui");
        e.Handled = true;
        MessageBox.Show(
            $"Something went wrong, but the app is still running and any recording is still going.\n\n" +
            $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\nLogged to:\n{CrashLog}",
            "Meeting Transcriber", MessageBoxButton.OK, MessageBoxImage.Warning);
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

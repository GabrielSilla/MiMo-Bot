using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Brobot.Sender;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
// UseWindowsForms (for the tray icon) puts System.Windows.Forms.Application
// in scope alongside System.Windows.Application, so "Application" alone is
// ambiguous — spelled out fully here.
public partial class App : System.Windows.Application
{
    // Now that the installer offers both a Start Menu shortcut and a
    // "start with Windows" shortcut, a second launch (someone double-clicking
    // the shortcut while the tray copy is already running) is a real scenario,
    // not just a theoretical one — a second AiThoughtsListener would fail to
    // bind AiThoughtsPort, and two tray icons would be genuinely confusing.
    // Kept as a field (never released/disposed) purely so the handle stays
    // alive for the process's lifetime instead of being GC'd; the OS reclaims
    // it on exit either way.
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // initialOwner: true means *this* call takes ownership if it's the one
        // that creates the mutex; createdNew tells us whether that happened, or
        // whether another running instance already owns it. WaitOne() would be
        // the wrong check here — an owning thread's own WaitOne on its already-
        // held mutex just re-enters and returns true, which would defeat this
        // check entirely.
        _singleInstanceMutex = new Mutex(true, "Brobot.Sender.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            // The app already starts with no window shown (see below), so a
            // second launch quietly stepping aside matches that same
            // no-window-by-default behavior rather than popping up a dialog.
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // Off-UI-thread failures (fire-and-forget tasks, WinRT callbacks)
        // never reach the dispatcher handler above — log them too.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                LogError("AppDomain", ex);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogError("Task", args.Exception);
            args.SetObserved();
        };
        SessionEnding += OnSessionEnding;

        // Must happen before MainWindow is constructed: its XAML resolves
        // brush DynamicResources against Application.Resources as soon as
        // it's parsed, so the theme needs to already be merged in by then.
        ThemeManager.Apply(SenderSettings.Load().Theme);

        // Deliberately not shown: the app lives in the tray until its icon
        // is clicked. Assigning MainWindow (rather than just letting it be
        // garbage-collected) is what keeps ShutdownMode.OnExplicitShutdown
        // from exiting immediately with no window ever open.
        MainWindow = new MainWindow();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A malformed line from Core, or a connection hiccup, should never take
        // the whole app down — surface it and keep the window open. The dialog
        // only has room for the message, so the full stack trace goes to
        // ErrorLogPath for diagnosing it afterwards.
        LogError("Dispatcher", e.Exception);
        System.Windows.MessageBox.Show($"Erro inesperado: {e.Exception.Message}\n\nDetalhes em {ErrorLogPath}", "MiMo",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private static readonly string ErrorLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Brobot", "sender-errors.log");

    // Same size-cap-then-start-over approach as MainWindow's ai-events.log.
    private const long MaxErrorLogBytes = 1024 * 1024;

    internal static void LogError(string source, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLogPath)!);
            var info = new FileInfo(ErrorLogPath);
            if (info.Exists && info.Length > MaxErrorLogBytes)
            {
                File.Delete(ErrorLogPath);
            }
            File.AppendAllText(ErrorLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ({source}) {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never be the thing that crashes the app.
        }
    }

    // The one termination path the tray's own "Sair" (MainWindow.
    // ExitApplication) never sees: Windows logging the user off or shutting
    // down, with nobody having clicked anything in this app. Not cancelled —
    // there's no reason for this app to block a shutdown the user asked for,
    // only to say goodbye to MiMo first if it's actually reachable right now.
    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        (MainWindow as MainWindow)?.SendFarewellForShutdown();
    }
}

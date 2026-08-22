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
        // the whole app down — surface it and keep the window open.
        System.Windows.MessageBox.Show($"Erro inesperado: {e.Exception.Message}", "MiMo",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}

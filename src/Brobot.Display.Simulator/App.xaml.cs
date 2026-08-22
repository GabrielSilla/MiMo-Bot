using System.Windows;
using System.Windows.Threading;

namespace Brobot.Display.Simulator;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A malformed line from the device, or a serial hiccup, should never take
        // the whole simulator down — surface it and keep the window open.
        MessageBox.Show($"Erro inesperado: {e.Exception.Message}", "MiMo Virtual Display",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}

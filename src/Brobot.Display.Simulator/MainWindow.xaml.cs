using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Brobot.Display.Abstractions;

namespace Brobot.Display.Simulator;

/// <summary>
/// Interaction logic for MainWindow.xaml.
/// Hosts the virtual display, its scale/clear controls, a debug panel, and
/// the serial connection to a real Brobot Core (Arduino) running in vscreen mode.
/// Owns the render loop; drawing itself is delegated to <see cref="DisplayTestPattern"/>
/// (internal demo) or to <see cref="SerialDisplayBridge"/> (real device).
/// </summary>
public partial class MainWindow : Window
{
    private static readonly int[] AvailableScales = { 1, 2, 3, 4, 5, 6, 8 };
    private const int DefaultScale = 3;

    private readonly SimulatorDisplay _display = new();
    private readonly SerialDisplayBridge _serialBridge;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private int _scale = DefaultScale;
    private bool _demoRunning = true;
    private bool _tcpMode;

    private int _lastPresentCount;
    private double _lastFpsSampleTime;
    private double _currentFps;

    public MainWindow()
    {
        InitializeComponent();

        _serialBridge = new SerialDisplayBridge(_display, Dispatcher);

        DisplayImage.Source = _display.Bitmap;

        ScaleComboBox.ItemsSource = AvailableScales;
        ScaleComboBox.SelectedItem = DefaultScale;
        ApplyScale(DefaultScale);

        RefreshPortList();

        CompositionTarget.Rendering += OnRendering;
        Closed += (_, _) => _serialBridge.Dispose();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;

        if (_demoRunning)
        {
            _display.ResetDrawOperationCount();
            DisplayTestPattern.Render(_display, now);
            _display.Present();
        }

        UpdateFps(now);
        UpdateConnectionStatus();
        UpdateDebugPanel();
    }

    /// <summary>
    /// Refreshes TCP status text every frame instead of via a callback from
    /// the background connect/read thread — simpler, and this already runs
    /// every frame for FPS tracking. Reflects the bridge auto-retrying the
    /// connection after Core restarts, so the UI doesn't need its own
    /// reconnect handling.
    /// </summary>
    private void UpdateConnectionStatus()
    {
        if (!_tcpMode)
        {
            return;
        }

        TcpStatusText.Text = _serialBridge.IsConnected
            ? "Conectado (TCP)"
            : $"Conectando a {TcpHostTextBox.Text}:{TcpPortTextBox.Text}...";
    }

    private void UpdateFps(double now)
    {
        double elapsedSinceSample = now - _lastFpsSampleTime;
        if (elapsedSinceSample >= 0.5)
        {
            int presentedSinceSample = _display.PresentCount - _lastPresentCount;
            _currentFps = presentedSinceSample / elapsedSinceSample;
            _lastPresentCount = _display.PresentCount;
            _lastFpsSampleTime = now;
        }
    }

    private void UpdateDebugPanel()
    {
        ResolutionText.Text = $"Resolução: {SimulatorDisplay.LogicalWidth} x {SimulatorDisplay.LogicalHeight}";
        ScaleText.Text = $"Escala: {_scale}x";
        FpsText.Text = $"FPS: {_currentFps:0.0}";
        DrawOpsText.Text = $"Operações de desenho: {_display.DrawOperationCount}";
        SourceText.Text = _tcpMode
            ? (_serialBridge.IsConnected ? "Fonte: BrobotCore nativo (TCP)" : "Fonte: aguardando BrobotCore nativo (TCP)")
            : _serialBridge.IsConnected ? "Fonte: Arduino (Serial)" : "Fonte: Demonstração interna";
    }

    private void ApplyScale(int scale)
    {
        _scale = scale;
        DisplayImage.Width = SimulatorDisplay.LogicalWidth * scale;
        DisplayImage.Height = SimulatorDisplay.LogicalHeight * scale;
    }

    private void RefreshPortList()
    {
        string? selected = PortComboBox.SelectedItem as string;
        string[] ports = SerialDisplayBridge.GetAvailablePortNames();
        PortComboBox.ItemsSource = ports;
        PortComboBox.SelectedItem = ports.Contains(selected) ? selected : ports.FirstOrDefault();
    }

    private void ScaleComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ScaleComboBox.SelectedItem is int scale)
        {
            ApplyScale(scale);
        }
    }

    private void DemoCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        _demoRunning = DemoCheckBox.IsChecked == true;
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        DemoCheckBox.IsChecked = false;
        _display.ResetDrawOperationCount();
        _display.Clear(DisplayColor.Black);
        _display.Present();
        UpdateDebugPanel();
    }

    private void RefreshPortsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshPortList();
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_serialBridge.IsConnected)
        {
            _serialBridge.Disconnect();
            ConnectButton.Content = "Conectar";
            SerialStatusText.Text = "Desconectado";
            DemoCheckBox.IsEnabled = true;
            CommandTextBox.IsEnabled = false;
            SendCommandButton.IsEnabled = false;
            SetTcpControlsEnabled(true);
            return;
        }

        if (PortComboBox.SelectedItem is not string portName)
        {
            SerialStatusText.Text = "Nenhuma porta selecionada";
            return;
        }

        try
        {
            _serialBridge.Connect(portName);
            ConnectButton.Content = "Desconectar";
            SerialStatusText.Text = $"Conectado a {portName}";

            DemoCheckBox.IsChecked = false;
            DemoCheckBox.IsEnabled = false;
            _demoRunning = false;

            CommandTextBox.IsEnabled = true;
            SendCommandButton.IsEnabled = true;
            SetTcpControlsEnabled(false);
        }
        catch (Exception ex)
        {
            // Opening a real COM port can fail in more ways than the .NET docs
            // enumerate (driver quirks, the device mid-reset after a firmware
            // upload, etc.) — never let a failed connection attempt take the
            // whole app down.
            SerialStatusText.Text = $"Falha ao conectar: {ex.Message}";
        }
    }

    private void TcpConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tcpMode)
        {
            _serialBridge.Disconnect();
            _tcpMode = false;
            TcpConnectButton.Content = "Conectar (dev, sem Arduino)";
            TcpStatusText.Text = "Desconectado";
            DemoCheckBox.IsEnabled = true;
            CommandTextBox.IsEnabled = false;
            SendCommandButton.IsEnabled = false;
            SetSerialControlsEnabled(true);
            return;
        }

        string host = TcpHostTextBox.Text.Trim();
        if (host.Length == 0 || !int.TryParse(TcpPortTextBox.Text, out int port) || port is <= 0 or > 65535)
        {
            TcpStatusText.Text = "Host/porta inválidos";
            return;
        }

        _serialBridge.ConnectTcp(host, port);
        _tcpMode = true;
        TcpConnectButton.Content = "Desconectar";
        TcpStatusText.Text = $"Conectando a {host}:{port}...";

        DemoCheckBox.IsChecked = false;
        DemoCheckBox.IsEnabled = false;
        _demoRunning = false;

        CommandTextBox.IsEnabled = true;
        SendCommandButton.IsEnabled = true;
        SetSerialControlsEnabled(false);
    }

    private void SetTcpControlsEnabled(bool enabled)
    {
        TcpConnectButton.IsEnabled = enabled;
        TcpHostTextBox.IsEnabled = enabled;
        TcpPortTextBox.IsEnabled = enabled;
    }

    private void SetSerialControlsEnabled(bool enabled)
    {
        ConnectButton.IsEnabled = enabled;
        PortComboBox.IsEnabled = enabled;
        RefreshPortsButton.IsEnabled = enabled;
    }

    private void SendCommandButton_Click(object sender, RoutedEventArgs e)
    {
        SendCommand();
    }

    private void CommandTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            SendCommand();
        }
    }

    private void SendCommand()
    {
        string command = CommandTextBox.Text.Trim();
        if (command.Length == 0 || !_serialBridge.IsConnected)
        {
            return;
        }

        _serialBridge.SendCommand(command);
        CommandTextBox.Clear();
    }
}

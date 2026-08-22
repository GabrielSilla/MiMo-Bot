using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Brobot.Connection;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Brobot.Sender;

/// <summary>
/// The end-user-facing app for a Brobot someone has actually assembled —
/// branded "MiMo" to whoever's looking at it, even though the code/project
/// underneath keeps the Brobot name throughout. Runs quietly in the system
/// tray, and when opened shows only a checklist of what to send Brobot,
/// plus the Conexão card (MiMo's IP address on the local network) — Core is
/// only ever reachable over WiFi from this app now, not Serial/USB (see
/// BrobotConnection.cs's note on the ESP32-C3 SuperMini's native USB-CDC
/// port hanging .NET's SerialPort); Brobot.Display.Simulator still supports
/// Serial for a real Arduino/ESP32, this app just doesn't need it.
///
/// Closing the window (the X button) hides it back to the tray instead of
/// exiting — this is meant to run for the whole session. Only the tray
/// icon's "Sair" truly quits.
/// </summary>
public partial class MainWindow : Window
{
    // Local port the AI-thoughts bridge listens on for hook events (see
    // AiThoughtsListener). Fixed rather than user-configurable for now —
    // there's only one consumer (a Claude Code hook command) and nothing else
    // on the machine should be competing for it.
    private const int AiThoughtsPort = 5591;

    private readonly BrobotConnection _connection;
    private Forms.NotifyIcon? _trayIcon;

    private WindowsMediaMonitor? _mediaMonitor;
    private bool _mediaFaceActive;

    private GameMonitor? _gameMonitor;
    private bool _gameFaceActive;

    private WeatherMonitor? _weatherMonitor;
    private DispatcherTimer? _clockTimer;

    private AiThoughtsListener? _aiThoughtsListener;
    private bool _aiThoughtFaceActive;

    private readonly DispatcherTimer _connectionStatusTimer;

    public MainWindow()
    {
        InitializeComponent();

        _connection = new BrobotConnection(Dispatcher);

        SetupTrayIcon();
        Closing += MainWindow_Closing;

        _connectionStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _connectionStatusTimer.Tick += (_, _) => UpdateConnectionStatus();
        _connectionStatusTimer.Start();

        RestoreSettings();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    /// <summary>
    /// The single source of truth for both the status text and the button's
    /// label/enabled-state — this used to only touch the status text, while
    /// the button's Content was set optimistically by whichever code path
    /// last called Connect/Disconnect and never corrected afterward. If the
    /// connection dropped on its own (the actual TCP session never reached
    /// Core, or Core closed it) the button was left stuck reading
    /// "Desconectar" while IsConnected was already false — clicking it then
    /// hit the wrong branch below (tried to reconnect instead of
    /// disconnecting anything), which read as "the button doesn't work".
    /// This was a real bug, fixed once.
    /// </summary>
    private void UpdateConnectionStatus()
    {
        bool connected = _connection.IsConnected;
        bool connecting = _connection.IsConnectingTcp;

        ConnectionStatusText.Text = connected ? "Conectado" : (connecting ? "Conectando..." : "Desconectado");

        // While connecting (retrying), the button still reads "Desconectar" —
        // clicking it calls Disconnect(), which cancels the retry loop, same
        // as it would cancel an actual live connection.
        bool hasSomethingToDisconnect = connected || connecting;
        ConnectionButton.Content = hasSomethingToDisconnect ? "Desconectar" : "Conectar";
        ConnectionAddressTextBox.IsEnabled = !hasSomethingToDisconnect;
    }

    private void ConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_connection.IsConnected || _connection.IsConnectingTcp)
        {
            _connection.Disconnect();
            UpdateConnectionStatus();
            return;
        }

        if (!TryParseAddress(ConnectionAddressTextBox.Text, out string host, out int port))
        {
            ConnectionStatusText.Text = "Endereço inválido (formato IP:porta)";
            return;
        }

        // ConnectTcp retries on its own every 500ms until it reaches Core or
        // Disconnect() is called — never throws synchronously, so nothing is
        // set optimistically here; UpdateConnectionStatus's own polling (see
        // above) is what actually reflects the real state, including this
        // new attempt's "Conectando..." phase.
        _connection.ConnectTcp(host, port);
        UpdateConnectionStatus();
    }

    /// <summary>Splits "host:port" from a single field — on the last ':' rather than the first, so a literal IPv6 address wouldn't break this if one's ever typed here.</summary>
    private static bool TryParseAddress(string text, out string host, out int port)
    {
        host = "";
        port = 0;

        int separatorIndex = text.LastIndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == text.Length - 1)
        {
            return false;
        }

        host = text[..separatorIndex].Trim();
        return host.Length > 0
            && int.TryParse(text[(separatorIndex + 1)..].Trim(), out port)
            && port is > 0 and <= 65535;
    }

    private void HoraCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (HoraCheckBox.IsChecked == true)
        {
            // TIME has no "changed" event to react to, it's just pushed on a
            // fixed cadence — every second, not every minute, so the clock
            // doesn't sit up to 59s stale right after being turned on.
            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (_, _) => SendClock();
            _clockTimer.Start();
            SendClock();
        }
        else
        {
            _clockTimer?.Stop();
            _clockTimer = null;
            _connection.SendCommand("TIME");
        }
    }

    private void SendClock() => _connection.SendCommand($"TIME {DateTime.Now:HH:mm}");

    private void ClimaCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (ClimaCheckBox.IsChecked == true)
        {
            _weatherMonitor = new WeatherMonitor();
            _weatherMonitor.WeatherUpdated += OnWeatherUpdated;
            _weatherMonitor.StatusChanged += OnWeatherStatusChanged;
            _weatherMonitor.Start();
        }
        else
        {
            _weatherMonitor?.Dispose();
            _weatherMonitor = null;
            ClimaStatusText.Text = string.Empty;
            _connection.SendCommand("WEATHER");
        }
    }

    private void OnWeatherUpdated(WeatherReading? reading)
    {
        // WeatherMonitor raises this off its own background loop, not the UI thread.
        Dispatcher.Invoke(() =>
        {
            if (reading == null)
            {
                return; // StatusChanged already reported the failure; leave the last-good badge showing
            }

            _connection.SendCommand($"WEATHER {reading.TempC} {reading.CoreConditionName}");
        });
    }

    private void OnWeatherStatusChanged(string status)
    {
        Dispatcher.Invoke(() => ClimaStatusText.Text = status);
    }

    private async void MediaCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (MediaCheckBox.IsChecked == true)
        {
            _mediaMonitor = new WindowsMediaMonitor();
            _mediaMonitor.NowPlayingChanged += OnNowPlayingChanged;
            try
            {
                await _mediaMonitor.StartAsync();
                MediaStatusText.Text = "Aguardando algo tocar...";
            }
            catch (Exception ex)
            {
                MediaStatusText.Text = $"Falha ao observar mídia: {ex.Message}";
                MediaCheckBox.IsChecked = false;
            }
        }
        else
        {
            _mediaMonitor?.Dispose();
            _mediaMonitor = null;
            MediaStatusText.Text = string.Empty;
            ClearMediaFaceIfActive();
        }
    }

    private void OnNowPlayingChanged(NowPlaying? nowPlaying)
    {
        // WindowsMediaMonitor raises this off the WinRT event thread, not the UI thread.
        Dispatcher.Invoke(() =>
        {
            if (nowPlaying == null)
            {
                MediaStatusText.Text = "Nada tocando";
                ClearMediaFaceIfActive();
                return;
            }

            MediaStatusText.Text = $"{nowPlaying.Artist} - {nowPlaying.Title}";
            string face = nowPlaying.IsLikelyAudioOnly ? "MUSIC" : "WATCHING";
            _connection.SendCommand($"FACE {face}");
            _connection.SendCommand($"MSG {nowPlaying.Artist} - {nowPlaying.Title}");
            _mediaFaceActive = true;
        });
    }

    /// <summary>
    /// FACE MUSIC/WATCHING are "sticky" on Core — they hold until cleared,
    /// unlike most expressions which auto-revert after a few seconds. So
    /// when media stops (or the checkbox is unchecked) while one of them is
    /// showing, it has to be explicitly cleared or Brobot would be stuck
    /// dancing/watching forever. Sends FACE IDLE, not FACE NEUTRAL — IDLE
    /// clears only this background/media-or-game expression on Core, so it
    /// can't stomp an unrelated AI message/notification that happens to be
    /// showing at the same time (see PROTOCOL.md's FACE priority notes).
    /// </summary>
    private void ClearMediaFaceIfActive()
    {
        if (!_mediaFaceActive)
        {
            return;
        }

        _connection.SendCommand("FACE IDLE");
        _connection.SendCommand("MSG");
        _mediaFaceActive = false;
    }

    private void GameCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (GameCheckBox.IsChecked == true)
        {
            // FACE PLAYING is sticky on Core, and this app has no way to ask
            // Core what it's currently showing — a freshly started monitor
            // only knows about changes *it* detects from here on, so if a
            // previous session left Core stuck (e.g. crashed mid-detection),
            // nothing would ever notice it needs clearing. Force a known
            // baseline every time monitoring starts, rather than trusting
            // in-memory state that resets on every launch. FACE IDLE, not
            // FACE NEUTRAL — this is resetting *this* app's own background
            // expression, and must not clear an unrelated AI message that
            // happens to be showing (see PROTOCOL.md's FACE priority notes).
            _connection.SendCommand("FACE IDLE");
            _connection.SendCommand("MSG");
            _gameFaceActive = false;

            _gameMonitor = new GameMonitor();
            _gameMonitor.GameChanged += OnGameChanged;
            _gameMonitor.StatusChanged += OnGameStatusChanged;
            _gameMonitor.Start();
            GameStatusText.Text = "Carregando catálogo de jogos...";
        }
        else
        {
            _gameMonitor?.Dispose();
            _gameMonitor = null;
            GameStatusText.Text = string.Empty;
            ClearGameFaceIfActive();
        }
    }

    private void OnGameChanged(string? game)
    {
        // GameMonitor raises this off its own polling loop, not the UI thread.
        Dispatcher.Invoke(() =>
        {
            if (game == null)
            {
                GameStatusText.Text = "Nenhum jogo detectado";
                ClearGameFaceIfActive();
                return;
            }

            GameStatusText.Text = $"Jogando {game}";
            _connection.SendCommand("FACE PLAYING");
            _connection.SendCommand($"MSG Jogando {game}");
            _gameFaceActive = true;
        });
    }

    private void OnGameStatusChanged(string status)
    {
        // GameMonitor raises this off its own polling loop, not the UI thread.
        Dispatcher.Invoke(() => GameStatusText.Text = status);
    }

    /// <summary>
    /// FACE PLAYING is sticky on Core, same as MUSIC/WATCHING — if the game
    /// closes (or the checkbox is unchecked) while it's showing, it has to be
    /// explicitly cleared or Brobot would be stuck "playing" forever. Sends
    /// FACE IDLE, not FACE NEUTRAL — see ClearMediaFaceIfActive's comment.
    /// </summary>
    private void ClearGameFaceIfActive()
    {
        if (!_gameFaceActive)
        {
            return;
        }

        _connection.SendCommand("FACE IDLE");
        _connection.SendCommand("MSG");
        _gameFaceActive = false;
    }

    /// <summary>
    /// Installing/uninstalling edits the user's global Claude Code
    /// settings.json (see ClaudeCodeHookInstaller) — a persistent, explicit
    /// action the user opts into via this button, rather than something tied
    /// to a checkbox's on/off state.
    /// </summary>
    private void PensamentosIaInstallButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ClaudeCodeHookInstaller.IsInstalled())
            {
                ClaudeCodeHookInstaller.Uninstall();
                _aiThoughtsListener?.Dispose();
                _aiThoughtsListener = null;
                ClearAiThoughtFaceIfActive();
            }
            else
            {
                ClaudeCodeHookInstaller.Install();
                StartAiThoughtsListener();
            }
        }
        catch (Exception ex)
        {
            PensamentosIaStatusText.Text = $"Falha: {ex.Message}";
            return;
        }

        RefreshAiThoughtsInstallUi();
    }

    private void TemaComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || TemaComboBox.SelectedItem is not ThemeManager.ThemeInfo theme)
        {
            return;
        }

        ThemeManager.Apply(theme.Key);

        SenderSettings settings = SenderSettings.Load();
        settings.Theme = theme.Key;
        settings.Save();
    }

    private void SyncTemaComboBoxSelection(SenderSettings settings)
    {
        TemaComboBox.SelectedItem = ThemeManager.Available.FirstOrDefault(t => t.Key == settings.Theme)
            ?? ThemeManager.Available.First(t => t.Key == ThemeManager.DefaultTheme);
    }

    private void PensamentosIaComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The ComboBox's SelectedIndex="0" in XAML fires this during
        // InitializeComponent itself, before sibling controls declared later
        // in the markup (like the Instalar/Desinstalar button) have been
        // assigned to their fields yet — IsInitialized only turns true once
        // InitializeComponent finishes, so this skips that premature firing.
        // RestoreSettings() makes its own direct call to refresh the UI once
        // everything is actually ready.
        if (!IsInitialized)
        {
            return;
        }

        RefreshAiThoughtsInstallUi();
    }

    private void StartAiThoughtsListener()
    {
        if (_aiThoughtsListener != null)
        {
            return; // already running
        }

        _aiThoughtsListener = new AiThoughtsListener();
        _aiThoughtsListener.ThoughtReceived += OnAiThoughtReceived;
        try
        {
            _aiThoughtsListener.Start(AiThoughtsPort);
        }
        catch (Exception ex)
        {
            PensamentosIaStatusText.Text = $"Falha ao iniciar: {ex.Message}";
            _aiThoughtsListener.Dispose();
            _aiThoughtsListener = null;
        }
    }

    /// <summary>
    /// Reflects the *actual* on-disk install state (read fresh, not cached),
    /// so this stays correct even if settings.json was hand-edited or Claude
    /// Code was reinstalled since MiMo last checked. Only Claude Code hooks
    /// are wired up so far — the button is disabled for the other providers
    /// in the combo until they get their own installer.
    /// </summary>
    private void RefreshAiThoughtsInstallUi()
    {
        bool isClaudeSelected = (PensamentosIaComboBox.SelectedItem as ComboBoxItem)?.Content as string == "Claude";
        bool installed = ClaudeCodeHookInstaller.IsInstalled();

        PensamentosIaInstallButton.Content = installed ? "Desinstalar" : "Instalar";
        PensamentosIaInstallButton.IsEnabled = isClaudeSelected;

        if (!isClaudeSelected)
        {
            PensamentosIaStatusText.Text = "Instalação automática disponível só para Claude Code por enquanto.";
        }
        else
        {
            PensamentosIaStatusText.Text = installed
                ? $"Instalado — escutando eventos na porta {AiThoughtsPort}"
                : string.Empty;
        }
    }

    private void OnAiThoughtReceived(AiThoughtEvent thought)
    {
        // AiThoughtsListener raises this from a thread-pool thread, not the UI thread.
        Dispatcher.Invoke(() =>
        {
            switch (thought.Name)
            {
                case "UserPromptSubmit":
                    // THINKING is sticky on Core (holds until PreToolUse/Stop/etc.
                    // picks something else), so it needs the same explicit-clear
                    // handling as MUSIC/WATCHING below.
                    _connection.SendCommand("FACE THINKING");
                    _connection.SendCommand("MSG Pensando...");
                    _aiThoughtFaceActive = true;
                    break;

                case "PreToolUse":
                    _connection.SendCommand("FACE READING");
                    if (!string.IsNullOrWhiteSpace(thought.Text))
                    {
                        _connection.SendCommand($"MSG {thought.Text}");
                    }
                    _aiThoughtFaceActive = false; // READING isn't sticky — it auto-reverts on its own
                    break;

                case "Notification":
                    // No FACE change: a notification (e.g. "waiting for permission")
                    // isn't itself an expression, just something worth saying.
                    if (!string.IsNullOrWhiteSpace(thought.Text))
                    {
                        _connection.SendCommand($"MSG {thought.Text}");
                    }
                    break;

                case "Stop":
                    _connection.SendCommand("FACE FINISHED");
                    _connection.SendCommand("MSG Terminei!");
                    _aiThoughtFaceActive = false;
                    break;

                case "SessionEnd":
                    _connection.SendCommand("FACE NEUTRAL");
                    _connection.SendCommand("MSG");
                    _aiThoughtFaceActive = false;
                    break;
            }

            PensamentosIaStatusText.Text = $"Último evento: {thought.Name}";
        });
    }

    /// <summary>
    /// THINKING is sticky on Core, same as MUSIC/WATCHING — if the checkbox
    /// is turned off while it's showing, it has to be explicitly cleared or
    /// Brobot would be stuck glitching forever.
    /// </summary>
    private void ClearAiThoughtFaceIfActive()
    {
        if (!_aiThoughtFaceActive)
        {
            return;
        }

        _connection.SendCommand("FACE NEUTRAL");
        _connection.SendCommand("MSG");
        _aiThoughtFaceActive = false;
    }

    // Checkboxes take effect immediately (the monitors above start/stop the
    // moment you click one), but are only remembered across restarts once
    // "Salvar configurações" is pressed.
    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Load-then-mutate rather than constructing a fresh SenderSettings:
        // this button only owns the checkbox/provider fields below, and must
        // not stomp fields other windows own (e.g. Theme/connection settings
        // from SettingsWindow) with their defaults.
        SenderSettings settings = SenderSettings.Load();
        settings.HoraEnabled = HoraCheckBox.IsChecked == true;
        settings.ClimaEnabled = ClimaCheckBox.IsChecked == true;
        settings.PensamentosIaProvider = (PensamentosIaComboBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Claude";
        settings.MidiaEnabled = MediaCheckBox.IsChecked == true;
        settings.JogosEnabled = GameCheckBox.IsChecked == true;
        if (TryParseAddress(ConnectionAddressTextBox.Text, out string host, out int port))
        {
            settings.TcpHost = host;
            settings.TcpPort = port;
        }

        settings.Save();

        string original = SaveButton.Content as string ?? "Salvar configurações";
        SaveButton.Content = "Salvo!";
        var resetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        resetTimer.Tick += (_, _) =>
        {
            SaveButton.Content = original;
            resetTimer.Stop();
        };
        resetTimer.Start();
    }

    /// <summary>
    /// Replays whatever was saved last time: reconnects to Core, then
    /// re-checks each checkbox that was on (which fires the same handlers a
    /// manual click would, so the monitors actually start — no separate
    /// "apply settings" code path to keep in sync with the checkbox logic).
    /// </summary>
    private void RestoreSettings()
    {
        SenderSettings settings = SenderSettings.Load();

        ConnectionAddressTextBox.Text = string.IsNullOrWhiteSpace(settings.TcpHost) ? "" : $"{settings.TcpHost}:{settings.TcpPort}";
        if (!string.IsNullOrWhiteSpace(settings.TcpHost))
        {
            _connection.ConnectTcp(settings.TcpHost, settings.TcpPort);
        }
        UpdateConnectionStatus();

        foreach (ComboBoxItem item in PensamentosIaComboBox.Items)
        {
            if ((string)item.Content == settings.PensamentosIaProvider)
            {
                PensamentosIaComboBox.SelectedItem = item;
                break;
            }
        }

        HoraCheckBox.IsChecked = settings.HoraEnabled;
        ClimaCheckBox.IsChecked = settings.ClimaEnabled;
        MediaCheckBox.IsChecked = settings.MidiaEnabled;
        GameCheckBox.IsChecked = settings.JogosEnabled;

        TemaComboBox.ItemsSource = ThemeManager.Available;
        SyncTemaComboBoxSelection(settings);

        RefreshAiThoughtsInstallUi();
        if (ClaudeCodeHookInstaller.IsInstalled())
        {
            StartAiThoughtsListener();
        }
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Visible = true,
            Text = "MiMo",
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                ShowFromTray();
            }
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir", null, (_, _) => ShowFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => ExitApplication());
        _trayIcon.ContextMenuStrip = menu;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();

        // WPF's default keyboard-focus-follows-into-view behavior can auto-scroll
        // the ScrollViewer to whichever control ends up focused on show (e.g. the
        // first checkbox), clipping the logo at the top — force it back to the
        // top explicitly every time the window is (re)shown.
        RootScrollViewer.ScrollToTop();
    }

    private void ExitApplication()
    {
        _trayIcon!.Visible = false;
        _trayIcon.Dispose();
        _mediaMonitor?.Dispose();
        _gameMonitor?.Dispose();
        _weatherMonitor?.Dispose();
        _aiThoughtsListener?.Dispose();
        _clockTimer?.Stop();
        _connectionStatusTimer.Stop();
        _connection.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>Builds the tray icon from src/mimo-b.png (a "MiMo" wordmark on black) at runtime, scaled to the small size a tray icon actually needs.</summary>
    private static Drawing.Icon CreateTrayIcon()
    {
        var uri = new Uri("pack://application:,,,/Brobot.Sender;component/src/mimo-b.png");
        using System.IO.Stream resourceStream = System.Windows.Application.GetResourceStream(uri)!.Stream;
        using Drawing.Image source = Drawing.Image.FromStream(resourceStream);

        using var bitmap = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, 0, 0, 32, 32);
        }

        nint hIcon = bitmap.GetHicon();
        return Drawing.Icon.FromHandle(hIcon);
    }
}

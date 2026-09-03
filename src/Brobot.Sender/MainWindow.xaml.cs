using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    // Core reveals a message one character at a time at this rate
    // (TYPING_CHAR_INTERVAL_MS in BrobotCore/include/Face.h). Mirrored by hand
    // here, the same way IDisplay and Font5x7.CharAdvance already are, so this
    // app can work out how long a message of its own needs before it's been
    // fully said — see HoldAiMessagesWhileTyping. Nothing enforces the match;
    // if Core's rate changes, change it here too.
    private const int CoreTypingCharIntervalMs = 40;
    private const int AiMessageHoldMarginMs = 400; // typing finished vs. read comfortably are not the same instant

    private static readonly string AiEventLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Brobot", "ai-events.log");
    private const long AiEventLogMaxBytes = 256 * 1024;

    private readonly BrobotConnection _connection;
    private Forms.NotifyIcon? _trayIcon;

    private WindowsMediaMonitor? _mediaMonitor;
    private bool _mediaFaceActive;

    private GameMonitor? _gameMonitor;
    private bool _gameFaceActive;

    // Only runs while a game is actually detected — Game Mode is the one
    // screen these numbers appear on, and polling hardware sensors every two
    // seconds for a display nobody is looking at would be pure waste.
    private SystemStatsMonitor? _statsMonitor;

    private WeatherMonitor? _weatherMonitor;
    private WeatherReading? _lastWeatherReading; // resent on reconnect (see UpdateConnectionStatus) instead of waiting out WeatherMonitor's own 30-min cycle
    private WeatherCondition? _lastWeatherCondition; // null = no baseline yet, so the very first reading never fires a "changed" alert
    private bool _wasConnected;
    private DispatcherTimer? _clockTimer;

    // "Modo teste" — hidden behind a gesture on the wordmark rather than
    // shown as another card, because this app ships to whoever assembled a
    // Brobot and a raw command box in the middle of the checklist invites
    // breaking things. Clicks have to land within TestModeUnlockWindow of
    // each other, so ordinary stray clicks on the logo never accumulate
    // into an unlock.
    private const int TestModeUnlockClicks = 5;
    private static readonly TimeSpan TestModeUnlockWindow = TimeSpan.FromSeconds(2);
    private int _logoClickCount;
    private DateTime _lastLogoClick = DateTime.MinValue;
    private readonly List<string> _testCommandHistory = new();
    private int _testHistoryIndex = -1; // -1 = not currently browsing history

    private DispatcherTimer? _breakTimer;
    private DateOnly? _breakMorningFiredOn; // date each slot last fired on, so it fires once per day instead of every tick during that whole minute
    private DateOnly? _breakAfternoonFiredOn;

    // Caring "go stretch your legs" nudges — one random pick per trigger,
    // same flat-pool pattern as the bedtime messages in Personality.cpp,
    // just Sender-side since this is plain wall-clock logic with no need
    // for Core to know about it at all.
    private static readonly string[] PausaMessages =
    {
        "Hora de esticar as pernas, bora pegar um cafe",
        "Bora reabastecer o cafe!",
        "Que tal uma pausinha? Levanta e da uma volta",
        "Seus olhos merecem um descanso, bora cafe",
        "Intervalo chegou! Estica essas pernas ai",
        "Vai la, um cafezinho cai bem agora",
        "Pausa estrategica: levanta, anda um pouco, hidrata",
        "Cade aquele cafe? Hora de uma pausa",
        "Corpo agradece: levanta e da uma esticada",
        "Bora, um cafe rapido e volta com energia",
    };
    private static readonly Random PausaRng = new();

    private AiThoughtsListener? _aiThoughtsListener;
    private bool _aiThoughtFaceActive;
    private bool _aiStatsActive; // AISTATS is persistent on Core — see ClearAiStatsIfActive
    private DateTime _aiMessageHoldUntil = DateTime.MinValue; // see HoldAiMessagesWhileTyping

    private readonly DispatcherTimer _connectionStatusTimer;

    // MiMo's IP comes from the router's DHCP server, so it moves on its own —
    // a power cycle (MiMo's or the router's) can hand it a different address,
    // and the one saved in the Conexão card then points at nothing. Rather
    // than making that the user's problem, a failing connection eventually
    // triggers a sweep of the local network for MiMo (see MimoDiscovery), and
    // whatever it finds replaces the saved address.
    //
    // Long enough that an ordinary blip (MiMo still booting, WiFi
    // reassociating, the router's DHCP renewing) resolves itself on
    // ConnectTcp's own 500ms retry loop first — sweeping is for the case
    // where the address is genuinely wrong, not for every hiccup.
    private static readonly TimeSpan SweepAfterFailingFor = TimeSpan.FromSeconds(8);

    // MiMo simply being switched off looks exactly like MiMo having moved,
    // and there's no way to tell them apart without sweeping. This is what
    // keeps that case from sweeping the network back-to-back forever.
    private static readonly TimeSpan SweepCooldown = TimeSpan.FromSeconds(30);

    // Where MiMo is, as far as this app knows — the source of truth the
    // Conexão card merely displays. It used to be the text in an editable
    // field, which is exactly what broke every time DHCP moved MiMo: the
    // address was only ever as right as whatever someone last typed. Now it
    // comes from settings on startup and from the sweep after that, and null
    // means "nowhere known yet", which is a cue to go looking rather than an
    // error.
    private string? _coreHost;
    private int _corePort = MimoDiscovery.DefaultPort;

    private DateTime? _connectingSince;
    private DateTime? _lastSweepFinishedAt;
    private CancellationTokenSource? _sweepCts;
    private bool _sweepRunning;

    // What the card shows while a sweep runs. Held as fields (rather than
    // written straight to the controls) so UpdateConnectionStatus stays the
    // single place that touches them — the same reason the status text has
    // one writer instead of being set optimistically from everywhere, which
    // is a bug this card already had once.
    private string _sweepProgressText = "Procurando MiMo na rede...";
    private string _sweepProbeAddress = "";

    // A sweep finishes ~254 probes in a couple of seconds; repainting on every
    // one is an unreadable blur. Repainting on a fixed clock instead of on
    // each report is what makes the count climb evenly: probes complete in
    // bursts (64 run at once, and unreachable addresses all time out
    // together), so a report-driven repaint inherits that lumpiness, while a
    // steady tick just shows wherever the sweep has got to.
    private static readonly TimeSpan SweepProgressTickInterval = TimeSpan.FromMilliseconds(333);
    private DispatcherTimer? _sweepProgressTimer;

    // A one-off message (bad address, sweep found nothing) that has to
    // survive _connectionStatusTimer's next tick — which is 200ms away and
    // would otherwise overwrite it with the polled status before anyone
    // could read it.
    private string _transientStatus = "";
    private DateTime _transientStatusUntil = DateTime.MinValue;

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

        // Modo teste follows the same single source of truth as the Conexão
        // button rather than tracking the link itself — a parallel notion of
        // "are we connected" is exactly what left that button stuck reading
        // "Desconectar" once already.
        if (TesteCard.Visibility == Visibility.Visible)
        {
            TesteEnviarButton.IsEnabled = connected;
            TesteAtalhosPanel.IsEnabled = connected;
        }

        if (DateTime.Now < _transientStatusUntil)
        {
            ConnectionStatusText.Text = _transientStatus;
        }
        else if (connected)
        {
            ConnectionStatusText.Text = "Conectado";
        }
        else if (_sweepRunning)
        {
            // Takes precedence over "Conectando...": while a sweep runs, the
            // retry loop is usually still hammering an address already known
            // to be wrong, and the sweep is the part actually making progress.
            ConnectionStatusText.Text = _sweepProgressText;
        }
        else
        {
            ConnectionStatusText.Text = connecting ? "Conectando..." : "Desconectado";
        }

        // The address is a readout, never an input — MiMo's IP comes from the
        // sweep, and a stale hand-typed one is the whole problem this replaced.
        // It only ever shows an address that means something *right now*:
        // during a sweep, the one being probed; while connected, the one that
        // actually reached MiMo. Every other state shows nothing at all,
        // because the only address available then is one that is either
        // unproven or known not to work — and printing a dead address next to
        // "Conectando..." reads as if that address were the live one.
        ConnectionAddressText.Text = _sweepRunning
            ? _sweepProbeAddress
            : (connected && _coreHost != null ? $"{_coreHost}:{_corePort}" : "");

        TryStartNetworkSweep(connected, connecting);

        // The label tracks *connected*, and nothing else. It used to read
        // "Desconectar" while merely connecting or sweeping too, on the
        // reasoning that those are things a click could call off — but
        // ConnectTcp retries forever, so a failed search left the button
        // saying "Desconectar" indefinitely with nothing connected to
        // disconnect from. Now anything short of a live connection reads
        // "Conectar", and clicking it means "try now" (see the click handler),
        // so the label and the action agree in every state.
        ConnectionButton.Content = connected ? "Desconectar" : "Conectar";
        // Disabled only while a sweep is actually in flight — it's the one
        // state where a second click would stack a duplicate search, and it
        // lasts a few seconds.
        ConnectionButton.IsEnabled = !_sweepRunning;

        // A freshly (re)connected Core — e.g. just rebooted, or was off when
        // this app started — has no idea what the weather badge should show
        // until told. WeatherMonitor itself doesn't know or care about the
        // connection at all (it just runs on its own 30-min timer), so
        // without this the badge would stay blank for up to 30 minutes after
        // a reconnect instead of picking up the last known reading right away.
        if (connected && !_wasConnected && _lastWeatherReading is { } reading) {
            _connection.SendCommand($"WEATHER {reading.TempC} {reading.CoreConditionName}");
        }
        // Same reasoning as the weather resend above — THEME is another
        // persistent flag Core forgets on its own after a reboot. Any
        // non-DEFAULT selection needs resending; DEFAULT is already Core's
        // own boot default, same as the "Tela do MiMo" checkbox this replaced.
        if (connected && !_wasConnected && TemaComboBox.SelectedItem is ThemeManager.ThemeInfo currentTheme
            && currentTheme.CoreTheme != "DEFAULT") {
            _connection.SendCommand($"THEME {currentTheme.CoreTheme}");
        }
        // SOUND/SCANLINES are persistent flags too, and both default to ON
        // on Core (same as THEME's DEFAULT) — only the OFF case needs
        // resending after a reconnect, since ON is already what a freshly
        // booted/reconnected Core assumes on its own.
        if (connected && !_wasConnected && SonsCheckBox.IsChecked == false) {
            _connection.SendCommand("SOUND OFF");
        }
        if (connected && !_wasConnected && ScanlinesCheckBox.IsChecked == false) {
            _connection.SendCommand("SCANLINES OFF");
        }
        _wasConnected = connected;
    }

    /// <summary>
    /// Starts a network sweep once the connection has been failing long
    /// enough that the saved address itself is the likely problem (see
    /// SweepAfterFailingFor). Called from the status poll rather than from a
    /// failure callback because BrobotConnection has no "gave up" event —
    /// ConnectTcp retries forever by design, so "how long has this been
    /// getting nowhere" is something only the poller can notice.
    /// </summary>
    private void TryStartNetworkSweep(bool connected, bool connecting)
    {
        if (connected)
        {
            _connectingSince = null;
            return;
        }

        if (!connecting)
        {
            // Disconnected on purpose — nothing is trying, so there's nothing
            // failing to recover from.
            _connectingSince = null;
            return;
        }

        _connectingSince ??= DateTime.Now;

        if (_sweepRunning
            || DateTime.Now - _connectingSince.Value < SweepAfterFailingFor
            || (_lastSweepFinishedAt is { } lastSweep && DateTime.Now - lastSweep < SweepCooldown))
        {
            return;
        }

        // Mid-session recovery: whatever address this was using was reaching
        // MiMo until moments ago, so it's worth probing first.
        StartNetworkSweep(trustPreviousAddress: true);
    }

    /// <summary>
    /// Sweeps the local network for MiMo and, if it finds one, repoints the
    /// connection (and the saved address) at it. async void because it's
    /// driven by UI events/timers exactly like a click handler is; everything
    /// after each await is back on the UI thread, so touching controls and
    /// _connection here is safe.
    /// </summary>
    /// <param name="trustPreviousAddress">
    /// Whether the address currently in use gets probed first and, if it
    /// answers but won't identify itself, accepted anyway (see MimoDiscovery).
    /// True for a mid-session recovery, where that address was demonstrably
    /// MiMo moments ago. False at startup: the app may have been closed for
    /// days, MiMo may have moved, and DHCP may well have handed that address
    /// to something else entirely — in which case trusting it would mean
    /// adopting a stranger. A clean startup sweep costs ~2.5s and can't make
    /// that mistake.
    /// </param>
    private async void StartNetworkSweep(bool trustPreviousAddress)
    {
        _sweepRunning = true;
        _sweepCts = new CancellationTokenSource();
        CancellationToken token = _sweepCts.Token;
        _sweepProgressText = "Procurando MiMo na rede...";
        _sweepProbeAddress = "";
        _sweepProgressTimer = new DispatcherTimer { Interval = SweepProgressTickInterval };
        _sweepProgressTimer.Tick += (_, _) => UpdateConnectionStatus();
        _sweepProgressTimer.Start();
        UpdateConnectionStatus();

        // The port comes from whatever MiMo was last reached on, so a device
        // set up on a non-default port keeps being found there; a fresh
        // install with nothing known falls back to Core's own default. The
        // port survives even an untrusted startup sweep — an address goes
        // stale on its own, a port doesn't.
        string? previousHost = trustPreviousAddress ? _coreHost : null;
        int port = _corePort > 0 ? _corePort : MimoDiscovery.DefaultPort;

        // Constructed on the UI thread, so it marshals every report back here
        // by itself — the sweep reports from whichever thread pool thread
        // finished a probe, and none of them may touch these fields directly.
        var progress = new Progress<MimoDiscovery.SweepProgress>(OnSweepProgress);

        string? found = null;
        bool sweepCancelled;
        try
        {
            found = await MimoDiscovery.FindAsync(port, previousHost, progress, token);
        }
        catch (Exception)
        {
            // A sweep failing outright (an adapter yanked mid-scan, say) is
            // just a sweep that found nothing — never a reason to take down
            // the tray app.
        }
        finally
        {
            // Read before disposing the source: a CancellationToken whose
            // CancellationTokenSource has already been disposed is not safe to
            // keep querying.
            sweepCancelled = token.IsCancellationRequested;
            _sweepRunning = false;
            _lastSweepFinishedAt = DateTime.Now;
            _sweepCts?.Dispose();
            _sweepCts = null;
            _sweepProgressTimer?.Stop();
            _sweepProgressTimer = null;
        }

        if (sweepCancelled)
        {
            // The user hit Desconectar while this was running; acting on the
            // result now would reconnect behind their back.
            return;
        }

        if (found == null)
        {
            ShowTransientStatus("MiMo não encontrado na rede");
            return;
        }

        _coreHost = found;
        _corePort = port;
        _connection.ConnectTcp(found, port);
        // The retry clock restarts with the new address — without this, the
        // elapsed time from the old one carries over and the cooldown is all
        // that stands between this and an immediate second sweep.
        _connectingSince = null;
        PersistDiscoveredAddress(found, port);
        UpdateConnectionStatus();
    }

    /// <summary>
    /// Records where the sweep has got to, without painting anything — the
    /// 3-times-a-second tick below is what puts it on screen. Always called on
    /// the UI thread (see the Progress&lt;T&gt; that feeds it), so these fields
    /// need no locking against the tick that reads them.
    /// </summary>
    private void OnSweepProgress(MimoDiscovery.SweepProgress progress)
    {
        if (!_sweepRunning)
        {
            // A cancelled sweep's last few probes can still report after the
            // card has moved on to showing something else.
            return;
        }

        _sweepProbeAddress = progress.Address.ToString();
        _sweepProgressText = $"Procurando MiMo... {progress.Completed}/{progress.Total}";
    }

    private void CancelNetworkSweep()
    {
        _sweepCts?.Cancel();
        _sweepRunning = false;
        _sweepProgressTimer?.Stop();
        _sweepProgressTimer = null;
    }

    private void ShowTransientStatus(string text)
    {
        _transientStatus = text;
        _transientStatusUntil = DateTime.Now.AddSeconds(4);
        ConnectionStatusText.Text = text;
    }

    /// <summary>
    /// Writes a swept-out address straight to the settings file, without
    /// waiting for "Salvar configurações". Unlike every other setting there,
    /// this isn't a preference the user chose — it's a fact about where MiMo
    /// currently is, and leaving it unsaved would mean re-sweeping the whole
    /// network on every launch. Load-then-mutate-then-save, so the checkbox
    /// state already on disk is carried through untouched rather than
    /// overwritten with whatever the UI happens to show right now.
    /// </summary>
    private static void PersistDiscoveredAddress(string host, int port)
    {
        try
        {
            SenderSettings settings = SenderSettings.Load();
            settings.TcpHost = host;
            settings.TcpPort = port;
            settings.Save();
        }
        catch (Exception)
        {
            // Worst case the address is swept for again next launch — not
            // worth interrupting a connection that just started working.
        }
    }

    private void ConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_connection.IsConnected)
        {
            CancelNetworkSweep();
            _connection.Disconnect();
            _connectingSince = null;
            UpdateConnectionStatus();
            return;
        }

        if (_sweepRunning)
        {
            // The button is disabled in this state; this only guards against a
            // click that slipped through between a sweep starting and the
            // status poll repainting.
            return;
        }

        if (_coreHost != null && !_connection.IsConnectingTcp)
        {
            // A known address and nothing currently trying it — worth one
            // direct shot before searching the whole network. This is the
            // reconnect-after-Desconectar path, where the address was reaching
            // MiMo minutes ago. ConnectTcp retries on its own every 500ms and
            // never throws synchronously, so nothing is set optimistically
            // here; if the address has gone stale, TryStartNetworkSweep turns
            // those retries into a sweep after SweepAfterFailingFor.
            _connection.ConnectTcp(_coreHost, _corePort);
            UpdateConnectionStatus();
            return;
        }

        // Either nothing is known, or the known address is already being
        // retried and getting nowhere — in both cases the useful meaning of
        // "Conectar" is "search now", skipping the wait TryStartNetworkSweep
        // would otherwise impose. Untrusting on purpose: if that address were
        // going to work, the retry loop would already have connected.
        StartNetworkSweep(trustPreviousAddress: false);
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
            _lastWeatherReading = null; // otherwise a later reconnect would resend a badge the user just turned off
            _lastWeatherCondition = null; // re-checking later should start a fresh baseline, not "changed" against a stale value
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

            // The badge update goes out FIRST, before any alert. Core picks
            // the alert's artwork from the WeatherCondition this command
            // stores (see Face.cpp's drawWeatherNotification) rather than
            // from anything on the NOTIFY line, so sending them the other
            // way round would illustrate the alert with the *previous*
            // condition — raining while the umbrella screen still showed
            // yesterday's sun.
            _lastWeatherReading = reading;
            _connection.SendCommand($"WEATHER {reading.TempC} {reading.CoreConditionName}");

            // Only alert on an actual change — _lastWeatherCondition is null
            // on the very first reading after Start() (nothing to compare
            // against yet), and most 30-min polls just confirm the same
            // condition as before.
            //
            // This used to be a FACE NEUTRAL + MSG pair, which landed in the
            // same tier as AI activity and so could be buried by it. It's a
            // notification now: top priority, its own full-screen artwork,
            // and it clears itself — which also retires the FACE NEUTRAL,
            // whose only job was to claim a tier so the MSG wouldn't be
            // routed somewhere else by _lastCommandTier.
            if (_lastWeatherCondition.HasValue && _lastWeatherCondition.Value != reading.Condition)
            {
                _connection.SendCommand($"NOTIFY WEATHER {WeatherAlerts.RandomFor(reading.Condition)}");
            }
            _lastWeatherCondition = reading.Condition;
        });
    }

    private void OnWeatherStatusChanged(string status)
    {
        Dispatcher.Invoke(() => ClimaStatusText.Text = status);
    }

    private void PausaCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (PausaCheckBox.IsChecked == true)
        {
            // Re-armed every time the box is checked, but only for whichever
            // slot(s) haven't happened yet today — a slot whose time already
            // passed gets marked as fired immediately instead of null, or
            // CheckBreakTime's very next tick would see "haven't fired today"
            // + "now >= target" and fire right away, no matter how long ago
            // the target time actually was. This was a real bug: checking
            // Pausa at, say, 20:00 with a 10:00 morning slot fired the
            // reminder instantly instead of waiting for tomorrow's 10:00.
            DateTime now = DateTime.Now;
            DateOnly today = DateOnly.FromDateTime(now);
            TimeOnly nowTime = TimeOnly.FromDateTime(now);

            _breakMorningFiredOn = (TryParseTime(PausaManhaTextBox.Text, out TimeOnly morning) && nowTime >= morning)
                ? today : (DateOnly?)null;
            _breakAfternoonFiredOn = (TryParseTime(PausaTardeTextBox.Text, out TimeOnly afternoon) && nowTime >= afternoon)
                ? today : (DateOnly?)null;

            _breakTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _breakTimer.Tick += (_, _) => CheckBreakTime();
            _breakTimer.Start();
            PausaStatusText.Text = string.Empty;
        }
        else
        {
            _breakTimer?.Stop();
            _breakTimer = null;
            PausaStatusText.Text = string.Empty;
        }
    }

    /// <summary>
    /// Ticks every second (TIME/clock's own cadence — there's no "it's now
    /// HH:mm" event to react to) comparing wall-clock time against the two
    /// configured slots. Each slot tracks the date it last fired on so it
    /// fires exactly once per day instead of on every tick during that whole
    /// minute. FACE COFFEE isn't sticky on Core (auto-reverts like any other
    /// foreground expression), so there's nothing to explicitly clear here or
    /// on uncheck, unlike MUSIC/WATCHING/PLAYING/THINKING.
    /// </summary>
    private void CheckBreakTime()
    {
        DateTime now = DateTime.Now;
        DateOnly today = DateOnly.FromDateTime(now);
        TimeOnly nowTime = TimeOnly.FromDateTime(now);

        if (TryParseTime(PausaManhaTextBox.Text, out TimeOnly morning)
            && _breakMorningFiredOn != today && nowTime >= morning)
        {
            _breakMorningFiredOn = today;
            SendBreakReminder();
        }

        if (TryParseTime(PausaTardeTextBox.Text, out TimeOnly afternoon)
            && _breakAfternoonFiredOn != today && nowTime >= afternoon)
        {
            _breakAfternoonFiredOn = today;
            SendBreakReminder();
        }
    }

    /// <summary>
    /// Sends one NOTIFY line rather than the FACE+MSG pair this used to,
    /// which is what puts the reminder on Core's top-priority notification
    /// tier: it takes the whole screen for a few seconds, outranks even AI
    /// activity, and expires by itself (see PROTOCOL.md's NOTIFY). Atomic
    /// on purpose — a two-command form could be caught half-applied, and a
    /// lone MSG would route by Core's _lastCommandTier instead.
    ///
    /// Note this app still doesn't decide what a break reminder *looks*
    /// like: it says "this is a notification, the expression is COFFEE,
    /// here's the text", and Core owns the screen it turns into.
    /// </summary>
    private void SendBreakReminder()
    {
        string message = PausaMessages[PausaRng.Next(PausaMessages.Length)];
        _connection.SendCommand($"NOTIFY COFFEE {message}");
        PausaStatusText.Text = $"Último lembrete: {message}";
    }

    private static bool TryParseTime(string text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

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
    /// dancing/watching forever. Sends FACE IDLE_MEDIA, not FACE NEUTRAL —
    /// NEUTRAL would stomp an unrelated AI message that happens to be
    /// showing at the same time (see PROTOCOL.md's FACE priority notes).
    /// IDLE_MEDIA rather than plain IDLE because Jogos and Mídia are two
    /// separate tiers on Core now: bare IDLE clears both, so stopping the
    /// music would also wipe a game that is still very much open.
    /// </summary>
    private void ClearMediaFaceIfActive()
    {
        if (!_mediaFaceActive)
        {
            return;
        }

        _connection.SendCommand("FACE IDLE_MEDIA");
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
            // in-memory state that resets on every launch. FACE IDLE_GAME,
            // not FACE NEUTRAL — this is resetting *this* app's own game
            // expression, and must not clear an unrelated AI message that
            // happens to be showing (see PROTOCOL.md's FACE priority notes),
            // nor the media tier, which is now separate from this one.
            _connection.SendCommand("FACE IDLE_GAME");
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
            StartStatsMonitor();
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
    /// FACE IDLE_GAME, not FACE NEUTRAL and not bare IDLE — see
    /// ClearMediaFaceIfActive's comment for both halves of that.
    /// </summary>
    private void ClearGameFaceIfActive()
    {
        if (!_gameFaceActive)
        {
            return;
        }

        _connection.SendCommand("FACE IDLE_GAME");
        _connection.SendCommand("MSG");
        _gameFaceActive = false;
        StopStatsMonitor();
    }

    private void StartStatsMonitor()
    {
        if (_statsMonitor != null)
        {
            return;
        }

        _statsMonitor = new SystemStatsMonitor();
        _statsMonitor.StatsUpdated += OnStatsUpdated;
        _statsMonitor.Start();
    }

    private void StopStatsMonitor()
    {
        if (_statsMonitor == null)
        {
            return;
        }

        _statsMonitor.StatsUpdated -= OnStatsUpdated;
        _statsMonitor.Dispose();
        _statsMonitor = null;

        // STATS is persistent on Core, exactly like WEATHER/TIME — nothing
        // times it out — so leaving without clearing would freeze the last
        // reading on screen forever (see PROTOCOL.md).
        _connection.SendCommand("STATS");
    }

    private void OnStatsUpdated(SystemStatsReading reading)
    {
        // SystemStatsMonitor raises this from a thread-pool timer, not the UI thread.
        Dispatcher.Invoke(() =>
        {
            // -1 for anything with no source, which Core renders as "--"
            // rather than as a zero nobody should believe (see Face.h).
            static int Field(int? value) => value ?? -1;

            _connection.SendCommand(
                $"STATS {Field(reading.CpuLoadPercent)} {Field(reading.CpuTempC)} " +
                $"{Field(reading.GpuLoadPercent)} {Field(reading.GpuTempC)} {Field(reading.RamLoadPercent)}");
        });
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
                ClearAiStatsIfActive();
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

    /// <summary>
    /// Drives two unrelated systems from one picker: ThemeManager.Apply
    /// changes this app's own WPF skin, and the SendCommand below changes
    /// how Core itself draws the display (see PROTOCOL.md's THEME command)
    /// — they just happen to both be about "appearance". This used to be a
    /// separate "Tela do MiMo" checkbox card; folded in here instead, since
    /// from the user's point of view MiMo Classic/MiMo Matrix is a single
    /// choice, not two.
    /// </summary>
    private void TemaComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || TemaComboBox.SelectedItem is not ThemeManager.ThemeInfo theme)
        {
            return;
        }

        ThemeManager.Apply(theme.Key);
        _connection.SendCommand($"THEME {theme.CoreTheme}");

        SenderSettings settings = SenderSettings.Load();
        settings.Theme = theme.Key;
        settings.Save();
    }

    /// <summary>
    /// SOUND is a persistent device flag, same shape as THEME — Core just
    /// remembers whatever was last sent, with no expiry/typewriter/priority
    /// logic to worry about, so there's nothing to explicitly clear on
    /// uncheck (unlike FACE MUSIC/WATCHING/PLAYING/THINKING elsewhere here).
    /// </summary>
    private void SonsCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        _connection.SendCommand(SonsCheckBox.IsChecked == true ? "SOUND ON" : "SOUND OFF");
    }

    /// <summary>Same shape as SonsCheckBox_CheckedChanged — SCANLINES is the other persistent device flag (see PROTOCOL.md).</summary>
    private void ScanlinesCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        _connection.SendCommand(ScanlinesCheckBox.IsChecked == true ? "SCANLINES ON" : "SCANLINES OFF");
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

    /// <summary>
    /// Appends one line to %AppData%\Broboti-events.log.
    ///
    /// The AI bridge is a race between short-lived processes that MiMo shows
    /// the *result* of, one message at a time, so "the greeting vanished"
    /// looks identical from the outside whatever caused it — a clear, a
    /// message applied out of order, or a second event nobody expected. This
    /// log is the only place the actual sequence and timing is visible.
    /// Capped and self-truncating rather than rotated: it exists to be read
    /// right after reproducing something, not to be kept.
    /// Never allowed to throw — a failure to write a diagnostic must not take
    /// down the event that was being diagnosed.
    /// </summary>
    private static void LogAiEvent(string line)
    {
        try
        {
            string? dir = Path.GetDirectoryName(AiEventLogPath);
            if (dir != null)
            {
                Directory.CreateDirectory(dir);
            }

            var info = new FileInfo(AiEventLogPath);
            if (info.Exists && info.Length > AiEventLogMaxBytes)
            {
                File.Delete(AiEventLogPath);
            }

            File.AppendAllText(
                AiEventLogPath,
                $"{DateTime.Now:HH:mm:ss.fff}  {line}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Sends one MSG, falling back to <paramref name="fallback"/> when the hook
    /// had nothing to say. A hook's text is optional by design — the payload
    /// may not carry the field, or may carry it empty — and "MSG" with no
    /// argument means *clear the message* on Core (see PROTOCOL.md), so
    /// forwarding a null through would silently wipe the screen instead of
    /// saying something. A null fallback therefore means "say nothing at all"
    /// rather than "clear what's there".
    ///
    /// Every AI message goes through here rather than calling SendCommand
    /// directly, so <see cref="_aiMessageHoldUntil"/> only has to be honoured
    /// in one place.
    /// </summary>
    private void SendAiMessage(string? text, string? fallback = null, bool force = false)
    {
        string? message = string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (!force && DateTime.UtcNow < _aiMessageHoldUntil)
        {
            // The session greeting is still typing itself in — see HoldAiMessagesWhileTyping.
            LogAiEvent($"   (segurado, saudacao ainda digitando) MSG {message}");
            return;
        }

        _connection.SendCommand($"MSG {message}");
    }

    /// <summary>
    /// Keeps the next second or so of AI messages from overwriting one that
    /// still has characters left to type.
    ///
    /// This exists for exactly one event. A session starting is the single
    /// moment when three things fire within a few hundred milliseconds of each
    /// other — SessionStart's greeting, UserPromptSubmit's "Pensando...", and
    /// the status line, which Claude Code runs once on session start whether or
    /// not a prompt was submitted — so the greeting was reliably being replaced
    /// mid-word, a second or so before it had finished saying which project it
    /// had opened. Everywhere else, a message replacing an older one promptly
    /// is the wanted behaviour, not a bug: a tool description that lagged
    /// behind the tool actually running would be worse than one that cut the
    /// previous description short.
    ///
    /// The window is measured from the message's own length rather than fixed
    /// at a second, because a second isn't enough: at Core's typing rate the
    /// usual greeting takes about 1.2s, and a long folder name pushes that
    /// further.
    /// </summary>
    private void HoldAiMessagesWhileTyping(string message)
    {
        _aiMessageHoldUntil = DateTime.UtcNow.AddMilliseconds(
            message.Length * CoreTypingCharIntervalMs + AiMessageHoldMarginMs);
    }

    private void OnAiThoughtReceived(AiThoughtEvent thought)
    {
        // AiThoughtsListener raises this from its own worker thread, not the
        // UI thread — and strictly in arrival order, which is what makes the
        // log below a faithful record of the sequence rather than of whichever
        // thread happened to win.
        Dispatcher.Invoke(() =>
        {
            LogAiEvent($"<- {thought.Name} {thought.Text}".TrimEnd());

            switch (thought.Name)
            {
                case "SessionStart":
                    // HAPPY, not NEUTRAL: this is the one AI event that's
                    // unambiguously good news. It isn't sticky (it times out
                    // via FACE_OVERRIDE_DURATION_MS like every other
                    // foreground expression bar THINKING), so there's nothing
                    // to track for a later clear.
                    _connection.SendCommand("FACE HAPPY");

                    // Claude Code has no hook event for switching accounts —
                    // measured against the real desktop app, an account switch
                    // fires no Notification at all, only the usual SessionEnd/
                    // SessionStart burst any session restart produces. So this
                    // reads the actual signed-in account out of
                    // ~/.claude.json (see ClaudeCodeAccount) and compares it
                    // against the one recorded from the previous SessionStart,
                    // rather than trying to detect the switch from anything
                    // the hook delivers.
                    ClaudeAccount? account = ClaudeCodeAccount.TryRead();
                    SenderSettings sessionStartSettings = SenderSettings.Load(); // one read, used for both the compare and the update below
                    string greeting;

                    bool switched = account != null
                        && sessionStartSettings.LastClaudeAccountUuid is { Length: > 0 } previousUuid
                        && previousUuid != account.Uuid;

                    // Logged unconditionally (not just when something looks
                    // wrong) because the previous version of this feature
                    // silently never fired and there was no way to tell why
                    // from the log alone — only the hook's raw text was
                    // recorded, never this decision.
                    LogAiEvent(account == null
                        ? "   (conta: ~/.claude.json sem oauthAccount legivel)"
                        : $"   (conta: {account.Uuid} \"{account.DisplayName}\", salva: " +
                          $"\"{(string.IsNullOrEmpty(sessionStartSettings.LastClaudeAccountUuid) ? "(nenhuma)" : sessionStartSettings.LastClaudeAccountUuid)}\", trocou: {switched})");

                    if (switched)
                    {
                        // The account-switch greeting wins outright over the
                        // generic one below — knowing *who* just signed in is
                        // more specific than "bora trabalhar em <pasta>", and
                        // switching accounts lands you in the home directory
                        // anyway, where there's no project name to offer instead.
                        greeting = $"Conta do Claude trocada para {account!.DisplayName}!";
                    }
                    else
                    {
                        greeting = string.IsNullOrWhiteSpace(thought.Text)
                            ? "Bora trabalhar!"
                            : thought.Text.Trim();
                    }

                    if (account != null && sessionStartSettings.LastClaudeAccountUuid != account.Uuid)
                    {
                        // Recorded immediately, like a discovered network
                        // address — this is a fact about which account is
                        // signed in, not a preference to wait on "Salvar
                        // configurações" for.
                        sessionStartSettings.LastClaudeAccountUuid = account.Uuid;
                        sessionStartSettings.Save();
                    }

                    LogAiEvent($"   -> MSG {greeting}");
                    SendAiMessage(greeting);
                    HoldAiMessagesWhileTyping(greeting);
                    _aiThoughtFaceActive = false;
                    break;

                case "UserPromptSubmit":
                    // THINKING is sticky on Core (holds until PreToolUse/Stop/etc.
                    // picks something else), so it needs the same explicit-clear
                    // handling as MUSIC/WATCHING below.
                    _connection.SendCommand("FACE THINKING");
                    SendAiMessage(null, "Pensando...");
                    _aiThoughtFaceActive = true;
                    break;

                case "PreToolUse":
                    _connection.SendCommand("FACE READING");
                    SendAiMessage(thought.Text);
                    _aiThoughtFaceActive = false; // READING isn't sticky — it auto-reverts on its own
                    break;

                case "PostToolUseFailure":
                    // The only event in the whole bridge that legitimately
                    // means ERROR. Core's Expression::FAILED (spelled ERROR on
                    // the wire) and Buzzer's three-trill "uh-oh" cue both
                    // existed with nothing ever triggering them from the AI
                    // side until this event was installed.
                    _connection.SendCommand("FACE ERROR");
                    SendAiMessage(thought.Text, "Deu erro na ferramenta.");
                    _aiThoughtFaceActive = false;
                    break;

                case "PermissionRequest":
                    // The one AI event that is genuinely an interruption:
                    // Claude has stopped and cannot continue until you look at
                    // the terminal. That's what the notification tier is for —
                    // whole screen, 10s, expires on its own (see PROTOCOL.md's
                    // NOTIFY), rather than a MSG that would queue politely
                    // behind whatever is already up. READING carries it because
                    // its "olha aqui" chirp is already the sound for exactly
                    // this, and asking permission isn't an error.
                    _connection.SendCommand($"NOTIFY READING {(string.IsNullOrWhiteSpace(thought.Text) ? "Preciso da sua permissao!" : thought.Text.Trim())}");
                    break;

                case "PermissionDenied":
                    _connection.SendCommand("FACE ERROR");
                    SendAiMessage(thought.Text, "Bloqueado.");
                    _aiThoughtFaceActive = false;
                    break;

                case "SubagentStart":
                    // A subagent working is still the AI thinking, so this
                    // shares THINKING (and its sticky handling) with
                    // UserPromptSubmit rather than inventing an expression:
                    // what changed is the message, not the state.
                    _connection.SendCommand("FACE THINKING");
                    SendAiMessage(thought.Text, "Chamei um subagente...");
                    _aiThoughtFaceActive = true;
                    break;

                case "SubagentStop":
                    // No FACE: the main agent is still working, and saying
                    // FINISHED here would announce an ending that hasn't
                    // happened. Same no-FACE reasoning as Notification below.
                    SendAiMessage(thought.Text);
                    break;

                case "PreCompact":
                    _connection.SendCommand("FACE THINKING");
                    SendAiMessage(null, "Organizando a memoria...");
                    _aiThoughtFaceActive = true;
                    break;

                case "PostCompact":
                    _connection.SendCommand("FACE FINISHED");
                    SendAiMessage(null, "Memoria organizada!");
                    _aiThoughtFaceActive = false;
                    break;

                case "StopFailure":
                    // The turn ended on an API error rather than on an answer,
                    // so it gets ERROR where Stop gets FINISHED.
                    _connection.SendCommand("FACE ERROR");
                    SendAiMessage(null, "Deu ruim na API...");
                    _aiThoughtFaceActive = false;
                    break;

                case "PreModelSwitch":
                case "PostModelSwitch":
                case "TaskCreated":
                case "TaskCompleted":
                case "CwdChanged":
                    // Housekeeping events: worth saying, not worth a face.
                    // TaskCreated/TaskCompleted carry no text of their own, so
                    // the phrase lives here rather than in the hook script,
                    // which has nothing to read for them.
                    // A null fallback means "say nothing at all": the model
                    // switches and CwdChanged carry no text of their own worth
                    // announcing when the hook couldn't name what changed
                    // (CwdChanged into the home directory, say), and a bare
                    // "..." on screen is worse than silence.
                    SendAiMessage(thought.Text, thought.Name switch
                    {
                        "TaskCreated" => "Anotei uma tarefa nova.",
                        "TaskCompleted" => "Tarefa concluida!",
                        _ => null,
                    });
                    break;

                case "Notification":
                    // No FACE change: a notification (e.g. "waiting for permission")
                    // isn't itself an expression, just something worth saying.
                    SendAiMessage(thought.Text);
                    break;

                case "ContextUsage":
                    // Sent by mimo-claude-statusline.ps1 (Claude Code's statusLine
                    // command, not a hook — see ClaudeCodeHookInstaller) on every
                    // new assistant message. No FACE change, same reasoning as
                    // Notification — this is a stat, not an expression.
                    // Claude Code runs the status line once on session start,
                    // so this is one of the two things that used to land on top
                    // of the greeting before it had finished typing.
                    SendAiMessage(thought.Text);
                    break;

                case "AiStats":
                    // Straight passthrough: the status line script already
                    // formatted the fields exactly as PROTOCOL.md's AISTATS
                    // wants them, and nothing here has an opinion about how
                    // Core draws them — that's Core's call, same as every
                    // other command this app sends.
                    _connection.SendCommand($"AISTATS {thought.Text}");
                    _aiStatsActive = true;
                    break;

                case "Stop":
                    // thought.Text is the first sentence of what Claude
                    // actually just said (Stop's last_assistant_message, see
                    // the hook script) — MiMo reports the work instead of a
                    // fixed "Terminei!", which stays as the fallback for a
                    // turn that ended with no text.
                    _connection.SendCommand("FACE FINISHED");
                    SendAiMessage(thought.Text, "Terminei!");
                    _aiThoughtFaceActive = false;
                    break;

                case "SessionEnd":
                    // A SessionEnd landing inside the greeting's window is,
                    // by construction, the *previous* session's — starting a
                    // new conversation (or /clear) ends the old session and
                    // begins a new one, and those are two separate hook
                    // processes with no ordering between them. Honouring it
                    // would wipe a greeting that went up a moment ago and is
                    // still typing itself in, which is exactly the "manda e
                    // depois manda um comando pra limpar" symptom. A session
                    // that genuinely ends a second after starting loses only
                    // a tidy-up it will get again on the next SessionEnd.
                    if (DateTime.UtcNow < _aiMessageHoldUntil)
                    {
                        LogAiEvent("   (SessionEnd ignorado: e da sessao anterior)");
                        break;
                    }

                    _connection.SendCommand("FACE NEUTRAL");
                    _connection.SendCommand("MSG");
                    ClearAiStatsIfActive();
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

    /// <summary>
    /// AISTATS is persistent on Core — nothing times it out, exactly like
    /// WEATHER/TIME/STATS (see PROTOCOL.md) — so a session that ends, or a
    /// bridge that gets uninstalled, has to say so explicitly or the last
    /// reading sits frozen in the AI tab forever, claiming a session is
    /// running that isn't.
    /// </summary>
    private void ClearAiStatsIfActive()
    {
        if (!_aiStatsActive)
        {
            return;
        }

        _connection.SendCommand("AISTATS");
        _aiStatsActive = false;
    }

    // Checkboxes take effect immediately (the monitors above start/stop the
    // moment you click one), but are only remembered across restarts once
    // "Salvar configurações" is pressed.
    /// <summary>
    /// Reveals the hidden "Modo teste" card after TestModeUnlockClicks
    /// clicks on the wordmark. Deliberately silent until it fires: a
    /// progress hint would turn a hidden gesture into a visible one.
    /// </summary>
    private void LogoImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (TesteCard.Visibility == Visibility.Visible)
        {
            return;
        }

        DateTime now = DateTime.Now;
        _logoClickCount = (now - _lastLogoClick) <= TestModeUnlockWindow ? _logoClickCount + 1 : 1;
        _lastLogoClick = now;

        if (_logoClickCount < TestModeUnlockClicks)
        {
            return;
        }

        _logoClickCount = 0;
        ShowTestMode();

        // Persisted immediately rather than waiting for "Salvar
        // configurações": the gesture is the act of enabling it, and having
        // to also remember to save would just mean doing it again next launch.
        SenderSettings settings = SenderSettings.Load();
        settings.TestModeUnlocked = true;
        settings.Save();
    }

    private void ShowTestMode()
    {
        TesteCard.Visibility = Visibility.Visible;
        UpdateConnectionStatus(); // sets the buttons' enabled state for the current link
    }

    /// <summary>
    /// Sends one or more raw PROTOCOL.md lines, separated by '|'. Sequences
    /// are the common case when testing — a weather alert is a WEATHER
    /// followed by a NOTIFY, and the order between them is part of the
    /// contract (see OnWeatherUpdated) — and typing them one at a time makes
    /// that awkward to reproduce. SendCommand writes and flushes per line, so
    /// they reach Core in the order given.
    ///
    /// '|' is therefore reserved and cannot appear inside a message; there is
    /// no escape for it. That is a fair trade in a dev-only box, where
    /// sending a literal pipe to MiMo has no use and sending a sequence has
    /// plenty.
    /// </summary>
    private void SendTestInput(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        if (!_connection.IsConnected)
        {
            ShowTestStatus("Sem conexão com o MiMo.");
            return;
        }

        var sent = new List<string>();
        foreach (string part in raw.Split('|'))
        {
            string command = part.Trim();
            if (command.Length == 0)
            {
                continue; // tolerate a trailing pipe, or "a || b"
            }

            // Only the command token is upper-cased: Core matches commands
            // with strncmp against uppercase literals (see Protocol::dispatch),
            // so "face happy" would otherwise do nothing at all — but the rest
            // of the line is message text and has to survive as typed, accents
            // and casing included.
            int space = command.IndexOf(' ');
            string normalized = space < 0
                ? command.ToUpperInvariant()
                : command[..space].ToUpperInvariant() + command[space..];

            _connection.SendCommand(normalized);
            sent.Add(normalized);
        }

        if (sent.Count == 0)
        {
            return;
        }

        // The history keeps what was typed, pipes and all, so recalling a
        // sequence brings back the whole sequence rather than its last line.
        string entry = string.Join(" | ", sent);
        if (_testCommandHistory.Count == 0 || _testCommandHistory[^1] != entry)
        {
            _testCommandHistory.Add(entry);
        }
        _testHistoryIndex = -1;

        ShowTestStatus(sent.Count == 1
            ? "Enviado: " + sent[0]
            : $"Enviados {sent.Count} comandos: {entry}");
    }

    private void ShowTestStatus(string text) => TesteStatusText.Text = text;

    private void TesteEnviarButton_Click(object sender, RoutedEventArgs e)
    {
        SendTestInput(TesteComandoTextBox.Text);
        TesteComandoTextBox.Clear();
    }

    private void TesteAtalho_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string command })
        {
            SendTestInput(command);
        }
    }

    /// <summary>
    /// The escape hatch for the sticky tiers. Typing FACE MUSIC or FACE
    /// PLAYING by hand leaves that tier set on Core until something clears
    /// it, and this app's own _mediaFaceActive/_gameFaceActive tracking has
    /// no idea it happened — so nothing would ever clear it. IDLE (the plain
    /// form) clears both lower tiers at once, and NEUTRAL releases the
    /// foreground one; see PROTOCOL.md's FACE priority notes.
    /// </summary>
    private void TesteLimpar_Click(object sender, RoutedEventArgs e)
    {
        if (!_connection.IsConnected)
        {
            ShowTestStatus("Sem conexão com o MiMo.");
            return;
        }

        _connection.SendCommand("FACE NEUTRAL");
        _connection.SendCommand("FACE IDLE");
        _connection.SendCommand("MSG ");
        ShowTestStatus("Estado limpo (FACE NEUTRAL + FACE IDLE + MSG vazio).");
    }

    /// <summary>
    /// Enter sends; Up/Down walks the history, which is what makes repeating
    /// a command with small variations bearable.
    /// </summary>
    private void TesteComandoTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SendTestInput(TesteComandoTextBox.Text);
            TesteComandoTextBox.Clear();
            e.Handled = true;
            return;
        }

        if (_testCommandHistory.Count == 0)
        {
            return;
        }

        if (e.Key == Key.Up)
        {
            _testHistoryIndex = _testHistoryIndex < 0
                ? _testCommandHistory.Count - 1
                : Math.Max(0, _testHistoryIndex - 1);
        }
        else if (e.Key == Key.Down)
        {
            if (_testHistoryIndex < 0)
            {
                return;
            }
            _testHistoryIndex++;
            if (_testHistoryIndex >= _testCommandHistory.Count)
            {
                // Walked off the newest entry: back to an empty prompt.
                _testHistoryIndex = -1;
                TesteComandoTextBox.Clear();
                e.Handled = true;
                return;
            }
        }
        else
        {
            return;
        }

        TesteComandoTextBox.Text = _testCommandHistory[_testHistoryIndex];
        TesteComandoTextBox.CaretIndex = TesteComandoTextBox.Text.Length;
        e.Handled = true;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Load-then-mutate rather than constructing a fresh SenderSettings:
        // this button only owns the checkbox/provider fields below, and must
        // not stomp fields other windows own (e.g. Theme/connection settings
        // from SettingsWindow) with their defaults.
        SenderSettings settings = SenderSettings.Load();
        settings.HoraEnabled = HoraCheckBox.IsChecked == true;
        settings.ClimaEnabled = ClimaCheckBox.IsChecked == true;
        settings.PausaEnabled = PausaCheckBox.IsChecked == true;
        if (TryParseTime(PausaManhaTextBox.Text, out _))
        {
            settings.PausaManha = PausaManhaTextBox.Text.Trim();
        }
        if (TryParseTime(PausaTardeTextBox.Text, out _))
        {
            settings.PausaTarde = PausaTardeTextBox.Text.Trim();
        }
        settings.PensamentosIaProvider = (PensamentosIaComboBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Claude";
        settings.MidiaEnabled = MediaCheckBox.IsChecked == true;
        settings.JogosEnabled = GameCheckBox.IsChecked == true;
        settings.SonsEnabled = SonsCheckBox.IsChecked == true;
        settings.ScanlinesEnabled = ScanlinesCheckBox.IsChecked == true;
        // Carried through rather than read off the UI: the address isn't
        // typed any more, and the sweep already writes it here the moment it
        // finds MiMo (see PersistDiscoveredAddress). This only matters for not
        // wiping it when the user saves the rest of the checklist.
        if (_coreHost != null)
        {
            settings.TcpHost = _coreHost;
            settings.TcpPort = _corePort;
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

        // The saved address is loaded for display and for SaveSettings to
        // carry through, but is deliberately NOT connected to: every launch
        // starts with a fresh sweep instead. Between one run and the next the
        // app may have been closed for days — long enough for MiMo to have
        // been given a different address, and for its old one to have been
        // handed to some other device. Asking the network beats trusting a
        // note from last time, and it costs ~2.5s once at startup.
        _coreHost = string.IsNullOrWhiteSpace(settings.TcpHost) ? null : settings.TcpHost;
        _corePort = settings.TcpPort > 0 ? settings.TcpPort : MimoDiscovery.DefaultPort;
        StartNetworkSweep(trustPreviousAddress: false);
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
        // Text set before IsChecked — PausaCheckBox_CheckedChanged reads
        // straight from these TextBoxes when it fires below.
        PausaManhaTextBox.Text = settings.PausaManha;
        PausaTardeTextBox.Text = settings.PausaTarde;
        PausaCheckBox.IsChecked = settings.PausaEnabled;
        MediaCheckBox.IsChecked = settings.MidiaEnabled;
        GameCheckBox.IsChecked = settings.JogosEnabled;
        SonsCheckBox.IsChecked = settings.SonsEnabled;
        ScanlinesCheckBox.IsChecked = settings.ScanlinesEnabled;

        if (settings.TestModeUnlocked)
        {
            ShowTestMode();
        }

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
        _statsMonitor?.Dispose();
        _weatherMonitor?.Dispose();
        _aiThoughtsListener?.Dispose();
        _clockTimer?.Stop();
        _breakTimer?.Stop();
        _connectionStatusTimer.Stop();
        // A sweep in flight holds up to MaxConcurrentProbes sockets open;
        // cancelling lets them close instead of lingering past shutdown.
        CancelNetworkSweep();
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

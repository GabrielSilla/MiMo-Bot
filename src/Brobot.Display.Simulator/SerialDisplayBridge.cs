using System.Globalization;
using System.Windows.Threading;
using Brobot.Connection;
using Brobot.Display.Abstractions;

namespace Brobot.Display.Simulator;

/// <summary>
/// Interprets Brobot Core's draw-command protocol (see PROTOCOL.md) as
/// calls on a <see cref="SimulatorDisplay"/>. Connecting to Core (serial or
/// TCP) and batching its lines into frames is <see cref="BrobotConnection"/>'s
/// job — this class only knows what the lines mean, not how they arrived.
///
/// Targets the concrete SimulatorDisplay (rather than IDisplay) only
/// because "PRESENT" needs SimulatorDisplay.Present(), which is specific to
/// its WPF bitmap backing and intentionally isn't part of the generic
/// IDisplay contract.
/// </summary>
public sealed class SerialDisplayBridge : IDisposable
{
    private readonly SimulatorDisplay _display;
    private readonly BrobotConnection _connection;

    public SerialDisplayBridge(SimulatorDisplay display, Dispatcher dispatcher)
    {
        _display = display;
        _connection = new BrobotConnection(dispatcher);
        _connection.FrameReceived += OnFrameReceived;
    }

    public bool IsConnected => _connection.IsConnected;
    public bool IsConnectingTcp => _connection.IsConnectingTcp;

    public static string[] GetAvailablePortNames() => BrobotConnection.GetAvailablePortNames();

    public void Connect(string portName, int baudRate = 115200) => _connection.ConnectSerial(portName, baudRate);

    /// <summary>Dev-only alternative to <see cref="Connect"/>: connects to a native (non-Arduino) BrobotCore build over loopback TCP. See BrobotCore/native/README.md.</summary>
    public void ConnectTcp(string host, int port) => _connection.ConnectTcp(host, port);

    /// <summary>Sends a raw control command line (e.g. "FACE HAPPY", "MSG Ola") to Core.</summary>
    public void SendCommand(string command) => _connection.SendCommand(command);

    public void Disconnect() => _connection.Disconnect();

    public void Dispose() => _connection.Dispose();

    private void OnFrameReceived(IReadOnlyList<string> frame)
    {
        foreach (string line in frame)
        {
            ProcessLine(line);
        }
    }

    private void ProcessLine(string rawLine)
    {
        string line = rawLine.TrimEnd('\r', '\n');
        if (line.Length == 0 || line[0] == '#')
        {
            return;
        }

        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return;
        }

        try
        {
            switch (parts[0])
            {
                case "CLR":
                    _display.ResetDrawOperationCount();
                    _display.Clear(ParseColor(parts, 1));
                    break;
                case "PIXEL":
                    _display.DrawPixel(ParseInt(parts[1]), ParseInt(parts[2]), ParseColor(parts, 3));
                    break;
                case "RECT":
                    _display.DrawRect(ParseInt(parts[1]), ParseInt(parts[2]), ParseInt(parts[3]), ParseInt(parts[4]), ParseColor(parts, 5));
                    break;
                case "FILLRECT":
                    _display.FillRect(ParseInt(parts[1]), ParseInt(parts[2]), ParseInt(parts[3]), ParseInt(parts[4]), ParseColor(parts, 5));
                    break;
                case "RRECT":
                    _display.DrawRoundedRect(ParseInt(parts[1]), ParseInt(parts[2]), ParseInt(parts[3]), ParseInt(parts[4]), ParseInt(parts[5]), ParseColor(parts, 6));
                    break;
                case "TEXT":
                    _display.DrawText(ExtractTextArgument(line), ParseInt(parts[1]), ParseInt(parts[2]), ParseColor(parts, 3), ParseFont(parts[6]));
                    break;
                case "PRESENT":
                    _display.Present();
                    break;
            }
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException)
        {
            // Malformed line from Core; drop it and keep listening.
        }
    }

    private static int ParseInt(string token) => int.Parse(token, CultureInfo.InvariantCulture);

    private static DisplayColor ParseColor(string[] parts, int startIndex) => new(
        byte.Parse(parts[startIndex], CultureInfo.InvariantCulture),
        byte.Parse(parts[startIndex + 1], CultureInfo.InvariantCulture),
        byte.Parse(parts[startIndex + 2], CultureInfo.InvariantCulture));

    private static TextFont ParseFont(string token) => token == "AUREBESH" ? TextFont.Aurebesh : TextFont.Latin;

    /// <summary>TEXT x y r g b font &lt;text...&gt; — the text itself may contain spaces, so it's taken verbatim after the 7th token.</summary>
    private static string ExtractTextArgument(string line)
    {
        int index = 0;
        for (int i = 0; i < 7; i++)
        {
            index = line.IndexOf(' ', index);
            if (index < 0)
            {
                return string.Empty;
            }
            index++;
        }

        return index <= line.Length ? line[index..] : string.Empty;
    }
}

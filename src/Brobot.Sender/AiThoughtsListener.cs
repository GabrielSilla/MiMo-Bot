using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Brobot.Sender;

/// <summary>One event pushed by an AI tool's hook: an event name and optional free-text.</summary>
public sealed record AiThoughtEvent(string Name, string? Text);

/// <summary>
/// Local TCP listener that receives one-line events from AI tool hooks (a
/// Claude Code hook command, initially — see CLAUDE.md's "Atividade da IA"
/// section) and raises them as <see cref="AiThoughtEvent"/> for MainWindow to
/// translate into FACE/MSG commands toward Core. This is the "per-tool hooks
/// → a local endpoint → FACE/MSG" bridge CLAUDE.md describes as not yet built.
///
/// Deliberately plain TCP, not HTTP: System.Net.HttpListener needs either
/// Administrator or a netsh URL ACL reservation to bind a prefix on Windows,
/// even a loopback one — not something a tray app should require. A raw
/// Socket/TcpListener bound to 127.0.0.1 needs neither. The wire format is
/// one line per event, "EVENTNAME optional free text..." — the same shape
/// PROTOCOL.md's own FACE/MSG lines use, just in the other direction, so a
/// hook command can be as simple as opening a socket and writing one line.
/// Unknown event names are the caller's problem, not this class's — it only
/// knows how to split a line into (name, text).
/// </summary>
public sealed class AiThoughtsListener : IDisposable
{
    private TcpListener? _listener;
    private Thread? _acceptThread;
    private volatile bool _running;

    public event Action<AiThoughtEvent>? ThoughtReceived;

    public void Start(int port)
    {
        Stop();

        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();

        _running = true;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "AiThoughtsListener-Accept" };
        _acceptThread.Start();
    }

    public void Stop()
    {
        _running = false;
        _listener?.Stop(); // unblocks a pending AcceptTcpClient in the accept thread
        _acceptThread?.Join(1000);
        _acceptThread = null;
        _listener = null;
    }

    public void Dispose() => Stop();

    private void AcceptLoop()
    {
        while (_running && _listener is { } listener)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return; // Stop() called (or the listener never started) — nothing more to accept
            }

            // A hook invocation is a short-lived process: connect, write one
            // line, exit. Handling it on a pool thread (rather than a thread
            // per connection, or handling it inline and blocking the accept
            // loop) keeps the accept loop free to take the next hook's
            // connection immediately, even if several fire in a burst.
            ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
        }
    }

    private void HandleClient(TcpClient client)
    {
        using (client)
        using (var reader = new StreamReader(client.GetStream()))
        {
            string? line;
            try
            {
                line = reader.ReadLine();
            }
            catch (IOException)
            {
                return;
            }

            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            int space = line.IndexOf(' ');
            string name = space < 0 ? line : line[..space];
            string? text = space < 0 ? null : line[(space + 1)..];

            ThoughtReceived?.Invoke(new AiThoughtEvent(name, text));
        }
    }
}

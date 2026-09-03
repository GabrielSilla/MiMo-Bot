using System.Collections.Concurrent;
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
    // A hook that connects but never writes must not wedge the queue behind
    // it, now that everything is read on one thread instead of many.
    private const int ReadTimeoutMs = 1000;

    private TcpListener? _listener;
    private Thread? _acceptThread;
    private Thread? _workerThread;
    private BlockingCollection<TcpClient>? _pendingClients;
    private volatile bool _running;

    private BlockingCollection<TcpClient> _pending =>
        _pendingClients ?? throw new InvalidOperationException("listener not started");

    public event Action<AiThoughtEvent>? ThoughtReceived;

    public void Start(int port)
    {
        Stop();

        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();

        _running = true;
        _pendingClients = new BlockingCollection<TcpClient>();
        _workerThread = new Thread(WorkerLoop) { IsBackground = true, Name = "AiThoughtsListener-Worker" };
        _workerThread.Start();
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "AiThoughtsListener-Accept" };
        _acceptThread.Start();
    }

    public void Stop()
    {
        _running = false;
        _listener?.Stop(); // unblocks a pending AcceptTcpClient in the accept thread
        _acceptThread?.Join(1000);
        _acceptThread = null;

        // CompleteAdding is what ends the worker's GetConsumingEnumerable —
        // done after the accept thread is gone, so nothing can still be
        // adding to a completed collection.
        _pendingClients?.CompleteAdding();
        _workerThread?.Join(1000);
        _workerThread = null;
        _pendingClients?.Dispose();
        _pendingClients = null;

        _listener = null;
    }

    /// <summary>
    /// Reads and raises accepted connections strictly in the order they were
    /// accepted — see the hand-off comment in <see cref="AcceptLoop"/> for why
    /// that ordering is load-bearing rather than incidental.
    /// </summary>
    private void WorkerLoop()
    {
        BlockingCollection<TcpClient>? pending = _pendingClients;
        if (pending == null)
        {
            return;
        }

        try
        {
            foreach (TcpClient client in pending.GetConsumingEnumerable())
            {
                HandleClient(client);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // Stop() disposed the collection out from under us — nothing left to drain.
        }
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

            // Accepted here, read on the single worker below — never on a
            // pool thread. Order matters in this stream: a burst of hooks is
            // a burst of *separate processes*, and dispatching each to the
            // pool let two of them be read and raised concurrently, so the
            // order MainWindow saw them in had nothing to do with the order
            // they arrived in. A session start is exactly such a burst
            // (SessionStart, the status line, and often the previous
            // session's SessionEnd, all within a few hundred ms), and
            // "SessionEnd applied after SessionStart" wipes the greeting
            // that just went up. This was a real bug, fixed once.
            //
            // The accept loop still doesn't block on reading — that's the
            // whole point of handing off — it just hands off to one consumer
            // instead of many.
            try
            {
                _pending.Add(client);
            }
            catch (InvalidOperationException)
            {
                client.Dispose(); // CompleteAdding raced with us; Stop() is winding down
                return;
            }
        }
    }

    private void HandleClient(TcpClient client)
    {
        using (client)
        {
            client.ReceiveTimeout = ReadTimeoutMs;
            using var reader = new StreamReader(client.GetStream());
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

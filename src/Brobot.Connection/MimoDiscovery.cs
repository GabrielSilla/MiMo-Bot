using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Brobot.Connection;

/// <summary>
/// Finds MiMo's current IP by sweeping the local network, because MiMo's
/// address isn't stable: it's handed out by the router's DHCP server, so a
/// power cycle (MiMo's or the router's) can move it, and the address saved
/// in Brobot.Sender's Conexão card silently stops working.
///
/// The sweep asks every host on the PC's own subnet(s) whether something is
/// listening on Core's protocol port, then asks each one that is to identify
/// itself with PING (see PROTOCOL.md) — a real MiMo answers "MIMO &lt;rev&gt;".
/// That second step is what keeps this from being a guess: port 5555 isn't
/// reserved for this project (Android's ADB-over-network, among others, uses
/// it too), and connecting to the wrong device would mean quietly sending it
/// FACE/MSG lines forever.
///
/// A board still running firmware from before PING existed answers nothing,
/// so an unconfirmed-but-listening host is accepted in exactly one case: it's
/// the address that was already being used. That address was vetted by
/// whoever typed it in and worked until now, so an old board sitting there
/// silently is far likelier than a coincidence — whereas an unconfirmed host
/// at a *new* address is exactly the coincidence this class exists to avoid,
/// and gets rejected. That distinction isn't theoretical: the network this
/// was developed on turned out to have an unrelated device answering on 5555,
/// which a "first open port wins" sweep would have adopted as MiMo.
///
/// The practical consequence is that relocating a MiMo whose IP changed
/// requires the PING firmware; without it, a sweep can only re-confirm an
/// address that already worked.
/// </summary>
public static class MimoDiscovery
{
    /// <summary>Core's own PROTOCOL_TCP_PORT (see BrobotCore/include/Config.h).</summary>
    public const int DefaultPort = 5555;

    // A sweep is ~254 hosts; probing them one at a time would take minutes,
    // since an address with nothing at it costs the full ConnectTimeout
    // (there's no host to refuse the connection, so it's ARP silence rather
    // than an RST). Wide-but-bounded keeps a full subnet at a few seconds
    // without opening hundreds of sockets at once. With the stagger below in
    // play this cap is rarely the binding constraint — it's a ceiling, not
    // the pace-setter.
    private const int MaxConcurrentProbes = 64;

    // Probes are launched this far apart rather than all at once. Nothing to
    // do with load: it's what makes progress readable. Launched simultaneously,
    // every probe against an empty address times out at the same instant, so a
    // sweep completes in slabs — measured, the count jumped 0, 63, 64, 127,
    // 128, 191, 192, 253 rather than climbing. Spacing the starts spaces the
    // finishes, so the count rises evenly at roughly 1000/ProbeLaunchStagger
    // per second. Costs about 600ms on a full sweep (the last probe still has
    // its own timeout to serve out) and nothing at all on the common case,
    // where the address probed first answers immediately.
    private static readonly TimeSpan ProbeLaunchStagger = TimeSpan.FromMilliseconds(10);

    // Refuse to sweep anything bigger than a /23. A misconfigured adapter (or
    // a plain /16 like APIPA's 169.254.0.0) would otherwise mean tens of
    // thousands of probes for a device that, by definition, has to be on the
    // same small home network as this PC.
    private const int MaxHostsPerSubnet = 512;

    // Generous next to a LAN round trip (sub-millisecond), because the cost
    // of being too impatient is asymmetric: a timeout that clips MiMo's reply
    // means not finding it at all, while a slow probe only holds one of
    // MaxConcurrentProbes slots.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan IdentityTimeout = TimeSpan.FromMilliseconds(800);

    private static readonly byte[] PingBytes = Encoding.ASCII.GetBytes("PING\n");
    private const string IdentityReply = "MIMO";

    /// <summary>
    /// One probed address, reported as the sweep goes. Exists so the UI can
    /// show the search actually working through the network instead of a bare
    /// spinner — a sweep is seconds long, and "it's checking 192.168.15.87 of
    /// 254" is the difference between waiting and wondering whether it hung.
    /// </summary>
    /// <param name="Address">The address whose probe just finished. Probes run in parallel, so these arrive roughly, not strictly, in order.</param>
    /// <param name="Completed">How many probes have finished, including this one.</param>
    /// <param name="Total">How many addresses the sweep will probe in all.</param>
    public readonly record struct SweepProgress(IPAddress Address, int Completed, int Total);

    private enum ProbeResult
    {
        /// <summary>Nothing answered at that address, or it refused the connection.</summary>
        Unreachable,

        /// <summary>Something is listening on the port, but never identified itself as MiMo.</summary>
        Listening,

        /// <summary>Answered PING with MIMO — this is definitely a Brobot Core.</summary>
        ConfirmedMimo,
    }

    /// <summary>
    /// Returns the IP of the first MiMo found, or null if the sweep turned up
    /// nothing (MiMo switched off, on another network, or this PC has no
    /// usable LAN adapter). Never throws for ordinary network failures.
    /// </summary>
    /// <param name="port">Core's protocol port — the one from the Conexão card, so a non-default port still gets swept.</param>
    /// <param name="preferredHost">The address that was being used until now, probed first: after a MiMo reboot that kept its lease, this hits on the very first probe and the rest of the sweep never runs.</param>
    /// <param name="progress">Reported once per finished probe. A <see cref="Progress{T}"/> created on the UI thread marshals these back to it on its own, so callers need no dispatching of their own.</param>
    /// <param name="cancellationToken">Cancelled when the user disconnects mid-sweep.</param>
    public static async Task<string?> FindAsync(
        int port, string? preferredHost, IProgress<SweepProgress>? progress, CancellationToken cancellationToken)
    {
        IReadOnlyList<IPAddress> candidates = BuildCandidates(preferredHost, out bool preferredIsFirstCandidate);
        if (candidates.Count == 0)
        {
            return null;
        }

        var sweepCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Deliberately not disposed with a using: probes still in flight hold
        // this semaphore when an early confirmed hit returns, and disposing it
        // out from under them throws ObjectDisposedException inside tasks
        // nobody is awaiting. SemaphoreSlim only needs disposal once its
        // AvailableWaitHandle has been touched, which this never does.
        var gate = new SemaphoreSlim(MaxConcurrentProbes);

        var all = new List<Task<(int Index, ProbeResult Result)>>(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            int index = i;
            all.Add(ProbeThroughGateAsync(candidates[index], port, index, gate, sweepCts.Token));
        }

        try
        {
            var pending = new List<Task<(int Index, ProbeResult Result)>>(all);

            // Set once the address that was already in use turns out to be
            // listening without identifying itself — an old board, most
            // likely. Only ever index 0, and only when index 0 is that
            // address: a host anywhere else that won't identify itself is
            // some other device, not a MiMo (see the class comment).
            bool previousAddressStillListening = false;
            int completed = 0;

            while (pending.Count > 0)
            {
                Task<(int Index, ProbeResult Result)> finished = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(finished);

                (int index, ProbeResult result) = await finished.ConfigureAwait(false);

                // Reported here rather than from inside the probes: this loop
                // already knows both which address finished and how many have,
                // so the probes need no progress plumbing of their own.
                completed++;
                progress?.Report(new SweepProgress(candidates[index], completed, candidates.Count));

                if (result == ProbeResult.ConfirmedMimo)
                {
                    // A confirmed MiMo beats anything still in flight, so
                    // there's no reason to finish sweeping the rest of the
                    // subnet — the finally block below tears the rest down.
                    return candidates[index].ToString();
                }

                if (result == ProbeResult.Listening && index == 0 && preferredIsFirstCandidate)
                {
                    previousAddressStillListening = true;
                }
            }

            return previousAddressStillListening ? candidates[0].ToString() : null;
        }
        finally
        {
            // Wind the sweep down before letting go of it, whether it found
            // something, found nothing, or the caller cancelled — every probe
            // is cancellation-aware, so this settles in well under a second
            // rather than leaving sockets open behind an already-returned result.
            sweepCts.Cancel();
            try
            {
                await Task.WhenAll(all).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Probes report failure as a ProbeResult rather than by
                // throwing; anything still surfacing here is a socket torn
                // down by the cancel above, which is what was wanted.
            }

            sweepCts.Dispose();
        }
    }

    private static async Task<(int Index, ProbeResult Result)> ProbeThroughGateAsync(
        IPAddress address, int port, int index, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        try
        {
            if (index > 0)
            {
                // Staggered by position, so probe 0 — the address most likely
                // to be MiMo — still fires instantly and can end the sweep
                // before anything else has even started.
                await Task.Delay(index * ProbeLaunchStagger, cancellationToken).ConfigureAwait(false);
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return (index, ProbeResult.Unreachable);
        }

        try
        {
            return (index, await ProbeAsync(address, port, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<ProbeResult> ProbeAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };

        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(address, port, connectCts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nothing there, connection refused, unroutable, or the sweep was
            // cancelled — all the same answer as far as the caller cares.
            return ProbeResult.Unreachable;
        }

        try
        {
            using var identityCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            identityCts.CancelAfter(IdentityTimeout);

            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(PingBytes, identityCts.Token).ConfigureAwait(false);

            var buffer = new byte[128];
            var reply = new StringBuilder();

            // Bounded by IdentityTimeout rather than by a line count: a
            // VSCREEN=1 Core is streaming draw commands down this same socket
            // (see PROTOCOL.md), so the answer can arrive with a frame's worth
            // of PIXEL/RECT lines around it.
            while (reply.Length < 4096)
            {
                int read = await stream.ReadAsync(buffer, identityCts.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                reply.Append(Encoding.ASCII.GetString(buffer, 0, read));
                if (reply.ToString().Contains(IdentityReply, StringComparison.Ordinal))
                {
                    return ProbeResult.ConfirmedMimo;
                }
            }
        }
        catch (Exception)
        {
            // Timed out waiting for an answer, or the peer hung up on being
            // sent something it didn't understand. Either way it's listening
            // but unidentified, which is what falls through below.
        }

        return ProbeResult.Listening;
    }

    /// <summary>
    /// Every address worth probing, in the order they should be tried:
    /// whatever was working before, then the rest of that address's subnet,
    /// then any other local subnet. The order decides how fast a hit comes
    /// back, and which host wins when more than one is listening.
    /// </summary>
    /// <param name="preferredIsFirstCandidate">True when preferredHost was usable and therefore sits at index 0 — which is what lets the caller tell "the old address is still listening" apart from "some other host is".</param>
    private static IReadOnlyList<IPAddress> BuildCandidates(string? preferredHost, out bool preferredIsFirstCandidate)
    {
        var ordered = new List<IPAddress>();
        var seen = new HashSet<uint>();

        void Add(IPAddress address)
        {
            if (seen.Add(ToUInt32(address)))
            {
                ordered.Add(address);
            }
        }

        IPAddress? preferred = null;
        if (!string.IsNullOrWhiteSpace(preferredHost)
            && IPAddress.TryParse(preferredHost.Trim(), out IPAddress? parsed)
            && parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            preferred = parsed;
            Add(parsed);
        }

        preferredIsFirstCandidate = ordered.Count > 0;

        IEnumerable<LocalSubnet> subnets = GetLocalSubnets();
        if (preferred != null)
        {
            uint preferredValue = ToUInt32(preferred);
            subnets = subnets.OrderByDescending(subnet => subnet.Contains(preferredValue));
        }

        foreach (LocalSubnet subnet in subnets)
        {
            foreach (IPAddress host in subnet.Hosts())
            {
                Add(host);
            }
        }

        return ordered;
    }

    private static List<LocalSubnet> GetLocalSubnets()
    {
        var subnets = new List<LocalSubnet>();

        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up
                || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = adapter.GetIPProperties();
            }
            catch (Exception)
            {
                // An adapter can disappear (a VPN dropping, a USB tether
                // unplugged) between being listed and being queried.
                continue;
            }

            // Requiring a default gateway is what separates the adapter that
            // actually reaches the house's WiFi from the pile of virtual ones
            // a dev machine accumulates (Hyper-V, WSL, VirtualBox host-only,
            // Docker) — those have addresses and netmasks but lead nowhere,
            // and sweeping them is pure delay.
            bool hasGateway = properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork
                && !gateway.Address.Equals(IPAddress.Any));
            if (!hasGateway)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask == null)
                {
                    continue;
                }

                uint mask = ToUInt32(unicast.IPv4Mask);
                uint own = ToUInt32(unicast.Address);
                if (mask == 0 || mask == uint.MaxValue)
                {
                    continue;
                }

                uint hostCount = ~mask;
                if (hostCount > MaxHostsPerSubnet)
                {
                    continue;
                }

                subnets.Add(new LocalSubnet(own & mask, mask, own));
            }
        }

        return subnets;
    }

    private static uint ToUInt32(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress ToAddress(uint value) => new(new[]
    {
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value,
    });

    /// <param name="Network">The subnet's base address (host bits cleared).</param>
    /// <param name="Mask">Its netmask, read off the adapter rather than assumed to be /24 — a router handing out a /23 is unusual but not this code's business to rule out.</param>
    /// <param name="Own">This PC's own address on it, skipped when enumerating since MiMo can't be at it.</param>
    private readonly record struct LocalSubnet(uint Network, uint Mask, uint Own)
    {
        public bool Contains(uint address) => (address & Mask) == Network;

        /// <summary>
        /// Every usable host address, ascending — the network and broadcast
        /// addresses excluded, since neither can be a device.
        /// </summary>
        public IEnumerable<IPAddress> Hosts()
        {
            uint broadcast = Network | ~Mask;
            for (uint address = Network + 1; address < broadcast; address++)
            {
                if (address != Own)
                {
                    yield return ToAddress(address);
                }
            }
        }
    }
}

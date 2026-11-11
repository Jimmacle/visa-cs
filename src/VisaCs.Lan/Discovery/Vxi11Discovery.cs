using System.Net;
using System.Net.Sockets;
using VisaCs.Internal;
using VisaCs.Lan.Rpc;

namespace VisaCs.Lan;

/// <summary>
/// Finds VXI-11 instruments by sending a portmapper GETPORT request for the VXI-11 core program over UDP, as broadcast
/// on every IPv4 interface and to given addresses, and collecting the hosts that answer.
/// </summary>
public static class Vxi11Discovery
{
    /// <summary>
    /// Sends the query and collects the addresses that answer for <paramref name="timeoutMs"/> milliseconds.
    /// </summary>
    /// <param name="timeoutMs">How long to listen for answers.</param>
    /// <param name="portmapperPort">The portmapper port to query.</param>
    /// <param name="targets">Addresses queried directly (unicast), in addition to the broadcast; may be null.</param>
    /// <param name="broadcast">Whether to broadcast the query on every IPv4 interface.</param>
    public static IReadOnlyList<IPAddress> Discover(int timeoutMs, int portmapperPort = Portmapper.DefaultPort, IEnumerable<IPAddress>? targets = null,
        bool broadcast = true) =>
        AsyncIO.Complete(DiscoverAsync(timeoutMs, portmapperPort, targets, broadcast, CancellationToken.None, async: false));

    /// <summary>
    /// Like <see cref="Discover"/>, without holding a thread while waiting for answers. Cancelling
    /// <paramref name="cancellationToken"/> ends the wait early with <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<IReadOnlyList<IPAddress>> DiscoverAsync(int timeoutMs, int portmapperPort = Portmapper.DefaultPort,
        IEnumerable<IPAddress>? targets = null, bool broadcast = true, CancellationToken cancellationToken = default)
    {
        var result = await DiscoverAsync(timeoutMs, portmapperPort, targets, broadcast, cancellationToken, async: true).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static async ValueTask<IReadOnlyList<IPAddress>> DiscoverAsync(int timeoutMs, int portmapperPort, IEnumerable<IPAddress>? extraTargets,
        bool broadcast, CancellationToken cancellationToken, bool async)
    {
        var found = new List<IPAddress>();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.EnableBroadcast = true;
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        uint xid = OncRpc.NewXidBase();
        var datagram = Portmapper.BuildGetPortDatagram(xid, Vxi11Protocol.CoreProgram, Vxi11Protocol.Version);

        var targets = new HashSet<IPAddress>(extraTargets ?? []);
        if (broadcast)
        {
            targets.Add(IPAddress.Broadcast);
            foreach (var b in LanInterfaces.BroadcastAddresses()) targets.Add(b);
        }
        foreach (var target in targets)
        {
            try { socket.SendTo(datagram, new IPEndPoint(target, portmapperPort)); }
            catch (SocketException) { /* unreachable network */ }
        }

        var deadline = Deadline.FromMilliseconds(timeoutMs).WithCancellation(cancellationToken);
        using var cts = async ? deadline.CreateTokenSource() : null;
        var buffer = new byte[1500];
        while (!deadline.HasExpired)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int n;
            try
            {
                if (async)
                {
                    var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, from, cts?.Token ?? default).ConfigureAwait(false);
                    (n, from) = (received.ReceivedBytes, received.RemoteEndPoint);
                }
                else
                {
                    if (!socket.Poll(Math.Max(1, deadline.RemainingMilliseconds) * 1000, SelectMode.SelectRead)) break;
                    n = socket.ReceiveFrom(buffer, ref from);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }
            try
            {
                var reader = OncRpc.ParseReply(buffer, n, xid);
                if (reader is null) continue;
                uint port = reader.ReadUInt32();
                var address = NetUtil.Normalize(((IPEndPoint)from).Address);
                if (port != 0 && !found.Contains(address)) found.Add(address);
            }
            catch (VisaException)
            {
                // Not a valid reply.
            }
        }
        return found;
    }
}

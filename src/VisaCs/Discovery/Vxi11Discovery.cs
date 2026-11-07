using System.Net;
using System.Net.Sockets;
using VisaCs.Internal;
using VisaCs.Protocols.Rpc;
using VisaCs.Protocols.Vxi11;

namespace VisaCs.Discovery;

/// <summary>Finds VXI-11 instruments by broadcasting a portmapper GETPORT request for the VXI-11 core program.</summary>
internal static class Vxi11Discovery
{
    public static List<IPAddress> Discover(int timeoutMs, int portmapperPort, IEnumerable<IPAddress> extraTargets, bool broadcast = true) =>
        AsyncIO.Complete(DiscoverAsync(timeoutMs, portmapperPort, extraTargets, broadcast, CancellationToken.None, async: false));

    /// <summary>Broadcasts the query and collects replies until the timeout (or until <paramref name="cancellationToken"/> fires).</summary>
    public static async ValueTask<List<IPAddress>> DiscoverAsync(int timeoutMs, int portmapperPort, IEnumerable<IPAddress> extraTargets,
        bool broadcast, CancellationToken cancellationToken, bool async)
    {
        var found = new List<IPAddress>();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.EnableBroadcast = true;
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        uint xid = OncRpc.NewXidBase();
        var datagram = Portmapper.BuildGetPortDatagram(xid, Vxi11Constants.CoreProgram, Vxi11Constants.Version);

        var targets = new HashSet<IPAddress>(extraTargets);
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

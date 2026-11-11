using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using VisaCs.Internal;
using VisaCs.Lan;
using VisaCs.Lan.Protocols;

namespace VisaCs.Simulation;

/// <summary>
/// Blocking forms of the library's protocol helpers, for the simulator's thread-per-connection servers. They call
/// the shared implementations with <c>async: false</c>.
/// </summary>
internal static class SyncProtocolIO
{
    // ------------------------------------------------------------------ HiSLIP

    public static void SendHislip(HislipChannel channel, HislipMessageType type, byte control, uint parameter, ReadOnlySpan<byte> payload) =>
        AsyncIO.Complete(Hislip.SendAsync(channel, type, control, parameter, payload.ToArray(), Deadline.Infinite, async: false));

    public static HislipMessage ReceiveHislip(HislipChannel channel) =>
        AsyncIO.Complete(Hislip.ReceiveAsync(channel, Deadline.Infinite, async: false));

    public static void ReceiveExact(this HislipChannel channel, Span<byte> buffer)
    {
        var temp = new byte[buffer.Length];
        AsyncIO.Complete(channel.ReceiveExactAsync(temp, Deadline.Infinite, async: false));
        temp.CopyTo(buffer);
    }

    public static void Skip(this HislipChannel channel, long count) =>
        AsyncIO.Complete(channel.SkipAsync(count, Deadline.Infinite, async: false));

    public static TlsHislipChannel AuthenticateAsServer(Socket socket, SslServerAuthenticationOptions options, Deadline deadline) =>
        AsyncIO.Complete(TlsHislipChannel.AuthenticateAsServerAsync(socket, options, deadline, async: false));

    public static PlainHislipChannel EndTls(this TlsHislipChannel channel, Deadline deadline) =>
        AsyncIO.Complete(channel.EndTlsAsync(deadline, async: false));

    public static byte[] EncodeDescriptors(IEnumerable<HislipDescriptor> descriptors)
    {
        var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[3];
        foreach (var d in descriptors)
        {
            BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)d.Content.Length);
            header[2] = (byte)d.Type;
            ms.Write(header);
            ms.Write(d.Content);
        }
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ sockets

    /// <summary>Receives exactly <paramref name="buffer"/>.Length bytes from a (blocking) accepted socket.</summary>
    public static void ReceiveExact(Socket socket, byte[] buffer) =>
        AsyncIO.Complete(NetUtil.ReceiveExactAsync(socket, buffer, Deadline.Infinite, async: false));
}

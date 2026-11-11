using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VisaCs.Internal;

namespace VisaCs.Lan;

/// <summary>A DNS-SD service instance found with mDNS.</summary>
/// <param name="ServiceType">The service type it was found under, e.g. <c>_hislip._tcp.local</c>.</param>
/// <param name="InstanceName">The instance name without the service type, e.g. <c>Keysight DSOX1204G</c>.</param>
/// <param name="HostName">The target host of its SRV record, e.g. <c>scope.local.</c>.</param>
/// <param name="Port">The port of its SRV record.</param>
/// <param name="Addresses">The host's addresses sent with the answer; may be empty.</param>
/// <param name="Text">The key/value pairs of its TXT record (keys case-insensitive).</param>
public sealed record MdnsServiceInstance(string ServiceType, string InstanceName, string HostName, int Port,
    IReadOnlyList<IPAddress> Addresses, IReadOnlyDictionary<string, string> Text);

/// <summary>DNS-SD service types that instruments advertise.</summary>
public static class MdnsServiceTypes
{
    /// <summary>LXI instruments (web interface).</summary>
    public const string Lxi = "_lxi._tcp.local";
    /// <summary>HiSLIP servers.</summary>
    public const string Hislip = "_hislip._tcp.local";
    /// <summary>VXI-11 servers.</summary>
    public const string Vxi11 = "_vxi-11._tcp.local";
    /// <summary>Raw SCPI sockets.</summary>
    public const string ScpiRaw = "_scpi-raw._tcp.local";
}

/// <summary>
/// One-shot mDNS / DNS-SD browser (RFC 6762/6763) for finding LAN instruments: sends one query on every IPv4
/// interface and collects the answers until the timeout.
/// </summary>
public static class MdnsDiscovery
{
    /// <summary>The mDNS multicast group and port (224.0.0.251:5353).</summary>
    public static IPEndPoint MulticastEndPoint => new(IPAddress.Parse("224.0.0.251"), 5353);

    private const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeAaaa = 28, TypeSrv = 33, ClassIn = 1;

    /// <summary>
    /// Sends one query for the given service types (see <see cref="MdnsServiceTypes"/>) and collects the answers for
    /// <paramref name="timeoutMilliseconds"/> milliseconds (0: only answers already received).
    /// <paramref name="target"/> overrides the multicast destination (for example a unicast responder in a test).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeoutMilliseconds"/> is negative: the timeout is
    /// how long to listen, so it cannot be unlimited here.</exception>
    public static IReadOnlyList<MdnsServiceInstance> Browse(IReadOnlyList<string> serviceTypes, int timeoutMilliseconds, IPEndPoint? target = null)
    {
        ArgumentNullException.ThrowIfNull(serviceTypes);
        ArgumentOutOfRangeException.ThrowIfNegative(timeoutMilliseconds);
        return AsyncIO.Complete(BrowseAsync(serviceTypes, timeoutMilliseconds, target, CancellationToken.None, async: false));
    }

    /// <summary>
    /// Like <see cref="Browse"/>, without holding a thread while waiting for answers. Cancelling
    /// <paramref name="cancellationToken"/> ends the wait early with <see cref="OperationCanceledException"/>. A negative
    /// <paramref name="timeoutMilliseconds"/> listens until the token is cancelled.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeoutMilliseconds"/> is negative and
    /// <paramref name="cancellationToken"/> cannot be cancelled.</exception>
    public static Task<IReadOnlyList<MdnsServiceInstance>> BrowseAsync(IReadOnlyList<string> serviceTypes, int timeoutMilliseconds,
        IPEndPoint? target = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceTypes);
        if (timeoutMilliseconds < 0 && !cancellationToken.CanBeCanceled)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds), timeoutMilliseconds,
                "A negative timeout (listen until cancelled) needs a cancellable token.");
        return BrowseCheckedAsync(serviceTypes, timeoutMilliseconds, target, cancellationToken);
    }

    private static async Task<IReadOnlyList<MdnsServiceInstance>> BrowseCheckedAsync(IReadOnlyList<string> serviceTypes, int timeoutMilliseconds,
        IPEndPoint? target, CancellationToken cancellationToken)
    {
        var result = await BrowseAsync(serviceTypes, timeoutMilliseconds, target, cancellationToken, async: true).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static async ValueTask<IReadOnlyList<MdnsServiceInstance>> BrowseAsync(IReadOnlyList<string> serviceTypes, int timeoutMilliseconds,
        IPEndPoint? target, CancellationToken cancellationToken, bool async)
    {
        ArgumentNullException.ThrowIfNull(serviceTypes);
        var query = BuildQuery(serviceTypes);
        var sockets = new List<Socket>();
        try
        {
            if (target is not null)
            {
                var s = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                s.Bind(new IPEndPoint(target.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
                sockets.Add(s);
                s.SendTo(query, target);
            }
            else
            {
                var multicast = MulticastEndPoint;
                foreach (var (address, _) in LanInterfaces.IPv4Interfaces())
                {
                    var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    try
                    {
                        s.Bind(new IPEndPoint(address, 0));
                        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                        s.SendTo(query, multicast);
                        sockets.Add(s);
                    }
                    catch (SocketException)
                    {
                        s.Dispose();
                    }
                }
            }

            var records = new List<Record>();
            var deadline = Deadline.FromMilliseconds(timeoutMilliseconds).WithCancellation(cancellationToken);
            if (async)
            {
                // One receive loop per interface socket; all of them end when the deadline passes.
                using var cts = deadline.CreateTokenSource() ?? new CancellationTokenSource();
                await Task.WhenAll(sockets.Select(s => ReceiveRecordsAsync(s, records, cts.Token))).ConfigureAwait(false);
                return Assemble(serviceTypes, records);
            }

            var buffer = new byte[9000];
            while (!deadline.HasExpired && sockets.Count > 0)
            {
                var readable = new List<Socket>(sockets);
                Socket.Select(readable, null, null, Math.Max(1, deadline.RemainingMilliseconds) * 1000);
                if (readable.Count == 0) break;
                foreach (var s in readable)
                {
                    try
                    {
                        int n = s.Receive(buffer);
                        records.AddRange(ParseRecords(buffer.AsSpan(0, n).ToArray()));
                    }
                    catch (Exception ex) when (ex is SocketException or IndexOutOfRangeException or ArgumentException)
                    {
                        // Malformed packet or ICMP error: ignore.
                    }
                }
            }
            return Assemble(serviceTypes, records);
        }
        finally
        {
            foreach (var s in sockets) s.Dispose();
        }
    }

    private static async Task ReceiveRecordsAsync(Socket socket, List<Record> records, CancellationToken stop)
    {
        var buffer = new byte[9000];
        while (true)
        {
            try
            {
                int n = await socket.ReceiveAsync(buffer, SocketFlags.None, stop).ConfigureAwait(false);
                var parsed = ParseRecords(buffer.AsSpan(0, n).ToArray());
                lock (records) records.AddRange(parsed);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or IndexOutOfRangeException or ArgumentException)
            {
                // Malformed packet or ICMP error: ignore.
            }
        }
    }

    internal static byte[] BuildQuery(IReadOnlyList<string> serviceTypes)
    {
        var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        header.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], (ushort)serviceTypes.Count);
        ms.Write(header);
        Span<byte> q = stackalloc byte[4];
        foreach (var type in serviceTypes)
        {
            WriteName(ms, type);
            BinaryPrimitives.WriteUInt16BigEndian(q, TypePtr);
            BinaryPrimitives.WriteUInt16BigEndian(q[2..], 0x8000 | ClassIn); // QU: ask for a unicast reply
            ms.Write(q);
        }
        return ms.ToArray();
    }

    internal static void WriteName(Stream stream, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }
        stream.WriteByte(0);
    }

    internal sealed record Record(string Name, ushort Type, byte[] Message, int DataOffset, int DataLength);

    internal static List<Record> ParseRecords(byte[] message)
    {
        var records = new List<Record>();
        if (message.Length < 12) return records;
        int qd = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(4));
        int total = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(6)) + BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(8)) +
                    BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(10));
        int offset = 12;
        for (int i = 0; i < qd; i++)
        {
            ReadName(message, ref offset);
            offset += 4;
        }
        for (int i = 0; i < total && offset + 10 <= message.Length; i++)
        {
            string name = ReadName(message, ref offset);
            if (offset + 10 > message.Length) break;
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset));
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset + 8));
            offset += 10;
            if (offset + length > message.Length) break;
            records.Add(new Record(name, type, message, offset, length));
            offset += length;
        }
        return records;
    }

    /// <summary>Reads a possibly compressed DNS name. A truncated name throws <see cref="IndexOutOfRangeException"/> or <see cref="ArgumentException"/>.</summary>
    internal static string ReadName(byte[] message, ref int offset)
    {
        var labels = new List<string>();
        int position = offset;
        bool jumped = false;
        for (int guard = 0; guard < 128; guard++)
        {
            int length = message[position];
            if (length == 0)
            {
                position++;
                break;
            }
            if ((length & 0xC0) == 0xC0)
            {
                int pointer = ((length & 0x3F) << 8) | message[position + 1];
                if (!jumped) offset = position + 2;
                jumped = true;
                position = pointer;
                continue;
            }
            labels.Add(Encoding.UTF8.GetString(message, position + 1, length));
            position += length + 1;
        }
        if (!jumped) offset = position;
        return string.Join('.', labels);
    }

    /// <summary>
    /// Builds the service instances from the collected records. A malformed record (from any responder on the network)
    /// only drops the instance it belongs to.
    /// </summary>
    internal static List<MdnsServiceInstance> Assemble(IReadOnlyList<string> serviceTypes, List<Record> records)
    {
        var result = new List<MdnsServiceInstance>();
        var hostAddresses = new Dictionary<string, List<IPAddress>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in records)
        {
            if (r.Type == TypeA && r.DataLength == 4 || r.Type == TypeAaaa && r.DataLength == 16)
            {
                var ip = new IPAddress(r.Message.AsSpan(r.DataOffset, r.DataLength));
                if (!hostAddresses.TryGetValue(r.Name, out var list)) hostAddresses[r.Name] = list = new();
                if (!list.Contains(ip)) list.Add(ip);
            }
        }

        foreach (var type in serviceTypes)
        {
            string service = type.TrimEnd('.');
            var instances = new List<string>();
            foreach (var r in records)
            {
                if (r.Type != TypePtr || !r.Name.Equals(service, StringComparison.OrdinalIgnoreCase)) continue;
                if (TryReadName(r, 0, out var instance) && !instances.Contains(instance, StringComparer.OrdinalIgnoreCase))
                    instances.Add(instance);
            }
            foreach (var instance in instances)
            {
                if (TryDescribe(service, instance, records, hostAddresses) is { } found) result.Add(found);
            }
        }
        return result;
    }

    private static MdnsServiceInstance? TryDescribe(string service, string instance, List<Record> records,
        Dictionary<string, List<IPAddress>> hostAddresses)
    {
        // SRV data: priority (2), weight (2), port (2), target name.
        var srv = records.FirstOrDefault(r => r.Type == TypeSrv && r.DataLength >= 7 && r.Name.Equals(instance, StringComparison.OrdinalIgnoreCase));
        if (srv is null) return null;
        int port = BinaryPrimitives.ReadUInt16BigEndian(srv.Message.AsSpan(srv.DataOffset + 4));
        if (!TryReadName(srv, 6, out var host)) return null;

        var text = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var txt = records.FirstOrDefault(r => r.Type == TypeTxt && r.Name.Equals(instance, StringComparison.OrdinalIgnoreCase));
        if (txt is not null)
        {
            int p = txt.DataOffset, end = txt.DataOffset + txt.DataLength;
            while (p < end)
            {
                int len = txt.Message[p++];
                if (p + len > end) break;
                var entry = Encoding.UTF8.GetString(txt.Message, p, len);
                p += len;
                int eq = entry.IndexOf('=');
                if (eq > 0) text[entry[..eq]] = entry[(eq + 1)..];
                else if (entry.Length > 0) text[entry] = "";
            }
        }
        hostAddresses.TryGetValue(host, out var addresses);
        string label = instance.EndsWith(service, StringComparison.OrdinalIgnoreCase)
            ? instance[..^service.Length].TrimEnd('.')
            : instance;
        return new MdnsServiceInstance(service, label, host, port, (IReadOnlyList<IPAddress>?)addresses ?? Array.Empty<IPAddress>(), text);
    }

    /// <summary>Reads a name stored in a record's data at <paramref name="start"/>; false if it is malformed.</summary>
    private static bool TryReadName(Record record, int start, out string name)
    {
        name = "";
        if (start >= record.DataLength) return false;
        try
        {
            int offset = record.DataOffset + start;
            name = ReadName(record.Message, ref offset);
            return name.Length > 0;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
        {
            return false;
        }
    }
}

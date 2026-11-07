using System.Net.Sockets;
using VisaCs.Internal;

namespace VisaCs;

/// <summary>Raw TCP socket session (<c>TCPIP::host::port::SOCKET</c>), e.g. SCPI-RAW on port 5025.</summary>
/// <remarks>
/// <para>
/// Sockets have no END indicator, so reads complete on the termination character. Unlike VISA's
/// default, <see cref="MessageBasedSession.TerminationCharacterEnabled"/> starts as true for this class
/// so that <c>Query</c> works out of the box.
/// </para>
/// <para>
/// A stream has no device clear: <see cref="MessageBasedSession.Clear"/> only discards input that has already arrived
/// (and sends <c>*CLS</c> with <see cref="IOProtocol.Ieee4882Strings"/>). After a timeout or cancellation the late
/// response is still on its way; a write cut off by a timeout has sent part of its data.
/// </para>
/// </remarks>
public sealed class TcpipSocketSession : MessageBasedSession
{
    private readonly TcpipSocketResourceName _name;
    private Socket? _socket;

    internal TcpipSocketSession(ResourceManager manager, TcpipSocketResourceName name) : base(manager, name)
    {
        _name = name;
        TerminationCharacterEnabled = true;
    }

    /// <summary>Remote IP address (VI_ATTR_TCPIP_ADDR).</summary>
    public string Address { get; private set; } = "";

    /// <summary>Host name as given in the resource string (VI_ATTR_TCPIP_HOSTNAME).</summary>
    public string HostName => _name.Host;

    /// <summary>Remote port (VI_ATTR_TCPIP_PORT).</summary>
    public int Port => _name.Port;

    /// <summary>Disables Nagle's algorithm (VI_ATTR_TCPIP_NODELAY). Default true.</summary>
    public bool NoDelay
    {
        get => Socket.NoDelay;
        set => Socket.NoDelay = value;
    }

    /// <summary>TCP keep-alive (VI_ATTR_TCPIP_KEEPALIVE). Default false.</summary>
    public bool KeepAlive
    {
        get => NetUtil.GetKeepAlive(Socket);
        set => NetUtil.SetKeepAlive(Socket, value);
    }

    /// <inheritdoc/>
    public override bool Is4882Compliant => false;

    private Socket Socket => _socket ?? throw new VisaException(VisaStatus.ErrorInvalidObject, "The session is not open.");

    private protected override bool HasEndIndicator => false;

    private protected override async ValueTask OpenCoreAsync(int connectTimeoutMs, CancellationToken cancellationToken, bool async)
    {
        _socket = await NetUtil.ConnectAsync(_name.Host, _name.Port, Deadline.FromMilliseconds(connectTimeoutMs).WithCancellation(cancellationToken),
            async).ConfigureAwait(false);
        Address = NetUtil.RemoteAddressText(_socket);
    }

    private protected override ValueTask CloseCoreAsync(bool async)
    {
        NetUtil.SafeClose(_socket);
        return default;
    }

    private protected override ValueTask WriteCoreAsync(ReadOnlyMemory<byte> data, bool end, Deadline deadline, bool async) =>
        NetUtil.SendAllAsync(Socket, data, deadline, async);

    private protected override async ValueTask<FillResult> FillCoreAsync(Memory<byte> target, Deadline deadline, bool async) =>
        new(await NetUtil.ReceiveSomeAsync(Socket, target, deadline, async).ConfigureAwait(false), false);

    private protected override ValueTask<byte> ReadStatusByteCoreAsync(Deadline deadline, bool async) =>
        QueryStatusByteWithStringsAsync(deadline, async);

    private protected override ValueTask ClearCoreAsync(Deadline deadline, bool async)
    {
        NetUtil.Drain(Socket);
        return IOProtocol == IOProtocol.Ieee4882Strings ? WriteCoreAsync("*CLS\n"u8.ToArray(), true, deadline, async) : default;
    }

    private protected override bool SupportsIOProtocol(IOProtocol protocol) => protocol is IOProtocol.Normal or IOProtocol.Ieee4882Strings;

    private protected override ValueTask TriggerCoreAsync(Deadline deadline, bool async) => WriteCoreAsync("*TRG\n"u8.ToArray(), true, deadline, async);

    private protected override void FlushTransportInput() => NetUtil.Drain(Socket);

    private protected override void RegisterAttributes(AttributeTable table)
    {
        base.RegisterAttributes(table);
        table.Add(VisaAttribute.TcpipAddress, () => Address);
        table.Add(VisaAttribute.TcpipHostName, () => HostName);
        table.Add(VisaAttribute.TcpipPort, () => Port);
        table.Add(VisaAttribute.TcpipNoDelay, () => NoDelay, v => NoDelay = v);
        table.Add(VisaAttribute.TcpipKeepAlive, () => KeepAlive, v => KeepAlive = v);
    }
}

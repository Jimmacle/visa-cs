using VisaCs.Internal;
using VisaCs.Lan;

namespace VisaCs;

/// <summary>Teledyne LeCroy VICP session (<c>VICP::host::INSTR</c>, TCP port 1861).</summary>
/// <remarks>
/// VICP frames each block with an EOI flag, so reads end on END. Status byte, device clear and remote/local use VICP's
/// own operations; a device clear reconnects the socket, as VICP requires. Service requests arrive in-band and are only
/// noticed while the session is reading. After a timeout the late response is still on its way; call
/// <see cref="MessageBasedSession.Clear"/> before the next command. The protocol itself is <see cref="VicpClient"/>
/// from <c>VisaCs.Lan</c>.
/// </remarks>
public sealed class VicpSession : MessageBasedSession
{
    private readonly VicpResourceName _name;
    private VicpClient? _client;

    internal VicpSession(ResourceManager manager, VicpResourceName name) : base(manager, name, 256 * 1024) => _name = name;

    /// <summary>Remote IP address (VI_ATTR_TCPIP_ADDR).</summary>
    public string Address => _client?.RemoteAddress.ToString() ?? "";

    /// <summary>Host name as given in the resource name (VI_ATTR_TCPIP_HOSTNAME).</summary>
    public string HostName => _name.Host;

    /// <summary>VICP port (1861 unless overridden in the options).</summary>
    public int Port => ResourceManager.Options.VicpPort;

    /// <inheritdoc/>
    public override IReadOnlyCollection<EventType> SupportedEventTypes { get; } = [EventType.ServiceRequest];

    private VicpClient Client => _client ?? throw new VisaException(VisaStatus.ErrorInvalidObject, "The session is not open.");

    private protected override async ValueTask OpenCoreAsync(int connectTimeoutMs, CancellationToken cancellationToken, bool async)
    {
        _client = async
            ? await VicpClient.ConnectAsync(_name.Host, connectTimeoutMs, Port, cancellationToken).ConfigureAwait(false)
            : VicpClient.Connect(_name.Host, connectTimeoutMs, Port);
        _client.ServiceRequest += () => RaiseEvent(new ServiceRequestEventArgs());
    }

    private protected override ValueTask CloseCoreAsync(bool async)
    {
        _client?.Dispose();
        return default;
    }

    private protected override ValueTask WriteCoreAsync(ReadOnlyMemory<byte> data, bool end, Deadline deadline, bool async)
    {
        if (async) return Client.WriteAsync(data, end, deadline.RemainingMilliseconds, deadline.CancellationToken);
        Client.Write(data, end, deadline.RemainingMilliseconds);
        return default;
    }

    private protected override async ValueTask<FillResult> FillCoreAsync(Memory<byte> target, Deadline deadline, bool async)
    {
        var (count, end) = async
            ? await Client.ReadAsync(target, deadline.RemainingMilliseconds, deadline.CancellationToken).ConfigureAwait(false)
            : Client.Read(target, deadline.RemainingMilliseconds);
        return new FillResult(count, end);
    }

    private protected override ValueTask<byte> ReadStatusByteCoreAsync(Deadline deadline, bool async)
    {
        DiscardInputBuffer();
        return async
            ? Client.ReadStatusByteAsync(deadline.RemainingMilliseconds, deadline.CancellationToken)
            : new(Client.ReadStatusByte(deadline.RemainingMilliseconds));
    }

    private protected override ValueTask ClearCoreAsync(Deadline deadline, bool async)
    {
        if (async) return Client.DeviceClearAsync(deadline.RemainingMilliseconds, deadline.CancellationToken);
        Client.DeviceClear(deadline.RemainingMilliseconds);
        return default;
    }

    private protected override ValueTask TriggerCoreAsync(Deadline deadline, bool async) => WriteCoreAsync("*TRG\n"u8.ToArray(), true, deadline, async);

    private protected override ValueTask ControlRenCoreAsync(RemoteLocalMode mode, Deadline deadline, bool async)
    {
        bool remote = mode is RemoteLocalMode.Assert or RemoteLocalMode.AssertAddress or RemoteLocalMode.AssertLocalLockout or
            RemoteLocalMode.AssertAddressLocalLockout;
        bool lockout = mode is RemoteLocalMode.AssertLocalLockout or RemoteLocalMode.AssertAddressLocalLockout;
        if (async) return Client.SetRemoteAsync(remote, lockout, deadline.RemainingMilliseconds, deadline.CancellationToken);
        Client.SetRemote(remote, lockout, deadline.RemainingMilliseconds);
        return default;
    }

    private protected override void FlushTransportInput() => Client.DiscardInput();

    private protected override void RegisterAttributes(AttributeTable table)
    {
        base.RegisterAttributes(table);
        table.Add(VisaAttribute.TcpipAddress, () => Address);
        table.Add(VisaAttribute.TcpipHostName, () => HostName);
        table.Add(VisaAttribute.TcpipPort, () => Port);
    }
}

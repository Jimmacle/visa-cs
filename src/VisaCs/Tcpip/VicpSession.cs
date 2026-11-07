using System.Buffers.Binary;
using System.Net.Sockets;
using VisaCs.Internal;

namespace VisaCs;

/// <summary>Teledyne LeCroy VICP session (<c>VICP::host::INSTR</c>, TCP port 1861).</summary>
/// <remarks>
/// VICP frames each block with an EOI flag, so reads end on END. Status byte, device clear and remote/local use VICP's
/// own operations; a device clear reconnects the socket, as VICP requires. Service requests arrive in-band and are only
/// noticed while the session is reading. After a timeout the late response is still on its way; call
/// <see cref="MessageBasedSession.Clear"/> before the next command.
/// </remarks>
public sealed class VicpSession : MessageBasedSession
{
    private const byte OpData = 0x80;
    private const byte OpRemote = 0x40;
    private const byte OpLockout = 0x20;
    private const byte OpClear = 0x10;
    private const byte OpSrq = 0x08;
    private const byte OpSerialPoll = 0x04;
    private const byte OpEoi = 0x01;
    private const byte HeaderVersion = 0x01;
    private const int MaxSrqPayload = 16;

    private readonly VicpResourceName _name;
    private readonly byte[] _header = new byte[8];
    private readonly byte[] _sendHeader = new byte[8];
    private int _headerFilled;
    private long _blockRemaining;
    private bool _blockEoi;
    private Socket? _socket;
    private byte _sequence;
    private bool _remote;
    private bool _lockout;
    private int _connectTimeout;

    internal VicpSession(ResourceManager manager, VicpResourceName name) : base(manager, name, 256 * 1024) => _name = name;

    /// <summary>Remote IP address (VI_ATTR_TCPIP_ADDR).</summary>
    public string Address { get; private set; } = "";

    /// <summary>Host name as given in the resource name (VI_ATTR_TCPIP_HOSTNAME).</summary>
    public string HostName => _name.Host;

    /// <summary>VICP port (1861 unless overridden in the options).</summary>
    public int Port => ResourceManager.Options.VicpPort;

    /// <inheritdoc/>
    public override IReadOnlyCollection<EventType> SupportedEventTypes { get; } = [EventType.ServiceRequest];

    private Socket Socket => _socket ?? throw new VisaException(VisaStatus.ErrorInvalidObject, "The session is not open.");

    private protected override ValueTask OpenCoreAsync(int connectTimeoutMs, CancellationToken cancellationToken, bool async)
    {
        _connectTimeout = connectTimeoutMs;
        return ConnectAsync(Deadline.FromMilliseconds(connectTimeoutMs).WithCancellation(cancellationToken), async);
    }

    private async ValueTask ConnectAsync(Deadline deadline, bool async)
    {
        _socket = await NetUtil.ConnectAsync(_name.Host, Port, deadline, async).ConfigureAwait(false);
        Address = NetUtil.RemoteAddressText(_socket);
        _headerFilled = 0;
        _blockRemaining = 0;
        _blockEoi = false;
    }

    private protected override ValueTask CloseCoreAsync(bool async)
    {
        NetUtil.SafeClose(_socket);
        return default;
    }

    private byte NextSequence()
    {
        _sequence = (byte)(_sequence == 255 ? 1 : _sequence + 1);
        return _sequence;
    }

    private async ValueTask SendBlockAsync(byte operation, ReadOnlyMemory<byte> data, Deadline deadline, bool async)
    {
        if (_remote) operation |= OpRemote;
        if (_lockout) operation |= OpLockout;
        _sendHeader[0] = operation;
        _sendHeader[1] = HeaderVersion;
        _sendHeader[2] = NextSequence();
        _sendHeader[3] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(_sendHeader.AsSpan(4), (uint)data.Length);
        await NetUtil.SendAllAsync(Socket, _sendHeader, deadline, async).ConfigureAwait(false);
        if (!data.IsEmpty) await NetUtil.SendAllAsync(Socket, data, deadline, async).ConfigureAwait(false);
    }

    private protected override ValueTask WriteCoreAsync(ReadOnlyMemory<byte> data, bool end, Deadline deadline, bool async) =>
        SendBlockAsync((byte)(OpData | (end ? OpEoi : 0)), data, deadline, async);

    private async ValueTask<(byte Operation, long Length)> ReadHeaderAsync(Deadline deadline, bool async)
    {
        while (_headerFilled < 8)
            _headerFilled += await NetUtil.ReceiveSomeAsync(Socket, _header.AsMemory(_headerFilled), deadline, async).ConfigureAwait(false);
        _headerFilled = 0;
        return (_header[0], BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(4)));
    }

    private protected override async ValueTask<FillResult> FillCoreAsync(Memory<byte> target, Deadline deadline, bool async)
    {
        while (true)
        {
            if (_blockRemaining == 0)
            {
                var (op, length) = await ReadHeaderAsync(deadline, async).ConfigureAwait(false);
                if ((op & OpSrq) != 0)
                {
                    // SRQ notification: one byte, '1' when asserted.
                    var payload = new byte[(int)Math.Min(length, MaxSrqPayload)];
                    await NetUtil.ReceiveExactAsync(Socket, payload, deadline, async).ConfigureAwait(false);
                    await NetUtil.SkipAsync(Socket, length - payload.Length, deadline, async).ConfigureAwait(false);
                    if (payload.Length > 0 && payload[0] == (byte)'1') RaiseEvent(new ServiceRequestEventArgs());
                    continue;
                }
                _blockRemaining = length;
                _blockEoi = (op & OpEoi) != 0;
                if (length == 0)
                {
                    if (_blockEoi) return new FillResult(0, true);
                    continue;
                }
            }
            int n = await NetUtil.ReceiveSomeAsync(Socket, target[..(int)Math.Min(target.Length, _blockRemaining)], deadline, async).ConfigureAwait(false);
            _blockRemaining -= n;
            return new FillResult(n, _blockRemaining == 0 && _blockEoi);
        }
    }

    private protected override async ValueTask<byte> ReadStatusByteCoreAsync(Deadline deadline, bool async)
    {
        DiscardInputBuffer();
        // Drop the rest of a block a previous read left unfinished, so the reply is not taken from it.
        if (_blockRemaining > 0)
        {
            await NetUtil.SkipAsync(Socket, _blockRemaining, deadline, async).ConfigureAwait(false);
            _blockRemaining = 0;
        }
        await SendBlockAsync(OpData | OpSerialPoll, ReadOnlyMemory<byte>.Empty, deadline, async).ConfigureAwait(false);
        var buffer = new byte[16];
        int total = 0;
        while (true)
        {
            var (n, end) = await FillCoreAsync(buffer.AsMemory(total), deadline, async).ConfigureAwait(false);
            total += n;
            if (end || total == buffer.Length) break;
        }
        if (total == 0) throw new VisaException(VisaStatus.ErrorIO, "Empty VICP serial poll response.");
        if (total > 1 && byte.TryParse(System.Text.Encoding.ASCII.GetString(buffer, 0, total).Trim(), out byte parsed)) return parsed;
        return buffer[0];
    }

    private protected override async ValueTask ClearCoreAsync(Deadline deadline, bool async)
    {
        await SendBlockAsync(OpData | OpClear, ReadOnlyMemory<byte>.Empty, deadline, async).ConfigureAwait(false);
        if (async) await Task.Delay(100, deadline.CancellationToken).ConfigureAwait(false);
        else Thread.Sleep(100);
        // The VICP 1a protocol requires reconnecting after a device clear.
        NetUtil.SafeClose(_socket);
        await ConnectAsync(Deadline.FromMilliseconds(_connectTimeout).WithCancellation(deadline.CancellationToken), async).ConfigureAwait(false);
    }

    private protected override ValueTask TriggerCoreAsync(Deadline deadline, bool async) => WriteCoreAsync("*TRG\n"u8.ToArray(), true, deadline, async);

    private protected override ValueTask ControlRenCoreAsync(RemoteLocalMode mode, Deadline deadline, bool async)
    {
        switch (mode)
        {
            case RemoteLocalMode.Assert:
            case RemoteLocalMode.AssertAddress:
                _remote = true;
                _lockout = false;
                break;
            case RemoteLocalMode.AssertLocalLockout:
            case RemoteLocalMode.AssertAddressLocalLockout:
                _remote = true;
                _lockout = true;
                break;
            default:
                _remote = false;
                _lockout = false;
                break;
        }
        // Remote state travels in the header of the next message; send an empty block so it takes effect now.
        return SendBlockAsync(OpData, ReadOnlyMemory<byte>.Empty, deadline, async);
    }

    private protected override void FlushTransportInput() => NetUtil.Drain(Socket);

    private protected override void RegisterAttributes(AttributeTable table)
    {
        base.RegisterAttributes(table);
        table.Add(VisaAttribute.TcpipAddress, () => Address);
        table.Add(VisaAttribute.TcpipHostName, () => HostName);
        table.Add(VisaAttribute.TcpipPort, () => Port);
    }
}

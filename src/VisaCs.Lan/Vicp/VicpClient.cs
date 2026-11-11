using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using VisaCs.Internal;

namespace VisaCs.Lan;

/// <summary>
/// A Teledyne LeCroy VICP connection (TCP port 1861): data blocks with an EOI flag, serial poll, device clear and
/// remote/local control, with service requests arriving in-band.
/// </summary>
/// <remarks>
/// <para>
/// Timeouts are in milliseconds: negative (<see cref="Timeout.Infinite"/>) waits without limit, 0 does not wait
/// (the call times out unless it can complete at once). A timeout throws <see cref="LanTimeoutException"/>, other failures
/// <see cref="LanException"/>, and cancelling the token of an asynchronous call
/// <see cref="OperationCanceledException"/>. Use one caller at a time.
/// </para>
/// <para>
/// After a timeout the late response is still on its way; call <see cref="DeviceClear"/> before the next command.
/// Service requests are only noticed while reading.
/// </para>
/// </remarks>
public sealed class VicpClient : IDisposable
{
    /// <summary>The VICP port (1861).</summary>
    public const int DefaultPort = 1861;

    private const byte OpData = 0x80;
    private const byte OpRemote = 0x40;
    private const byte OpLockout = 0x20;
    private const byte OpClear = 0x10;
    private const byte OpSrq = 0x08;
    private const byte OpSerialPoll = 0x04;
    private const byte OpEoi = 0x01;
    private const byte HeaderVersion = 0x01;
    private const int MaxSrqPayload = 16;

    private readonly string _host;
    private readonly int _connectTimeout;
    private readonly byte[] _header = new byte[8];
    private readonly byte[] _sendHeader = new byte[8];
    private int _headerFilled;
    private long _blockRemaining;
    private bool _blockEoi;
    private Socket _socket = null!;
    private byte _sequence;
    private bool _remote;
    private bool _lockout;

    private VicpClient(string host, int port, int connectTimeout)
    {
        _host = host;
        Port = port;
        _connectTimeout = connectTimeout;
    }

    /// <summary>The device's port.</summary>
    public int Port { get; }

    /// <summary>The device's IP address (IPv4-mapped addresses as IPv4).</summary>
    public IPAddress RemoteAddress { get; private set; } = IPAddress.None;

    /// <summary>Raised (on the reading thread) when the device signals a service request in-band.</summary>
    public event Action? ServiceRequest;

    /// <summary>
    /// Connects to <paramref name="host"/>. <paramref name="timeoutMilliseconds"/> also bounds the reconnect after a
    /// <see cref="DeviceClear"/>.
    /// </summary>
    public static VicpClient Connect(string host, int timeoutMilliseconds, int port = DefaultPort)
    {
        var client = new VicpClient(host, port, timeoutMilliseconds);
        AsyncIO.Complete(client.ConnectCoreAsync(Deadline.FromMilliseconds(timeoutMilliseconds), async: false));
        return client;
    }

    /// <summary>Connects to <paramref name="host"/>; see <see cref="Connect"/>.</summary>
    public static async ValueTask<VicpClient> ConnectAsync(string host, int timeoutMilliseconds, int port = DefaultPort,
        CancellationToken cancellationToken = default)
    {
        var client = new VicpClient(host, port, timeoutMilliseconds);
        await LanCall.RunAsync(client.ConnectCoreAsync(LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken), async: true), cancellationToken)
            .ConfigureAwait(false);
        return client;
    }

    private async ValueTask ConnectCoreAsync(Deadline deadline, bool async)
    {
        ArgumentNullException.ThrowIfNull(_host);
        _socket = await NetUtil.ConnectAsync(_host, Port, deadline, async).ConfigureAwait(false);
        RemoteAddress = NetUtil.Normalize(((IPEndPoint)_socket.RemoteEndPoint!).Address);
        _headerFilled = 0;
        _blockRemaining = 0;
        _blockEoi = false;
    }

    /// <summary>Writes <paramref name="data"/> as one block, with EOI (END) when <paramref name="end"/>.</summary>
    public void Write(ReadOnlyMemory<byte> data, bool end, int timeoutMilliseconds) =>
        AsyncIO.Complete(WriteCoreAsync(data, end, Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>Writes one block; see <see cref="Write"/>.</summary>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, bool end, int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        LanCall.RunAsync(WriteCoreAsync(data, end, LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken), async: true), cancellationToken);

    /// <summary>
    /// Reads response data into <paramref name="buffer"/>: returns as soon as some bytes arrived, at most up to the end
    /// of the current block; <see cref="LanReadResult.End"/> is set at the end of a block with EOI.
    /// </summary>
    public LanReadResult Read(Memory<byte> buffer, int timeoutMilliseconds) =>
        AsyncIO.Complete(ReadCoreAsync(buffer, Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>Reads response data; see <see cref="Read"/>.</summary>
    public ValueTask<LanReadResult> ReadAsync(Memory<byte> buffer, int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        LanCall.RunAsync(ReadCoreAsync(buffer, LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken), async: true), cancellationToken);

    /// <summary>
    /// Serial-polls the device and returns its status byte. The rest of a block a previous read left unfinished is
    /// discarded first.
    /// </summary>
    public byte ReadStatusByte(int timeoutMilliseconds) =>
        AsyncIO.Complete(ReadStatusByteCoreAsync(Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>Serial-polls the device; see <see cref="ReadStatusByte"/>.</summary>
    public ValueTask<byte> ReadStatusByteAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        LanCall.RunAsync(ReadStatusByteCoreAsync(LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken), async: true), cancellationToken);

    /// <summary>
    /// Sends a device clear, then reconnects (VICP requires it) within the connect timeout given to
    /// <see cref="Connect"/>.
    /// </summary>
    public void DeviceClear(int timeoutMilliseconds) =>
        AsyncIO.Complete(DeviceClearCoreAsync(Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>Sends a device clear and reconnects; see <see cref="DeviceClear"/>.</summary>
    public ValueTask DeviceClearAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        LanCall.RunAsync(DeviceClearCoreAsync(LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken), async: true), cancellationToken);

    /// <summary>
    /// Sets the remote and local lockout state. It travels in the header of every block; an empty block is sent so it
    /// takes effect now.
    /// </summary>
    public void SetRemote(bool remote, bool localLockout, int timeoutMilliseconds) =>
        AsyncIO.Complete(SetRemoteCoreAsync(remote, localLockout, Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>Sets the remote and local lockout state; see <see cref="SetRemote"/>.</summary>
    public ValueTask SetRemoteAsync(bool remote, bool localLockout, int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        LanCall.RunAsync(SetRemoteCoreAsync(remote, localLockout, LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken), async: true),
            cancellationToken);

    /// <summary>Discards everything received but not read yet, without waiting.</summary>
    public void DiscardInput() => NetUtil.Drain(_socket);

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
        await NetUtil.SendAllAsync(_socket, _sendHeader, deadline, async).ConfigureAwait(false);
        if (!data.IsEmpty) await NetUtil.SendAllAsync(_socket, data, deadline, async).ConfigureAwait(false);
    }

    private ValueTask WriteCoreAsync(ReadOnlyMemory<byte> data, bool end, Deadline deadline, bool async) =>
        SendBlockAsync((byte)(OpData | (end ? OpEoi : 0)), data, deadline, async);

    private async ValueTask<(byte Operation, long Length)> ReadHeaderAsync(Deadline deadline, bool async)
    {
        while (_headerFilled < 8)
            _headerFilled += await NetUtil.ReceiveSomeAsync(_socket, _header.AsMemory(_headerFilled), deadline, async).ConfigureAwait(false);
        _headerFilled = 0;
        return (_header[0], BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(4)));
    }

    private async ValueTask<LanReadResult> ReadCoreAsync(Memory<byte> target, Deadline deadline, bool async)
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
                    await NetUtil.ReceiveExactAsync(_socket, payload, deadline, async).ConfigureAwait(false);
                    await NetUtil.SkipAsync(_socket, length - payload.Length, deadline, async).ConfigureAwait(false);
                    if (payload.Length > 0 && payload[0] == (byte)'1') ServiceRequest?.Invoke();
                    continue;
                }
                _blockRemaining = length;
                _blockEoi = (op & OpEoi) != 0;
                if (length == 0)
                {
                    if (_blockEoi) return new LanReadResult(0, true);
                    continue;
                }
            }
            int n = await NetUtil.ReceiveSomeAsync(_socket, target[..(int)Math.Min(target.Length, _blockRemaining)], deadline, async).ConfigureAwait(false);
            _blockRemaining -= n;
            return new LanReadResult(n, _blockRemaining == 0 && _blockEoi);
        }
    }

    private async ValueTask<byte> ReadStatusByteCoreAsync(Deadline deadline, bool async)
    {
        // Drop the rest of a block a previous read left unfinished, so the reply is not taken from it.
        if (_blockRemaining > 0)
        {
            await NetUtil.SkipAsync(_socket, _blockRemaining, deadline, async).ConfigureAwait(false);
            _blockRemaining = 0;
        }
        await SendBlockAsync(OpData | OpSerialPoll, ReadOnlyMemory<byte>.Empty, deadline, async).ConfigureAwait(false);
        var buffer = new byte[16];
        int total = 0;
        while (true)
        {
            var (n, end) = await ReadCoreAsync(buffer.AsMemory(total), deadline, async).ConfigureAwait(false);
            total += n;
            if (end || total == buffer.Length) break;
        }
        if (total == 0) throw LanErrors.Protocol("Empty VICP serial poll response.");
        if (total > 1 && byte.TryParse(System.Text.Encoding.ASCII.GetString(buffer, 0, total).Trim(), out byte parsed)) return parsed;
        return buffer[0];
    }

    private async ValueTask DeviceClearCoreAsync(Deadline deadline, bool async)
    {
        await SendBlockAsync(OpData | OpClear, ReadOnlyMemory<byte>.Empty, deadline, async).ConfigureAwait(false);
        if (async) await Task.Delay(100, deadline.CancellationToken).ConfigureAwait(false);
        else Thread.Sleep(100);
        // The VICP 1a protocol requires reconnecting after a device clear.
        NetUtil.SafeClose(_socket);
        await ConnectCoreAsync(Deadline.FromMilliseconds(_connectTimeout).WithCancellation(deadline.CancellationToken), async).ConfigureAwait(false);
    }

    private ValueTask SetRemoteCoreAsync(bool remote, bool localLockout, Deadline deadline, bool async)
    {
        _remote = remote;
        _lockout = remote && localLockout;
        return SendBlockAsync(OpData, ReadOnlyMemory<byte>.Empty, deadline, async);
    }

    /// <summary>Closes the connection.</summary>
    public void Dispose() => NetUtil.SafeClose(_socket);
}

using System.Net.Sockets;
using VisaCs.Internal;
using VisaCs.Lan;
using VisaCs.Lan.Rpc;

namespace VisaCs;

/// <summary>VXI-11 instrument session (<c>TCPIP::host[::inst0]::INSTR</c>, also VXI-11 GPIB gateways like <c>gpib0,5</c>).</summary>
/// <remarks>
/// <para>
/// Each call passes the remaining session timeout to the instrument as <c>io_timeout</c>, so the instrument ends the
/// call itself; the client waits 2 s longer for the reply. I/O does not wait for a lock held by another link: it fails
/// at once with VI_ERROR_RSRC_LOCKED.
/// </para>
/// <para>
/// Cancelling an asynchronous operation also sends <c>device_abort</c> on the abort channel, so the instrument ends
/// the pending call; the session stays usable and the aborted call's late reply is discarded. A write cut off after
/// part of a request was sent closes the connection (later calls fail with VI_ERROR_CONN_LOST), because the
/// instrument would read the next request as the rest of that one.
/// </para>
/// <para>
/// Only exclusive locks take the instrument's lock (<c>device_lock</c>); VXI-11 has no shared locks, so shared locks
/// only exclude other sessions in this process.
/// </para>
/// <para>The protocol itself is <see cref="Vxi11Client"/> from <c>VisaCs.Lan</c>.</para>
/// </remarks>
public sealed class TcpipVxi11Session : TcpipInstrumentSession
{
    private static int _clientIdSeed = Environment.ProcessId & 0xFFFF;
    private Vxi11Client? _client;
    private RpcTcpServer? _interruptServer;
    private readonly byte[] _srqHandle;

    internal TcpipVxi11Session(ResourceManager manager, TcpipInstrumentResourceName name)
        : base(manager, name, inputBufferSize: 256 * 1024)
    {
        _srqHandle = BitConverter.GetBytes(Interlocked.Increment(ref _clientIdSeed) ^ Environment.TickCount);
    }

    /// <inheritdoc/>
    public override bool IsHislip => false;

    /// <summary>VXI-11 link identifier assigned by the instrument (<c>create_link</c>).</summary>
    public int LinkId => Client.LinkId;

    /// <summary>
    /// Largest write the instrument accepts in one <c>device_write</c> call (<c>maxRecvSize</c>). Writes are split into
    /// calls of at most this size, and at most 4 MiB.
    /// </summary>
    public long MaxReceiveSize => Client.MaxReceiveSize;

    /// <inheritdoc/>
    public override bool KeepAlive
    {
        get => Client.KeepAlive;
        set => Client.KeepAlive = value;
    }

    private Vxi11Client Client => _client ?? throw new VisaException(VisaStatus.ErrorInvalidObject, "The session is not open.");

    /// <summary>
    /// True while the session is being closed and a core-channel call is still in progress (on another thread). Closing
    /// the connection then ends that call, and the instrument destroys the link and its locks itself.
    /// </summary>
    private bool ClosingWhileBusy => IsDisposed && _client is { IsBusy: true };

    private protected override async ValueTask OpenCoreAsync(int connectTimeoutMs, CancellationToken cancellationToken, bool async)
    {
        int clientId = Interlocked.Increment(ref _clientIdSeed);
        int port = ResourceManager.Options.Vxi11PortmapperPort;
        _client = async
            ? await Vxi11Client.ConnectAsync(Name.Host, Name.DeviceName, connectTimeoutMs, port, clientId, cancellationToken).ConfigureAwait(false)
            : Vxi11Client.Connect(Name.Host, Name.DeviceName, connectTimeoutMs, port, clientId);
        Address = NetUtil.Normalize(_client.RemoteEndPoint.Address).ToString();
    }

    private protected override async ValueTask CloseCoreAsync(bool async)
    {
        if (_client is null) return;
        if (ClosingWhileBusy) _client.Dispose();
        await StopInterruptChannelAsync(async).ConfigureAwait(false);
        if (async) await _client.DestroyLinkAsync(1000).ConfigureAwait(false);
        else _client.DestroyLink(1000);
        _client.Dispose();
    }

    private int MaxChunk
    {
        get
        {
            long max = Client.MaxReceiveSize;
            if (max <= 0) max = 1024;
            return (int)Math.Min(max, 4 * 1024 * 1024);
        }
    }

    private async ValueTask<Vxi11WriteResult> DeviceWriteAsync(ReadOnlyMemory<byte> data, bool end, Deadline deadline, bool async) =>
        async
            ? await Client.WriteAsync(data, end, deadline.RemainingMilliseconds, deadline.CancellationToken).ConfigureAwait(false)
            : Client.Write(data, end, deadline.RemainingMilliseconds);

    private protected override async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> data, bool end, Deadline deadline, bool async)
    {
        if (data.IsEmpty)
        {
            if (!end) return;
            var (error, _) = await DeviceWriteAsync(ReadOnlyMemory<byte>.Empty, true, deadline, async).ConfigureAwait(false);
            if (error != Vxi11Error.None) throw Vxi11Errors.ToException(error, "device_write");
            return;
        }

        int chunk = MaxChunk;
        int offset = 0;
        while (offset < data.Length)
        {
            int n = Math.Min(chunk, data.Length - offset);
            bool last = offset + n >= data.Length;
            var (error, size) = await DeviceWriteAsync(data.Slice(offset, n), last && end, deadline, async).ConfigureAwait(false);
            if (error != Vxi11Error.None) throw Vxi11Errors.ToException(error, "device_write");
            if (size == 0) deadline.ThrowIfExpired("device_write");
            offset += Math.Min(size, n);
        }
    }

    private protected override async ValueTask<FillResult> FillCoreAsync(Memory<byte> target, Deadline deadline, bool async)
    {
        while (true)
        {
            int term = EffectiveTermChar;
            byte? termChar = term >= 0 ? (byte)term : null;
            var (error, reason, data) = async
                ? await Client.ReadAsync(target.Length, termChar, deadline.RemainingMilliseconds, deadline.CancellationToken).ConfigureAwait(false)
                : Client.Read(target.Length, termChar, deadline.RemainingMilliseconds);
            if (error != Vxi11Error.None) throw Vxi11Errors.ToException(error, "device_read");
            int n = Math.Min(data.Length, target.Length);
            data.Span[..n].CopyTo(target.Span);
            bool end = (reason & Vxi11ReadReasons.End) != 0;
            if (n > 0 || end) return new FillResult(n, end);
            deadline.ThrowIfExpired("device_read");
        }
    }

    private protected override async ValueTask<byte> ReadStatusByteCoreAsync(Deadline deadline, bool async)
    {
        var (error, stb) = async
            ? await Client.ReadStatusByteAsync(deadline.RemainingMilliseconds, deadline.CancellationToken).ConfigureAwait(false)
            : Client.ReadStatusByte(deadline.RemainingMilliseconds);
        if (error != Vxi11Error.None) throw Vxi11Errors.ToException(error, "device_readstb");
        return stb;
    }

    private protected override async ValueTask ClearCoreAsync(Deadline deadline, bool async)
    {
        var error = async
            ? await Client.ClearAsync(deadline.RemainingMilliseconds, deadline.CancellationToken).ConfigureAwait(false)
            : Client.Clear(deadline.RemainingMilliseconds);
        if (error != Vxi11Error.None) throw Vxi11Errors.ToException(error, "device_clear");
    }

    private protected override async ValueTask TriggerCoreAsync(Deadline deadline, bool async)
    {
        var error = async
            ? await Client.TriggerAsync(deadline.RemainingMilliseconds, deadline.CancellationToken).ConfigureAwait(false)
            : Client.Trigger(deadline.RemainingMilliseconds);
        if (error != Vxi11Error.None) throw Vxi11Errors.ToException(error, "device_trigger");
    }

    private protected override async ValueTask ControlRenCoreAsync(RemoteLocalMode mode, Deadline deadline, bool async)
    {
        bool remote = mode is RemoteLocalMode.Assert or RemoteLocalMode.AssertAddress or RemoteLocalMode.AssertLocalLockout or
            RemoteLocalMode.AssertAddressLocalLockout;
        int timeout = deadline.RemainingMilliseconds;
        var token = deadline.CancellationToken;
        var error = (remote, async) switch
        {
            (true, true) => await Client.RemoteAsync(timeout, token).ConfigureAwait(false),
            (true, false) => Client.Remote(timeout),
            (false, true) => await Client.LocalAsync(timeout, token).ConfigureAwait(false),
            (false, false) => Client.Local(timeout),
        };
        if (error != Vxi11Error.None) throw Vxi11Errors.ToException(error, "device_remote/local");
    }

    /// <summary>
    /// Issues a VXI-11 <c>device_docmd</c>, used by GPIB gateways (VXI-11.2), e.g. command 0x020000 sends GPIB command
    /// bytes, 0x020001 reads bus status, 0x020002 controls ATN, 0x020003 REN, 0x020004 passes control, 0x02000A sets the
    /// bus address and 0x020010 pulses IFC. The session timeout is passed as <c>io_timeout</c>.
    /// </summary>
    /// <param name="command">The <c>cmd</c> code; gateways may define further, vendor-specific codes.</param>
    /// <param name="data">The <c>data_in</c> bytes.</param>
    /// <param name="dataSize">Size in bytes of one element of <paramref name="data"/> (1, 2, 4 or 8), which the gateway
    /// uses to convert byte order.</param>
    /// <param name="networkOrder">True when multi-byte elements are in network (big-endian) byte order.</param>
    /// <returns>The <c>data_out</c> bytes.</returns>
    public byte[] DoCommand(int command, ReadOnlySpan<byte> data, int dataSize = 1, bool networkOrder = true) =>
        AsyncIO.Complete(DoCommandAsync(command, data.ToArray(), dataSize, networkOrder, CancellationToken.None, async: false));

    /// <summary>Issues a VXI-11 <c>device_docmd</c>; see <see cref="DoCommand"/>.</summary>
    public Task<byte[]> DoCommandAsync(int command, ReadOnlyMemory<byte> data, int dataSize = 1, bool networkOrder = true,
        CancellationToken cancellationToken = default) =>
        DoCommandAsync(command, data, dataSize, networkOrder, cancellationToken, async: true).AsTask();

    private ValueTask<byte[]> DoCommandAsync(int command, ReadOnlyMemory<byte> data, int dataSize, bool networkOrder,
        CancellationToken cancellationToken, bool async) =>
        ExecuteAsync((command, data, dataSize, networkOrder), static async (s, a, ct, async) =>
        {
            var session = (TcpipVxi11Session)s;
            int timeout = session.TimeoutMilliseconds;
            var (error, output) = async
                ? await session.Client.DoCommandAsync(a.command, a.data, a.dataSize, a.networkOrder, timeout, ct).ConfigureAwait(false)
                : session.Client.DoCommand(a.command, a.data, a.dataSize, a.networkOrder, timeout);
            if (error != Vxi11Error.None) throw Vxi11Errors.ToException(error, "device_docmd");
            return output;
        }, cancellationToken, async);

    private protected override async ValueTask AcquireDeviceLockAsync(LockType type, string? key, int timeoutMilliseconds, Deadline deadline,
        bool async)
    {
        // VXI-11 only has exclusive device locks; shared locks are enforced within this process.
        if (type != LockType.Exclusive) return;
        // The instrument waits for the time this request has left: the caller may have waited for sessions in this
        // process already. A grant that arrives after the caller gave up is released by the client.
        int lockTimeout = timeoutMilliseconds < 0 ? -1 : deadline.RemainingMilliseconds;
        var error = async
            ? await Client.LockAsync(lockTimeout, deadline.CancellationToken).ConfigureAwait(false)
            : Client.Lock(lockTimeout);
        if (error == Vxi11Error.DeviceLocked)
            throw new VisaTimeoutException("The device is locked by another VXI-11 link.");
        if (error != Vxi11Error.None) throw Vxi11Errors.ToException(error, "device_lock");
    }

    private protected override async ValueTask ReleaseDeviceLockAsync(LockType type, CancellationToken cancellationToken, bool async)
    {
        // While closing, destroy_link (or the connection closing) releases the lock.
        if (type != LockType.Exclusive || _client is null || IsDisposed) return;
        var error = async
            ? await _client.UnlockAsync(5000, cancellationToken).ConfigureAwait(false)
            : _client.Unlock(5000);
        if (error != Vxi11Error.None && error != Vxi11Error.NoLockHeld) throw Vxi11Errors.ToException(error, "device_unlock");
    }

    // VXI-11 service requests arrive as RPC calls from the instrument to an RPC server run by the client.
    private protected override async ValueTask OnEventEnabledAsync(EventType eventType, CancellationToken cancellationToken, bool async)
    {
        if (eventType != EventType.ServiceRequest) return;
        var local = NetUtil.Normalize(Client.LocalEndPoint.Address);
        if (local.AddressFamily != AddressFamily.InterNetwork)
            throw new VisaException(VisaStatus.ErrorOperationNotSupported, "VXI-11 service requests require an IPv4 connection.");

        var server = new RpcTcpServer(local, 0, Vxi11Protocol.InterruptProgram, Vxi11Protocol.Version, HandleInterrupt);
        server.Start();
        try
        {
            var deadline = Deadline.FromMilliseconds(Math.Max(TimeoutMilliseconds, 5000)).WithCancellation(cancellationToken);
            var client = Client;
            var error = async
                ? await client.CreateInterruptChannelAsync(local, server.Port, deadline.RemainingMilliseconds, cancellationToken).ConfigureAwait(false)
                : client.CreateInterruptChannel(local, server.Port, deadline.RemainingMilliseconds);
            if (error == Vxi11Error.ChannelAlreadyEstablished)
            {
                if (async) await client.DestroyInterruptChannelAsync(deadline.RemainingMilliseconds, cancellationToken).ConfigureAwait(false);
                else client.DestroyInterruptChannel(deadline.RemainingMilliseconds);
                error = async
                    ? await client.CreateInterruptChannelAsync(local, server.Port, deadline.RemainingMilliseconds, cancellationToken).ConfigureAwait(false)
                    : client.CreateInterruptChannel(local, server.Port, deadline.RemainingMilliseconds);
            }
            if (error != Vxi11Error.None) throw Vxi11Errors.ToException(error, "create_intr_chan");
            error = async
                ? await client.EnableServiceRequestsAsync(true, _srqHandle, deadline.RemainingMilliseconds, cancellationToken).ConfigureAwait(false)
                : client.EnableServiceRequests(true, _srqHandle, deadline.RemainingMilliseconds);
            if (error != Vxi11Error.None) throw Vxi11Errors.ToException(error, "device_enable_srq");
            _interruptServer = server;
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    private RpcAcceptStatus HandleInterrupt(RpcCallContext context, XdrReader args, XdrWriter results)
    {
        if (context.Procedure != Vxi11Protocol.ProcDeviceIntrSrq) return RpcAcceptStatus.ProcedureUnavailable;
        var handle = args.ReadOpaqueSpan();
        if (handle.SequenceEqual(_srqHandle) || handle.IsEmpty)
            RaiseEvent(new ServiceRequestEventArgs());
        return RpcAcceptStatus.Success;
    }

    private protected override ValueTask OnEventDisabledAsync(EventType eventType, bool async) =>
        eventType == EventType.ServiceRequest ? StopInterruptChannelAsync(async) : default;

    private async ValueTask StopInterruptChannelAsync(bool async)
    {
        var server = Interlocked.Exchange(ref _interruptServer, null);
        if (server is null || _client is null) return;
        try
        {
            if (ClosingWhileBusy) _client.Dispose();
            var deadline = Deadline.FromMilliseconds(2000);
            if (async)
            {
                await _client.EnableServiceRequestsAsync(false, _srqHandle, deadline.RemainingMilliseconds).ConfigureAwait(false);
                await _client.DestroyInterruptChannelAsync(deadline.RemainingMilliseconds).ConfigureAwait(false);
            }
            else
            {
                _client.EnableServiceRequests(false, _srqHandle, deadline.RemainingMilliseconds);
                _client.DestroyInterruptChannel(deadline.RemainingMilliseconds);
            }
        }
        catch
        {
            // Best effort.
        }
        finally
        {
            server.Dispose();
        }
    }

    private protected override void RegisterAttributes(AttributeTable table)
    {
        base.RegisterAttributes(table);
        table.Add(VisaAttribute.TcpipPort, () => Client.RemoteEndPoint.Port);
    }
}

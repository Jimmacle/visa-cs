using System.Net;
using VisaCs.Internal;
using VisaCs.Lan.Rpc;

namespace VisaCs.Lan;

/// <summary>
/// A VXI-11 device link: the core channel for I/O, status, locks and service-request setup, and the abort channel.
/// </summary>
/// <remarks>
/// <para>
/// Timeouts are in milliseconds: negative (<see cref="Timeout.Infinite"/>) waits without limit, 0 does not wait. Calls
/// that act on the device pass the timeout to it (<c>io_timeout</c>, so 0 lets the device complete only what it can
/// do at once), and the device ends the call itself; the client waits 2 s longer for the reply. The device's own
/// errors are returned as <see cref="Vxi11Error"/> codes (<see cref="Vxi11Errors.ToException"/> turns one into an
/// exception). Transport and RPC failures throw <see cref="LanException"/> (<see cref="Rpc.RpcException"/> when the
/// server does not accept a call), a reply that does not arrive in time <see cref="LanTimeoutException"/>, and
/// cancelling an asynchronous call <see cref="OperationCanceledException"/>.
/// </para>
/// <para>
/// Core-channel calls run one at a time. Cancelling an asynchronous call also sends <c>device_abort</c> on the abort
/// channel, so the device ends the pending call; the link stays usable and the late reply is discarded. A
/// device_abort is completed before the next core-channel call is sent, so it cannot end that call. A request cut off
/// after part of it was sent closes the connection (later calls throw <see cref="LanException"/> with
/// <see cref="LanError.ConnectionLost"/>). All channels go to the
/// address the portmapper answered on.
/// </para>
/// </remarks>
public sealed class Vxi11Client : IDisposable
{
    /// <summary>Additional time allowed for the RPC reply beyond the device-side I/O or lock timeout.</summary>
    private const int ReplySlackMs = 2000;

    private const int AbortTimeoutMs = 2000;

    private readonly RpcTcpClient _core;
    private readonly IPAddress _address;
    private RpcTcpClient? _abort;
    private readonly SemaphoreSlim _abortGate = new(1, 1);
    private Task _pendingAbort = Task.CompletedTask;
    private volatile bool _disposed;

    private Vxi11Client(RpcTcpClient core, IPAddress address)
    {
        _core = core;
        _address = address;
    }

    /// <summary>The link identifier the device assigned (create_link).</summary>
    public int LinkId { get; private set; }

    /// <summary>The TCP port of the abort channel; 0 when the device has none.</summary>
    public int AbortPort { get; private set; }

    /// <summary>The largest write the device accepts in one <c>device_write</c> call (<c>maxRecvSize</c>).</summary>
    public uint MaxReceiveSize { get; private set; }

    /// <summary>The local end of the core channel (the address an interrupt channel listens on).</summary>
    public IPEndPoint LocalEndPoint => _core.LocalEndPoint;

    /// <summary>The device's end of the core channel.</summary>
    public IPEndPoint RemoteEndPoint => _core.RemoteEndPoint;

    /// <summary>TCP keep-alive on the core channel. Default false.</summary>
    public bool KeepAlive
    {
        get => NetUtil.GetKeepAlive(_core.Socket);
        set => NetUtil.SetKeepAlive(_core.Socket, value);
    }

    /// <summary>True while a core-channel call is in progress.</summary>
    public bool IsBusy => _core.IsBusy;

    /// <summary>
    /// Asks the portmapper of <paramref name="host"/> for the core channel, connects to it, creates a link to
    /// <paramref name="deviceName"/> (e.g. <c>inst0</c>, or <c>gpib0,5</c> on a GPIB gateway) and connects the abort
    /// channel. The timeout covers all of it. A create_link error throws (<see cref="Vxi11Errors.ToException"/>).
    /// </summary>
    /// <param name="host">Host name or IP address.</param>
    /// <param name="deviceName">The device on the host.</param>
    /// <param name="timeoutMilliseconds">Time for the whole connection setup (-1: no limit).</param>
    /// <param name="portmapperPort">The portmapper's port.</param>
    /// <param name="clientId">The <c>clientId</c> sent with create_link.</param>
    public static Vxi11Client Connect(string host, string deviceName, int timeoutMilliseconds, int portmapperPort = Portmapper.DefaultPort,
        int clientId = 0) =>
        AsyncIO.Complete(ConnectAsync(host, deviceName, portmapperPort, clientId, Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>Connects and creates a link; see <see cref="Connect"/>.</summary>
    public static ValueTask<Vxi11Client> ConnectAsync(string host, string deviceName, int timeoutMilliseconds,
        int portmapperPort = Portmapper.DefaultPort, int clientId = 0, CancellationToken cancellationToken = default) =>
        LanCall.RunAsync(ConnectAsync(host, deviceName, portmapperPort, clientId, LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken),
            async: true), cancellationToken);

    private static async ValueTask<Vxi11Client> ConnectAsync(string host, string deviceName, int portmapperPort, int clientId,
        Deadline deadline, bool async)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(deviceName);
        IPAddress address;
        int corePort;
        using (var portmapper = await Portmapper.ConnectAsync(host, portmapperPort, deadline, async).ConfigureAwait(false))
        {
            address = portmapper.RemoteEndPoint.Address;
            corePort = await Portmapper.GetPortAsync(portmapper, host, Vxi11Protocol.CoreProgram, Vxi11Protocol.Version, deadline, async)
                .ConfigureAwait(false);
        }
        var core = await RpcTcpClient.ConnectAsync(new IPEndPoint(address, corePort), Vxi11Protocol.CoreProgram, Vxi11Protocol.Version,
            deadline, async).ConfigureAwait(false);
        var client = new Vxi11Client(core, address);
        try
        {
            var r = await core.CallAsync(Vxi11Protocol.ProcCreateLink, w =>
            {
                w.WriteInt32(clientId);
                w.WriteBool(false); // lockDevice: locks are taken with device_lock
                w.WriteUInt32(0);   // lock_timeout
                w.WriteString(deviceName);
            }, deadline, async).ConfigureAwait(false);
            var error = (Vxi11Error)r.ReadInt32();
            int linkId = r.ReadInt32();
            uint abortPort = r.ReadUInt32();
            uint maxRecv = r.ReadUInt32();
            if (error != Vxi11Error.None) throw Vxi11Errors.ToException(error, "create_link");
            client.LinkId = linkId;
            client.AbortPort = (int)(abortPort & 0xFFFF);
            client.MaxReceiveSize = maxRecv;
            await client.ConnectAbortChannelAsync(deadline, async).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Connects the abort channel now, so that a later device_abort does not wait for a connect.</summary>
    private async ValueTask ConnectAbortChannelAsync(Deadline deadline, bool async)
    {
        if (AbortPort == 0) return;
        try
        {
            _abort = await RpcTcpClient.ConnectAsync(new IPEndPoint(_address, AbortPort), Vxi11Protocol.AbortProgram, Vxi11Protocol.Version,
                deadline, async).ConfigureAwait(false);
        }
        catch (Exception ex) when (LanErrors.IsLanError(ex) && !deadline.IsCanceled)
        {
            // Not fatal: AbortAsync connects when it is needed.
        }
    }

    // ------------------------------------------------------------------ public call forms

    /// <summary>
    /// Runs an asynchronous public call. A token cancelled while it runs starts a device_abort (unless
    /// <paramref name="abortOnCancel"/> is false) and ends the call with <see cref="OperationCanceledException"/>.
    /// </summary>
    private async ValueTask<T> RunAsync<T>(Func<Vxi11Client, Deadline, ValueTask<T>> call, int timeoutMilliseconds,
        CancellationToken cancellationToken, bool abortOnCancel = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var registration = abortOnCancel && cancellationToken.CanBeCanceled
            ? cancellationToken.UnsafeRegister(static s => ((Vxi11Client)s!).BeginAbort(), this)
            : default;
        return await LanCall.RunAsync(call(this, LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken)), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// device_write: sends <paramref name="data"/> (at most <see cref="MaxReceiveSize"/> bytes), with END when
    /// <paramref name="end"/>. The device may accept fewer bytes than sent (see <see cref="Vxi11WriteResult.Size"/>).
    /// </summary>
    public Vxi11WriteResult Write(ReadOnlyMemory<byte> data, bool end, int timeoutMilliseconds) =>
        AsyncIO.Complete(WriteCoreAsync(data, end, Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>device_write; see <see cref="Write"/>.</summary>
    public ValueTask<Vxi11WriteResult> WriteAsync(ReadOnlyMemory<byte> data, bool end, int timeoutMilliseconds,
        CancellationToken cancellationToken = default) =>
        RunAsync((c, d) => c.WriteCoreAsync(data, end, d, async: true), timeoutMilliseconds, cancellationToken);

    /// <summary>
    /// device_read: reads up to <paramref name="requestSize"/> bytes, ending early at END or, when
    /// <paramref name="terminationCharacter"/> is set, after that character.
    /// </summary>
    public Vxi11ReadResult Read(int requestSize, byte? terminationCharacter, int timeoutMilliseconds) =>
        AsyncIO.Complete(ReadCoreAsync(requestSize, terminationCharacter, Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>device_read; see <see cref="Read"/>.</summary>
    public ValueTask<Vxi11ReadResult> ReadAsync(int requestSize, byte? terminationCharacter, int timeoutMilliseconds,
        CancellationToken cancellationToken = default) =>
        RunAsync((c, d) => c.ReadCoreAsync(requestSize, terminationCharacter, d, async: true), timeoutMilliseconds, cancellationToken);

    /// <summary>device_readstb: reads the status byte.</summary>
    public Vxi11StatusByteResult ReadStatusByte(int timeoutMilliseconds) =>
        AsyncIO.Complete(ReadStatusByteCoreAsync(Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>device_readstb; see <see cref="ReadStatusByte"/>.</summary>
    public ValueTask<Vxi11StatusByteResult> ReadStatusByteAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        RunAsync((c, d) => c.ReadStatusByteCoreAsync(d, async: true), timeoutMilliseconds, cancellationToken);

    /// <summary>device_trigger.</summary>
    public Vxi11Error Trigger(int timeoutMilliseconds) => Generic(Vxi11Protocol.ProcDeviceTrigger, timeoutMilliseconds);

    /// <summary>device_trigger.</summary>
    public ValueTask<Vxi11Error> TriggerAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        GenericAsync(Vxi11Protocol.ProcDeviceTrigger, timeoutMilliseconds, cancellationToken);

    /// <summary>device_clear.</summary>
    public Vxi11Error Clear(int timeoutMilliseconds) => Generic(Vxi11Protocol.ProcDeviceClear, timeoutMilliseconds);

    /// <summary>device_clear.</summary>
    public ValueTask<Vxi11Error> ClearAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        GenericAsync(Vxi11Protocol.ProcDeviceClear, timeoutMilliseconds, cancellationToken);

    /// <summary>device_remote: puts the device in remote state.</summary>
    public Vxi11Error Remote(int timeoutMilliseconds) => Generic(Vxi11Protocol.ProcDeviceRemote, timeoutMilliseconds);

    /// <summary>device_remote.</summary>
    public ValueTask<Vxi11Error> RemoteAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        GenericAsync(Vxi11Protocol.ProcDeviceRemote, timeoutMilliseconds, cancellationToken);

    /// <summary>device_local: returns the device to local state.</summary>
    public Vxi11Error Local(int timeoutMilliseconds) => Generic(Vxi11Protocol.ProcDeviceLocal, timeoutMilliseconds);

    /// <summary>device_local.</summary>
    public ValueTask<Vxi11Error> LocalAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        GenericAsync(Vxi11Protocol.ProcDeviceLocal, timeoutMilliseconds, cancellationToken);

    private Vxi11Error Generic(uint procedure, int timeoutMilliseconds) =>
        AsyncIO.Complete(GenericErrorAsync(procedure, Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    private ValueTask<Vxi11Error> GenericAsync(uint procedure, int timeoutMilliseconds, CancellationToken cancellationToken) =>
        RunAsync((c, d) => c.GenericErrorAsync(procedure, d, async: true), timeoutMilliseconds, cancellationToken);

    /// <summary>
    /// device_lock: takes the device's exclusive lock, letting the device wait up to <paramref name="timeoutMilliseconds"/>
    /// for another link to release it (<see cref="Vxi11Error.DeviceLocked"/> when it does not). If the reply does not
    /// arrive in time (or the call is cancelled), the device may still grant the lock later: the wait is aborted and
    /// such a late lock released in the background.
    /// </summary>
    public Vxi11Error Lock(int timeoutMilliseconds) =>
        AsyncIO.Complete(LockCoreAsync(timeoutMilliseconds, Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>device_lock; see <see cref="Lock"/>.</summary>
    public ValueTask<Vxi11Error> LockAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        RunAsync((c, d) => c.LockCoreAsync(timeoutMilliseconds, d, async: true), timeoutMilliseconds, cancellationToken, abortOnCancel: false);

    /// <summary>device_unlock: releases the lock (<see cref="Vxi11Error.NoLockHeld"/> when this link holds none).</summary>
    public Vxi11Error Unlock(int timeoutMilliseconds) =>
        AsyncIO.Complete(UnlockCoreAsync(Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>device_unlock; see <see cref="Unlock"/>.</summary>
    public ValueTask<Vxi11Error> UnlockAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        RunAsync((c, d) => c.UnlockCoreAsync(d, async: true), timeoutMilliseconds, cancellationToken);

    /// <summary>
    /// device_enable_srq: enables or disables service requests on the interrupt channel. The device sends
    /// <paramref name="handle"/> back with each device_intr_srq call.
    /// </summary>
    public Vxi11Error EnableServiceRequests(bool enable, ReadOnlyMemory<byte> handle, int timeoutMilliseconds) =>
        AsyncIO.Complete(EnableSrqCoreAsync(enable, handle, Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>device_enable_srq; see <see cref="EnableServiceRequests"/>.</summary>
    public ValueTask<Vxi11Error> EnableServiceRequestsAsync(bool enable, ReadOnlyMemory<byte> handle, int timeoutMilliseconds,
        CancellationToken cancellationToken = default) =>
        RunAsync((c, d) => c.EnableSrqCoreAsync(enable, handle, d, async: true), timeoutMilliseconds, cancellationToken);

    /// <summary>
    /// device_docmd, used by GPIB gateways (VXI-11.2): <paramref name="command"/> with <paramref name="data"/>, whose
    /// elements are <paramref name="dataSize"/> bytes long, in network byte order when <paramref name="networkOrder"/>.
    /// </summary>
    public Vxi11DocmdResult DoCommand(int command, ReadOnlyMemory<byte> data, int dataSize, bool networkOrder, int timeoutMilliseconds) =>
        AsyncIO.Complete(DocmdCoreAsync(command, networkOrder, dataSize, data, Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>device_docmd; see <see cref="DoCommand"/>.</summary>
    public ValueTask<Vxi11DocmdResult> DoCommandAsync(int command, ReadOnlyMemory<byte> data, int dataSize, bool networkOrder,
        int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        RunAsync((c, d) => c.DocmdCoreAsync(command, networkOrder, dataSize, data, d, async: true), timeoutMilliseconds, cancellationToken);

    /// <summary>
    /// create_intr_chan: asks the device to call device_intr_srq on the RPC server at
    /// <paramref name="hostAddress"/>:<paramref name="hostPort"/> (an <see cref="RpcTcpServer"/> for
    /// <see cref="Vxi11Protocol.InterruptProgram"/>). Requires IPv4.
    /// </summary>
    public Vxi11Error CreateInterruptChannel(IPAddress hostAddress, int hostPort, int timeoutMilliseconds) =>
        AsyncIO.Complete(CreateIntrChanCoreAsync(hostAddress, hostPort, Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>create_intr_chan; see <see cref="CreateInterruptChannel"/>.</summary>
    public ValueTask<Vxi11Error> CreateInterruptChannelAsync(IPAddress hostAddress, int hostPort, int timeoutMilliseconds,
        CancellationToken cancellationToken = default) =>
        RunAsync((c, d) => c.CreateIntrChanCoreAsync(hostAddress, hostPort, d, async: true), timeoutMilliseconds, cancellationToken);

    /// <summary>destroy_intr_chan.</summary>
    public Vxi11Error DestroyInterruptChannel(int timeoutMilliseconds) =>
        AsyncIO.Complete(DestroyIntrChanCoreAsync(Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>destroy_intr_chan.</summary>
    public ValueTask<Vxi11Error> DestroyInterruptChannelAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        RunAsync((c, d) => c.DestroyIntrChanCoreAsync(d, async: true), timeoutMilliseconds, cancellationToken);

    /// <summary>
    /// device_abort on the abort channel: ends the core-channel call in progress. Waits up to 2 s. Throws
    /// <see cref="LanException"/> with <see cref="LanError.NotSupported"/> when the device has no abort channel.
    /// </summary>
    public Vxi11Error Abort() => AsyncIO.Complete(AbortCoreAsync(async: false));

    /// <summary>device_abort; see <see cref="Abort"/>.</summary>
    public ValueTask<Vxi11Error> AbortAsync() => AbortCoreAsync(async: true);

    /// <summary>destroy_link, best effort (errors are ignored). Dispose the client afterwards.</summary>
    public void DestroyLink(int timeoutMilliseconds) => AsyncIO.Complete(DestroyLinkCoreAsync(timeoutMilliseconds, async: false));

    /// <summary>destroy_link, best effort; see <see cref="DestroyLink"/>.</summary>
    public ValueTask DestroyLinkAsync(int timeoutMilliseconds) => DestroyLinkCoreAsync(timeoutMilliseconds, async: true);

    // ------------------------------------------------------------------ implementation

    /// <summary>Converts a deadline to a VXI-11 millisecond timeout and the RPC reply deadline.</summary>
    private static (uint DeviceTimeout, Deadline ReplyDeadline) Timeouts(Deadline deadline)
    {
        if (deadline.IsInfinite) return (int.MaxValue, deadline);
        int remaining = deadline.RemainingMilliseconds;
        return ((uint)remaining, deadline.Extend(ReplySlackMs));
    }

    private async ValueTask<XdrReader> CoreCallAsync(uint procedure, Action<XdrWriter>? writeArgs, Deadline deadline, bool async)
    {
        // A device_abort for a cancelled call must reach the device before the next call does, or it could end that one.
        // The abort is bounded by its own timeout.
        var abort = Volatile.Read(ref _pendingAbort);
        if (!abort.IsCompleted)
        {
            if (async) await abort.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            else
            {
                try { abort.Wait(); } catch (AggregateException) { /* the abort's own failure does not matter here */ }
            }
        }
        return await _core.CallAsync(procedure, writeArgs, deadline, async).ConfigureAwait(false);
    }

    private async ValueTask<Vxi11WriteResult> WriteCoreAsync(ReadOnlyMemory<byte> data, bool end, Deadline deadline, bool async)
    {
        var (io, reply) = Timeouts(deadline);
        var r = await CoreCallAsync(Vxi11Protocol.ProcDeviceWrite, w =>
        {
            w.WriteInt32(LinkId);
            w.WriteUInt32(io);
            w.WriteUInt32(0); // lock_timeout: fail at once when another link holds the lock
            w.WriteInt32(end ? Vxi11Protocol.FlagEnd : 0);
            w.WriteOpaque(data.Span);
        }, reply, async).ConfigureAwait(false);
        var error = (Vxi11Error)r.ReadInt32();
        return new Vxi11WriteResult(error, (int)Math.Min(r.ReadUInt32(), int.MaxValue));
    }

    private async ValueTask<Vxi11ReadResult> ReadCoreAsync(int requestSize, byte? termChar, Deadline deadline, bool async)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requestSize);
        var (io, reply) = Timeouts(deadline);
        var r = await CoreCallAsync(Vxi11Protocol.ProcDeviceRead, w =>
        {
            w.WriteInt32(LinkId);
            w.WriteUInt32((uint)requestSize);
            w.WriteUInt32(io);
            w.WriteUInt32(0); // lock_timeout
            w.WriteInt32(termChar is null ? 0 : Vxi11Protocol.FlagTermCharSet);
            w.WriteInt32(termChar ?? 0);
        }, reply, async).ConfigureAwait(false);
        var error = (Vxi11Error)r.ReadInt32();
        var reason = (Vxi11ReadReasons)r.ReadInt32();
        var data = r.ReadOpaqueMemory();
        return new Vxi11ReadResult(error, reason, data);
    }

    private async ValueTask<Vxi11StatusByteResult> ReadStatusByteCoreAsync(Deadline deadline, bool async)
    {
        var r = await GenericCallAsync(Vxi11Protocol.ProcDeviceReadStb, deadline, async).ConfigureAwait(false);
        var error = (Vxi11Error)r.ReadInt32();
        byte stb = (byte)r.ReadUInt32();
        return new Vxi11StatusByteResult(error, stb);
    }

    private async ValueTask<Vxi11Error> GenericErrorAsync(uint procedure, Deadline deadline, bool async) =>
        (Vxi11Error)(await GenericCallAsync(procedure, deadline, async).ConfigureAwait(false)).ReadInt32();

    private ValueTask<XdrReader> GenericCallAsync(uint procedure, Deadline deadline, bool async)
    {
        var (io, reply) = Timeouts(deadline);
        return CoreCallAsync(procedure, w =>
        {
            w.WriteInt32(LinkId);
            w.WriteInt32(0); // flags
            w.WriteUInt32(0); // lock_timeout
            w.WriteUInt32(io);
        }, reply, async);
    }

    private async ValueTask<Vxi11Error> LockCoreAsync(int timeoutMilliseconds, Deadline deadline, bool async)
    {
        uint lockTimeout = timeoutMilliseconds < 0 ? int.MaxValue : (uint)timeoutMilliseconds;
        var reply = deadline.Extend(ReplySlackMs);
        try
        {
            return (Vxi11Error)(await CoreCallAsync(Vxi11Protocol.ProcDeviceLock, w =>
            {
                w.WriteInt32(LinkId);
                w.WriteInt32(Vxi11Protocol.FlagWaitLock);
                w.WriteUInt32(lockTimeout);
            }, reply, async).ConfigureAwait(false)).ReadInt32();
        }
        catch (LanTimeoutException)
        {
            ReleaseAbandonedLock(lockTimeout);
            throw;
        }
    }

    private void ReleaseAbandonedLock(uint lockTimeout)
    {
        if (_disposed) return;
        BeginAbort();
        // The core channel runs calls in order: this unlock runs after the abandoned device_lock ends. This link held no
        // device lock before (VISA takes it once per session), so the unlock only releases a late grant.
        int wait = (int)Math.Min(lockTimeout, int.MaxValue - ReplySlackMs) + ReplySlackMs;
        _ = Task.Run(async () =>
        {
            try { await UnlockCoreAsync(Deadline.FromMilliseconds(wait), async: true).ConfigureAwait(false); }
            catch { /* closing the link releases the lock anyway */ }
        });
    }

    private async ValueTask<Vxi11Error> UnlockCoreAsync(Deadline deadline, bool async) =>
        (Vxi11Error)(await CoreCallAsync(Vxi11Protocol.ProcDeviceUnlock, w => w.WriteInt32(LinkId), deadline, async).ConfigureAwait(false))
            .ReadInt32();

    private async ValueTask<Vxi11Error> EnableSrqCoreAsync(bool enable, ReadOnlyMemory<byte> handle, Deadline deadline, bool async) =>
        (Vxi11Error)(await CoreCallAsync(Vxi11Protocol.ProcDeviceEnableSrq, w =>
        {
            w.WriteInt32(LinkId);
            w.WriteBool(enable);
            w.WriteOpaque(handle.Span);
        }, deadline, async).ConfigureAwait(false)).ReadInt32();

    private async ValueTask<Vxi11DocmdResult> DocmdCoreAsync(int command, bool networkOrder, int dataSize, ReadOnlyMemory<byte> dataIn,
        Deadline deadline, bool async)
    {
        var (io, reply) = Timeouts(deadline);
        var r = await CoreCallAsync(Vxi11Protocol.ProcDeviceDocmd, w =>
        {
            w.WriteInt32(LinkId);
            w.WriteInt32(0); // flags
            w.WriteUInt32(io);
            w.WriteUInt32(0); // lock_timeout
            w.WriteInt32(command);
            w.WriteBool(networkOrder);
            w.WriteInt32(dataSize);
            w.WriteOpaque(dataIn.Span);
        }, reply, async).ConfigureAwait(false);
        var error = (Vxi11Error)r.ReadInt32();
        return new Vxi11DocmdResult(error, r.ReadOpaque());
    }

    private async ValueTask<Vxi11Error> CreateIntrChanCoreAsync(IPAddress hostAddress, int hostPort, Deadline deadline, bool async)
    {
        ArgumentNullException.ThrowIfNull(hostAddress);
        byte[] bytes = hostAddress.GetAddressBytes();
        if (bytes.Length != 4)
            throw LanErrors.NotSupported("VXI-11 service requests require an IPv4 connection.");
        uint address = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        return (Vxi11Error)(await CoreCallAsync(Vxi11Protocol.ProcCreateIntrChan, w =>
        {
            w.WriteUInt32(address);
            w.WriteUInt32((uint)hostPort);
            w.WriteUInt32(Vxi11Protocol.InterruptProgram);
            w.WriteUInt32(Vxi11Protocol.Version);
            w.WriteUInt32(Vxi11Protocol.AddrFamilyTcp);
        }, deadline, async).ConfigureAwait(false)).ReadInt32();
    }

    private async ValueTask<Vxi11Error> DestroyIntrChanCoreAsync(Deadline deadline, bool async) =>
        (Vxi11Error)(await CoreCallAsync(Vxi11Protocol.ProcDestroyIntrChan, null, deadline, async).ConfigureAwait(false)).ReadInt32();

    /// <summary>
    /// Starts a device_abort in the background (best effort), to end the core-channel call in progress. The next
    /// core-channel call waits for it. Does nothing when the device has no abort channel.
    /// </summary>
    public void BeginAbort()
    {
        if (AbortPort == 0 || _disposed) return;
        var previous = Volatile.Read(ref _pendingAbort);
        Volatile.Write(ref _pendingAbort, Task.Run(async () =>
        {
            await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            try { await AbortCoreAsync(async: true).ConfigureAwait(false); } catch { /* best effort */ }
        }));
    }

    private async ValueTask<Vxi11Error> AbortCoreAsync(bool async)
    {
        if (AbortPort == 0) throw LanErrors.NotSupported("device_abort is not supported by this device (no abort channel).");
        var deadline = Deadline.FromMilliseconds(AbortTimeoutMs);
        await _abortGate.EnterOrThrowAsync(deadline, async, "the device_abort in progress").ConfigureAwait(false);
        try
        {
            if (_disposed) throw LanErrors.ConnectionLost("The VXI-11 link is closed.");
            var abort = _abort ??= await RpcTcpClient.ConnectAsync(new IPEndPoint(_address, AbortPort), Vxi11Protocol.AbortProgram,
                Vxi11Protocol.Version, deadline, async).ConfigureAwait(false);
            if (_disposed) abort.Dispose();
            try
            {
                var reply = await abort.CallAsync(Vxi11Protocol.ProcDeviceAbort, w => w.WriteInt32(LinkId), deadline, async).ConfigureAwait(false);
                return (Vxi11Error)reply.ReadInt32();
            }
            catch (Exception ex) when (LanErrors.IsLanError(ex))
            {
                // Reconnect next time: the device may have dropped the abort channel.
                abort.Dispose();
                _abort = null;
                throw;
            }
        }
        finally
        {
            _abortGate.Release();
        }
    }

    private async ValueTask DestroyLinkCoreAsync(int timeoutMs, bool async)
    {
        try
        {
            await CoreCallAsync(Vxi11Protocol.ProcDestroyLink, w => w.WriteInt32(LinkId), Deadline.FromMilliseconds(timeoutMs), async)
                .ConfigureAwait(false);
        }
        catch
        {
            // Best effort during close.
        }
    }

    /// <summary>Closes the connections (the device then destroys the link and releases its locks).</summary>
    public void Dispose()
    {
        _disposed = true;
        _core.Dispose();
        _abort?.Dispose();
    }
}

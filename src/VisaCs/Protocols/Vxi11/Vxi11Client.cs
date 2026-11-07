using System.Net;
using VisaCs.Internal;
using VisaCs.Protocols.Rpc;

namespace VisaCs.Protocols.Vxi11;

/// <summary>
/// Client for the VXI-11 core and abort channels of one device link. Every call takes a <c>bool async</c>:
/// false blocks the calling thread, true holds none while waiting for the reply.
/// </summary>
/// <remarks>
/// Core-channel calls are serialized by the RPC client. All channels go to the address the portmapper answered on,
/// so a host name with several addresses cannot split one link across machines. A device_abort started with
/// <see cref="BeginAbort"/> is completed before the next core-channel call is sent, so it cannot end that call.
/// </remarks>
internal sealed class Vxi11Client : IDisposable
{
    /// <summary>Additional time allowed for the RPC reply beyond the device-side I/O or lock timeout.</summary>
    public const int ReplySlackMs = 2000;

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

    public int LinkId { get; private set; }
    public int AbortPort { get; private set; }
    public uint MaxReceiveSize { get; private set; }
    public IPEndPoint LocalEndPoint => _core.LocalEndPoint;
    public IPEndPoint RemoteEndPoint => _core.RemoteEndPoint;
    public System.Net.Sockets.Socket CoreSocket => _core.Socket;

    /// <summary>True while a core-channel call is in progress.</summary>
    public bool IsBusy => _core.IsBusy;

    /// <summary>
    /// Asks the portmapper for the core channel, connects to it, creates the link and (best effort) connects the
    /// abort channel. <paramref name="deadline"/> covers all of it.
    /// </summary>
    public static async ValueTask<Vxi11Client> ConnectAsync(string host, string deviceName, int portmapperPort, int clientId,
        Deadline deadline, bool async)
    {
        IPAddress address;
        int corePort;
        using (var portmapper = await Portmapper.ConnectAsync(host, portmapperPort, deadline, async).ConfigureAwait(false))
        {
            address = portmapper.RemoteEndPoint.Address;
            corePort = await Portmapper.GetPortAsync(portmapper, host, Vxi11Constants.CoreProgram, Vxi11Constants.Version, deadline, async)
                .ConfigureAwait(false);
        }
        var core = await RpcTcpClient.ConnectAsync(new IPEndPoint(address, corePort), Vxi11Constants.CoreProgram, Vxi11Constants.Version,
            deadline, async).ConfigureAwait(false);
        var client = new Vxi11Client(core, address);
        try
        {
            var r = await core.CallAsync(Vxi11Constants.ProcCreateLink, w =>
            {
                w.WriteInt32(clientId);
                w.WriteBool(false); // lockDevice: VISA locks are taken with device_lock
                w.WriteUInt32(0);   // lock_timeout
                w.WriteString(deviceName);
            }, deadline, async).ConfigureAwait(false);
            int error = r.ReadInt32();
            int linkId = r.ReadInt32();
            uint abortPort = r.ReadUInt32();
            uint maxRecv = r.ReadUInt32();
            if (error != 0) throw Vxi11Constants.ToException(error, "create_link");
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
            _abort = await RpcTcpClient.ConnectAsync(new IPEndPoint(_address, AbortPort), Vxi11Constants.AbortProgram, Vxi11Constants.Version,
                deadline, async).ConfigureAwait(false);
        }
        catch (VisaException) when (!deadline.IsCanceled)
        {
            // Not fatal: AbortAsync connects when it is needed.
        }
    }

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

    public async ValueTask<(int Error, uint Size)> DeviceWriteAsync(ReadOnlyMemory<byte> data, Deadline deadline, int flags, bool async)
    {
        var (io, reply) = Timeouts(deadline);
        var r = await CoreCallAsync(Vxi11Constants.ProcDeviceWrite, w =>
        {
            w.WriteInt32(LinkId);
            w.WriteUInt32(io);
            w.WriteUInt32(0); // lock_timeout: fail at once when another link holds the lock, like VISA
            w.WriteInt32(flags);
            w.WriteOpaque(data.Span);
        }, reply, async).ConfigureAwait(false);
        return (r.ReadInt32(), r.ReadUInt32());
    }

    /// <summary>device_read. The returned data is a slice of the reply buffer, valid until the caller drops it.</summary>
    public async ValueTask<(int Error, int Reason, ReadOnlyMemory<byte> Data)> DeviceReadAsync(uint requestSize, Deadline deadline,
        int flags, byte termChar, bool async)
    {
        var (io, reply) = Timeouts(deadline);
        var r = await CoreCallAsync(Vxi11Constants.ProcDeviceRead, w =>
        {
            w.WriteInt32(LinkId);
            w.WriteUInt32(requestSize);
            w.WriteUInt32(io);
            w.WriteUInt32(0); // lock_timeout
            w.WriteInt32(flags);
            w.WriteInt32(termChar);
        }, reply, async).ConfigureAwait(false);
        int error = r.ReadInt32();
        int reason = r.ReadInt32();
        var data = r.ReadOpaqueMemory();
        return (error, reason, data);
    }

    public async ValueTask<(int Error, byte Stb)> DeviceReadStbAsync(Deadline deadline, bool async)
    {
        var r = await GenericCallAsync(Vxi11Constants.ProcDeviceReadStb, deadline, async).ConfigureAwait(false);
        int error = r.ReadInt32();
        byte stb = (byte)r.ReadUInt32();
        return (error, stb);
    }

    public async ValueTask<int> DeviceTriggerAsync(Deadline deadline, bool async) =>
        (await GenericCallAsync(Vxi11Constants.ProcDeviceTrigger, deadline, async).ConfigureAwait(false)).ReadInt32();

    public async ValueTask<int> DeviceClearAsync(Deadline deadline, bool async) =>
        (await GenericCallAsync(Vxi11Constants.ProcDeviceClear, deadline, async).ConfigureAwait(false)).ReadInt32();

    public async ValueTask<int> DeviceRemoteAsync(Deadline deadline, bool async) =>
        (await GenericCallAsync(Vxi11Constants.ProcDeviceRemote, deadline, async).ConfigureAwait(false)).ReadInt32();

    public async ValueTask<int> DeviceLocalAsync(Deadline deadline, bool async) =>
        (await GenericCallAsync(Vxi11Constants.ProcDeviceLocal, deadline, async).ConfigureAwait(false)).ReadInt32();

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

    /// <summary>
    /// device_lock, waiting up to <paramref name="lockTimeout"/> ms on the device. If the reply does not arrive in time
    /// (the caller cancelled), the device may still grant the lock later: the wait is aborted and the lock released in
    /// the background.
    /// </summary>
    public async ValueTask<int> DeviceLockAsync(uint lockTimeout, Deadline deadline, bool async)
    {
        var reply = deadline.Extend(ReplySlackMs);
        try
        {
            return (await CoreCallAsync(Vxi11Constants.ProcDeviceLock, w =>
            {
                w.WriteInt32(LinkId);
                w.WriteInt32(Vxi11Constants.FlagWaitLock);
                w.WriteUInt32(lockTimeout);
            }, reply, async).ConfigureAwait(false)).ReadInt32();
        }
        catch (VisaTimeoutException)
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
            try { await DeviceUnlockAsync(Deadline.FromMilliseconds(wait), async: true).ConfigureAwait(false); }
            catch { /* closing the link releases the lock anyway */ }
        });
    }

    public async ValueTask<int> DeviceUnlockAsync(Deadline deadline, bool async) =>
        (await CoreCallAsync(Vxi11Constants.ProcDeviceUnlock, w => w.WriteInt32(LinkId), deadline, async).ConfigureAwait(false)).ReadInt32();

    public async ValueTask<int> DeviceEnableSrqAsync(bool enable, ReadOnlyMemory<byte> handle, Deadline deadline, bool async) =>
        (await CoreCallAsync(Vxi11Constants.ProcDeviceEnableSrq, w =>
        {
            w.WriteInt32(LinkId);
            w.WriteBool(enable);
            w.WriteOpaque(handle.Span);
        }, deadline, async).ConfigureAwait(false)).ReadInt32();

    public async ValueTask<(int Error, byte[] Data)> DeviceDocmdAsync(int command, bool networkOrder, int dataSize, ReadOnlyMemory<byte> dataIn,
        Deadline deadline, bool async)
    {
        var (io, reply) = Timeouts(deadline);
        var r = await CoreCallAsync(Vxi11Constants.ProcDeviceDocmd, w =>
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
        int error = r.ReadInt32();
        return (error, r.ReadOpaque());
    }

    public async ValueTask<int> CreateInterruptChannelAsync(IPAddress hostAddress, int hostPort, Deadline deadline, bool async)
    {
        byte[] bytes = hostAddress.GetAddressBytes();
        if (bytes.Length != 4)
            throw new VisaException(VisaStatus.ErrorOperationNotSupported, "VXI-11 service requests require an IPv4 connection.");
        uint address = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        return (await CoreCallAsync(Vxi11Constants.ProcCreateIntrChan, w =>
        {
            w.WriteUInt32(address);
            w.WriteUInt32((uint)hostPort);
            w.WriteUInt32(Vxi11Constants.InterruptProgram);
            w.WriteUInt32(Vxi11Constants.Version);
            w.WriteUInt32(Vxi11Constants.AddrFamilyTcp);
        }, deadline, async).ConfigureAwait(false)).ReadInt32();
    }

    public async ValueTask<int> DestroyInterruptChannelAsync(Deadline deadline, bool async) =>
        (await CoreCallAsync(Vxi11Constants.ProcDestroyIntrChan, null, deadline, async).ConfigureAwait(false)).ReadInt32();

    /// <summary>
    /// Starts a device_abort in the background (best effort), to end the core-channel call in progress. The next
    /// core-channel call waits for it.
    /// </summary>
    public void BeginAbort()
    {
        if (AbortPort == 0 || _disposed) return;
        var previous = Volatile.Read(ref _pendingAbort);
        Volatile.Write(ref _pendingAbort, Task.Run(async () =>
        {
            await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            try { await AbortAsync(async: true).ConfigureAwait(false); } catch { /* best effort */ }
        }));
    }

    /// <summary>Sends device_abort on the abort channel, cancelling an in-progress core channel call.</summary>
    public async ValueTask<int> AbortAsync(bool async)
    {
        if (AbortPort == 0) throw VisaException.NotSupported("device_abort");
        var deadline = Deadline.FromMilliseconds(AbortTimeoutMs);
        await _abortGate.EnterOrThrowAsync(deadline, async, "the device_abort in progress").ConfigureAwait(false);
        try
        {
            if (_disposed) throw new VisaException(VisaStatus.ErrorConnectionLost, "The VXI-11 link is closed.");
            var abort = _abort ??= await RpcTcpClient.ConnectAsync(new IPEndPoint(_address, AbortPort), Vxi11Constants.AbortProgram,
                Vxi11Constants.Version, deadline, async).ConfigureAwait(false);
            if (_disposed) abort.Dispose();
            try
            {
                var reply = await abort.CallAsync(Vxi11Constants.ProcDeviceAbort, w => w.WriteInt32(LinkId), deadline, async).ConfigureAwait(false);
                return reply.ReadInt32();
            }
            catch (VisaException)
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

    public async ValueTask DestroyLinkAsync(int timeoutMs, bool async)
    {
        try
        {
            await CoreCallAsync(Vxi11Constants.ProcDestroyLink, w => w.WriteInt32(LinkId), Deadline.FromMilliseconds(timeoutMs), async)
                .ConfigureAwait(false);
        }
        catch
        {
            // Best effort during close.
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _core.Dispose();
        _abort?.Dispose();
    }
}

using System.Runtime.CompilerServices;
using VisaCs.Internal;
using VisaCs.Usb;

namespace VisaCs;

/// <summary>
/// One opened USB interface, shared by every session to the same resource that goes through the same backend
/// instance (operating system USB handles are exclusive). It owns what the sessions share: the handle, the gate that
/// makes their operations take turns, and the loop that reads the interrupt-IN endpoint for all of them. On a USBTMC
/// interface (or once an INSTR session asks for it) it also holds the one <see cref="UsbTmcDevice"/> client, which
/// takes the same gate and gets its status byte responses from the shared interrupt loop; RAW sessions use the handle
/// directly.
/// </summary>
/// <remarks>
/// The registry is kept per backend instance because a handle belongs to the backend that opened it: disposing
/// <see cref="LibUsbBackend"/> closes its handles, which must not take the sessions of another backend with them.
/// </remarks>
internal sealed class SharedUsbDevice
{
    private static readonly ConditionalWeakTable<IUsbBackend, Dictionary<string, SharedUsbDevice>> Registry = new();

    private readonly Dictionary<string, SharedUsbDevice> _devices; // this backend's registry, also its lock
    private readonly string _key;
    private int _refCount;

    private readonly int _interruptIn;
    private readonly int _interruptPacketSize;
    private readonly Lock _sync = new(); // guards the client and the interrupt reader
    private UsbTmcDevice? _tmc;
    private readonly List<Action<UsbTmcInterrupt>> _handlers = new();
    private CancellationTokenSource? _readerStop;
    private Task? _reader;

    private SharedUsbDevice(Dictionary<string, SharedUsbDevice> devices, string key, IUsbDeviceHandle handle)
    {
        _devices = devices;
        _key = key;
        Handle = handle;
        var interrupt = handle.Interface.FindEndpoint(UsbTransferType.Interrupt, input: true);
        _interruptIn = interrupt?.Address ?? -1;
        _interruptPacketSize = Math.Max(2, (int)(interrupt?.MaxPacketSize ?? 2));
        // On a USBTMC interface the client exists from the start, so that USB488 status byte responses never reach
        // the sessions' interrupt handlers, whichever kind of session is open.
        if (handle.Interface.IsUsbTmc) _tmc = CreateClient();
    }

    public IUsbDeviceHandle Handle { get; }

    /// <summary>Makes the operations of all sessions to the interface (and of the USBTMC client) take turns.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>The USBTMC client of the interface, for INSTR sessions; created on first use.</summary>
    public UsbTmcDevice Tmc
    {
        get
        {
            lock (_sync) return _tmc ??= CreateClient();
        }
    }

    private UsbTmcDevice CreateClient() =>
        new(Handle, leaveOpen: true, Gate) { ExternalInterruptReader = _reader is not null };

    /// <summary>Opens the interface, or returns the instance already open through <paramref name="backend"/>.</summary>
    public static SharedUsbDevice Acquire(IUsbBackend backend, UsbDeviceInfo device, int interfaceNumber)
    {
        var devices = Registry.GetValue(backend, static _ => new Dictionary<string, SharedUsbDevice>(StringComparer.OrdinalIgnoreCase));
        string key = $"{device.Location}|{device.VendorId:X4}:{device.ProductId:X4}:{device.SerialNumber}|{interfaceNumber}";
        lock (devices)
        {
            if (!devices.TryGetValue(key, out var shared))
            {
                shared = new SharedUsbDevice(devices, key, UsbErrorMapping.Run(() => backend.Open(device, interfaceNumber)));
                devices[key] = shared;
            }
            shared._refCount++;
            return shared;
        }
    }

    /// <summary>Drops one session's reference; the last one closes the interface.</summary>
    public async ValueTask ReleaseAsync(bool async)
    {
        lock (_devices)
        {
            if (--_refCount > 0) return;
            _devices.Remove(_key);
        }
        await StopReaderAsync(async).ConfigureAwait(false);
        UsbTmcDevice? tmc;
        lock (_sync) tmc = _tmc;
        if (tmc is not null)
        {
            if (async) await tmc.DisposeAsync().ConfigureAwait(false);
            else tmc.Dispose();
        }
        Handle.Dispose();
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the handle while no other operation on the interface is in progress, with the
    /// handle's exceptions translated to VISA ones. Waiting for the other operations is bounded by
    /// <paramref name="deadline"/>; the action is given the deadline's token.
    /// </summary>
    public async ValueTask<T> RunExclusiveAsync<T>(Deadline deadline, Func<IUsbDeviceHandle, CancellationToken, bool, ValueTask<T>> action, bool async)
    {
        var cancellationToken = deadline.CancellationToken;
        int wait = deadline.RemainingMilliseconds; // -1: no limit
        bool entered = async
            ? await Gate.WaitAsync(wait, cancellationToken).ConfigureAwait(false)
            : Gate.Wait(wait, cancellationToken);
        if (!entered) throw new VisaTimeoutException("Another operation kept the USB device busy until the timeout.");
        try
        {
            return await action(Handle, cancellationToken, async).ConfigureAwait(false);
        }
        catch (Exception ex) when (UsbErrorMapping.IsUsbError(ex))
        {
            var error = UsbErrorMapping.ToVisa(ex);
            throw cancellationToken.IsCancellationRequested ? AsyncIO.Canceled(error, cancellationToken) : error;
        }
        finally
        {
            Gate.Release();
        }
    }

    // ------------------------------------------------------------------ interrupt endpoint

    /// <summary>
    /// Adds a session's handler for packets from the interrupt-IN endpoint; the first one starts the read loop. USB488
    /// status byte responses go to the USBTMC client instead.
    /// </summary>
    public void Subscribe(Action<UsbTmcInterrupt> handler)
    {
        lock (_sync)
        {
            _handlers.Add(handler);
            if (_reader is not null || _interruptIn < 0) return;
            if (_tmc is not null) _tmc.ExternalInterruptReader = true;
            _readerStop = new CancellationTokenSource();
            var stop = _readerStop.Token;
            _reader = Task.Run(() => ReadInterruptsAsync(stop));
        }
    }

    /// <summary>Removes a session's handler; removing the last one stops the read loop and waits up to a second for it.</summary>
    public ValueTask UnsubscribeAsync(Action<UsbTmcInterrupt> handler, bool async)
    {
        bool stop;
        lock (_sync) stop = _handlers.Remove(handler) && _handlers.Count == 0;
        return stop ? StopReaderAsync(async) : default;
    }

    private async ValueTask StopReaderAsync(bool async)
    {
        Task? reader;
        lock (_sync)
        {
            reader = _reader;
            _reader = null;
            _readerStop?.Cancel();
            _readerStop = null;
            if (_tmc is not null) _tmc.ExternalInterruptReader = false;
        }
        if (reader is null) return;
        try
        {
            if (async) await reader.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            else reader.Wait(1000);
        }
        catch
        {
            // The loop ends on its own.
        }
    }

    private async Task ReadInterruptsAsync(CancellationToken stop)
    {
        var packet = new byte[_interruptPacketSize];
        while (!stop.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await Handle.InterruptTransferAsync((byte)_interruptIn, packet, Timeout.Infinite, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (UsbTimeoutException)
            {
                continue;
            }
            catch
            {
                try { await Task.Delay(50, stop).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                continue;
            }
            if (n > 0) Dispatch(packet.AsSpan(0, n).ToArray());
        }
    }

    /// <summary>Hands a status byte response to the USBTMC client, and anything else to the sessions' handlers.</summary>
    private void Dispatch(byte[] data)
    {
        Action<UsbTmcInterrupt>[] handlers;
        UsbTmcDevice? tmc;
        lock (_sync)
        {
            tmc = _tmc;
            handlers = _handlers.ToArray();
        }
        if (tmc is not null && tmc.ProcessInterrupt(data)) return;
        if (handlers.Length == 0) return;
        var e = new UsbTmcInterrupt(data);
        foreach (var h in handlers)
        {
            try { h(e); } catch { /* ignore */ }
        }
    }
}

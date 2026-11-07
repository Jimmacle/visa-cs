using System.Runtime.CompilerServices;
using VisaCs.Internal;
using VisaCs.Usb;

namespace VisaCs;

/// <summary>
/// One opened USB interface, shared by every session to the same resource that goes through the same backend
/// instance (operating system USB handles are exclusive). Transfers are serialized with <see cref="EnterAsync"/>,
/// USBTMC bTags are counted per device, and one asynchronous loop (holding no thread while idle) reads the interrupt
/// endpoint for all subscribers.
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
    private readonly bool _usb488;
    private readonly List<Action<byte[]>> _subscribers = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _statusSync = new();
    private readonly Queue<(byte Tag, byte Stb)> _statusResponses = new();
    private readonly ChangeSignal _statusChanged = new();
    private int _refCount;
    private CancellationTokenSource? _readerStop;
    private Task? _reader;
    // Random start values, so that a reopened device does not see the bTag of the previous session again.
    private byte _bTag = (byte)Random.Shared.Next(1, 256);
    private byte _statusTag = (byte)Random.Shared.Next(2, 128);

    private SharedUsbDevice(Dictionary<string, SharedUsbDevice> devices, string key, IUsbDeviceHandle handle)
    {
        _devices = devices;
        _key = key;
        Handle = handle;
        _usb488 = handle.Interface.IsUsbTmc && handle.Interface.Protocol == 1;
    }

    public IUsbDeviceHandle Handle { get; }

    public bool ReaderRunning
    {
        get
        {
            lock (_subscribers) return _reader is not null;
        }
    }

    public int InterruptEndpoint { get; set; } = -1;

    public int InterruptPacketSize { get; set; } = 8;

    /// <summary>Opens the interface, or returns the instance already open through <paramref name="backend"/>.</summary>
    public static SharedUsbDevice Acquire(IUsbBackend backend, UsbDeviceInfo device, int interfaceNumber)
    {
        var devices = Registry.GetValue(backend, static _ => new Dictionary<string, SharedUsbDevice>(StringComparer.OrdinalIgnoreCase));
        string key = $"{device.Location}|{device.VendorId:X4}:{device.ProductId:X4}:{device.SerialNumber}|{interfaceNumber}";
        lock (devices)
        {
            if (!devices.TryGetValue(key, out var shared))
            {
                shared = new SharedUsbDevice(devices, key, backend.Open(device, interfaceNumber));
                devices[key] = shared;
            }
            shared._refCount++;
            return shared;
        }
    }

    /// <summary>
    /// Serializes the transfers of the sessions sharing the device. Waits at most until <paramref name="deadline"/>
    /// (<see cref="VisaTimeoutException"/>); dispose the result to leave.
    /// </summary>
    public async ValueTask<IOGate.Releaser> EnterAsync(Deadline deadline, bool async)
    {
        int timeout = deadline.RemainingMilliseconds; // -1: no limit
        bool entered = async
            ? await _gate.WaitAsync(timeout, deadline.CancellationToken).ConfigureAwait(false)
            : _gate.Wait(timeout, deadline.CancellationToken);
        if (!entered) throw new VisaTimeoutException("Another session kept the USB device busy until the timeout.");
        return new IOGate.Releaser(_gate);
    }

    /// <summary>The next USBTMC bTag (1 to 255). Consecutive Bulk-OUT headers must differ, whichever session sends them.</summary>
    public byte NextTag()
    {
        lock (_statusSync)
        {
            _bTag = (byte)(_bTag == 255 ? 1 : _bTag + 1);
            return _bTag;
        }
    }

    /// <summary>The next USB488 READ_STATUS_BYTE bTag (2 to 127; 1 is reserved for service requests).</summary>
    public byte NextStatusTag()
    {
        lock (_statusSync)
        {
            _statusTag = (byte)(_statusTag >= 127 ? 2 : _statusTag + 1);
            return _statusTag;
        }
    }

    public async ValueTask ReleaseAsync(bool async)
    {
        lock (_devices)
        {
            if (--_refCount > 0) return;
            _devices.Remove(_key);
        }
        await StopReaderAsync(async).ConfigureAwait(false);
        Handle.Dispose();
    }

    public void Subscribe(Action<byte[]> handler)
    {
        lock (_subscribers)
        {
            _subscribers.Add(handler);
            if (_reader is not null || InterruptEndpoint < 0) return;
            _readerStop = new CancellationTokenSource();
            var stop = _readerStop.Token;
            _reader = Task.Run(() => ReadLoopAsync(stop));
        }
    }

    public ValueTask UnsubscribeAsync(Action<byte[]> handler, bool async)
    {
        bool stop;
        lock (_subscribers)
        {
            _subscribers.Remove(handler);
            stop = _subscribers.Count == 0;
        }
        return stop ? StopReaderAsync(async) : default;
    }

    private async ValueTask StopReaderAsync(bool async)
    {
        Task? reader;
        lock (_subscribers)
        {
            reader = _reader;
            _reader = null;
            _readerStop?.Cancel();
            _readerStop = null;
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

    private async Task ReadLoopAsync(CancellationToken stop)
    {
        var packet = new byte[Math.Max(2, InterruptPacketSize)];
        while (!stop.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await Handle.InterruptTransferAsync((byte)InterruptEndpoint, packet, 0, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (VisaTimeoutException)
            {
                continue;
            }
            catch
            {
                try { await Task.Delay(50, stop).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                continue;
            }
            if (n <= 0) continue;
            var data = packet.AsSpan(0, n).ToArray();
            // A USB488 READ_STATUS_BYTE response ([0x80 | bTag, STB], bTag > 1) goes to the session waiting for it.
            if (_usb488 && n >= 2 && (data[0] & 0x80) != 0 && (data[0] & 0x7F) > 1)
            {
                AddStatusResponse((byte)(data[0] & 0x7F), data[1]);
                continue;
            }
            Action<byte[]>[] subscribers;
            lock (_subscribers) subscribers = _subscribers.ToArray();
            foreach (var s in subscribers)
            {
                try { s(data); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>Records a READ_STATUS_BYTE response ([bTag, STB]) that arrived on the interrupt endpoint.</summary>
    public void AddStatusResponse(byte tag, byte stb)
    {
        lock (_statusSync)
        {
            _statusResponses.Enqueue((tag, stb));
            _statusChanged.Pulse();
        }
    }

    /// <summary>Waits for the READ_STATUS_BYTE response with <paramref name="tag"/>; null on timeout.</summary>
    public async ValueTask<byte?> TakeStatusResponseAsync(byte tag, Deadline deadline, bool async)
    {
        while (true)
        {
            Task next;
            lock (_statusSync)
            {
                while (_statusResponses.TryDequeue(out var item))
                {
                    if (item.Tag == tag) return item.Stb;
                }
                next = _statusChanged.Next;
            }
            if (!await ChangeSignal.WaitAsync(next, deadline, async).ConfigureAwait(false)) return null;
        }
    }
}

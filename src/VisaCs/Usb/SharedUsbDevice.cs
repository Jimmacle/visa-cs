using System.Runtime.CompilerServices;
using VisaCs.Usb;

namespace VisaCs;

/// <summary>
/// One opened USB interface, shared by every session to the same resource that goes through the same backend
/// instance (operating system USB handles are exclusive). The sessions share one <see cref="UsbTmcDevice"/>, which
/// serializes their operations, counts the USBTMC bTags and reads the interrupt endpoint for all of them; RAW
/// sessions use it only for that serialization and the interrupt packets.
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

    private SharedUsbDevice(Dictionary<string, SharedUsbDevice> devices, string key, IUsbDeviceHandle handle)
    {
        _devices = devices;
        _key = key;
        Device = new UsbTmcDevice(handle);
    }

    /// <summary>The client all sessions to this interface share.</summary>
    public UsbTmcDevice Device { get; }

    public IUsbDeviceHandle Handle => Device.Handle;

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

    /// <summary>Drops one session's reference; the last one closes the interface.</summary>
    public async ValueTask ReleaseAsync(bool async)
    {
        lock (_devices)
        {
            if (--_refCount > 0) return;
            _devices.Remove(_key);
        }
        if (async) await Device.DisposeAsync().ConfigureAwait(false);
        else Device.Dispose();
    }

    /// <summary>Adds an interrupt handler for a session (the client starts its read loop for the first one).</summary>
    public void Subscribe(Action<UsbTmcInterrupt> handler) => Device.AddInterruptHandler(handler);

    /// <summary>Removes a session's interrupt handler.</summary>
    public ValueTask UnsubscribeAsync(Action<UsbTmcInterrupt> handler, bool async)
    {
        if (async) return Device.RemoveInterruptHandlerAsync(handler);
        Device.RemoveInterruptHandler(handler);
        return default;
    }
}

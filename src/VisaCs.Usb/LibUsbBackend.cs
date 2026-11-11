using System.Runtime.InteropServices;
using VisaCs.Internal;

namespace VisaCs.Usb;

/// <summary>
/// USB access through libusb-1.0, on any operating system where it is installed (the default backend on macOS). The
/// library is loaded at run time, so it is only needed when this backend is used.
/// </summary>
/// <remarks>
/// <para>
/// Enumeration opens each device briefly to read its string descriptors (as ASCII; they stay empty for devices the
/// process cannot open). On Linux, <see cref="Open"/> detaches a kernel driver bound to the interface and reattaches
/// it when the handle is disposed; an interface that another program has claimed is reported as busy.
/// </para>
/// <para>
/// All transfers, synchronous ones included, use libusb's asynchronous API. The first transfer starts a background
/// thread that runs libusb's event handling for this backend and completes them. On macOS, libusb cancels a
/// timed-out or cancelled transfer by aborting its endpoint, which also cancels the other transfers pending on that
/// endpoint.
/// </para>
/// <para>
/// Disposing the backend closes the handles it opened (their transfers throw <see cref="ObjectDisposedException"/>),
/// stops the event thread and releases libusb; the backend cannot be used afterwards.
/// </para>
/// </remarks>
public sealed partial class LibUsbBackend : IUsbBackend
{
    /// <summary>How long disposing a handle waits for libusb to report its cancelled transfers.</summary>
    private const int CancelWaitMs = 5000;

    private nint _context;
    private readonly Lock _sync = new();
    private readonly List<Handle> _handles = new();
    private EventLoop? _events;
    private bool _wakeForTimeouts;
    private bool _disposed;

    /// <inheritdoc/>
    public string Name => "libusb";

    /// <summary>True if libusb-1.0 can be loaded.</summary>
    public static bool IsAvailable => NativeLibraries.IsAvailable(NativeLibraries.LibUsb);

    private unsafe nint Context
    {
        get
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_context != 0) return _context;
                NativeLibraries.EnsureRegistered();
                nint ctx;
                int rc = Native.libusb_init(&ctx);
                if (rc != 0) throw UsbErrors.Create(UsbError.LibraryNotFound, $"libusb_init failed ({rc}).", rc);
                _context = ctx;
                // Without a timer descriptor (macOS) libusb computes the next transfer timeout when event handling
                // starts, so the event thread is woken after each submission to pick up the new timeout.
                _wakeForTimeouts = Native.libusb_pollfds_handle_timeouts(ctx) == 0;
                return ctx;
            }
        }
    }

    /// <inheritdoc/>
    public unsafe IReadOnlyList<UsbDeviceInfo> EnumerateDevices()
    {
        var result = new List<UsbDeviceInfo>();
        nint* list;
        nint count = Native.libusb_get_device_list(Context, &list);
        if (count < 0) return result;
        try
        {
            for (int i = 0; i < count; i++)
            {
                nint dev = list[i];
                Native.DeviceDescriptor dd;
                if (Native.libusb_get_device_descriptor(dev, &dd) != 0) continue;
                var interfaces = ReadInterfaces(dev);
                string? serial = null, manufacturer = null, product = null;
                nint handle;
                if (Native.libusb_open(dev, &handle) == 0)
                {
                    serial = ReadString(handle, dd.iSerialNumber);
                    manufacturer = ReadString(handle, dd.iManufacturer);
                    product = ReadString(handle, dd.iProduct);
                    Native.libusb_close(handle);
                }
                byte bus = Native.libusb_get_bus_number(dev), address = Native.libusb_get_device_address(dev);
                result.Add(new UsbDeviceInfo
                {
                    VendorId = dd.idVendor,
                    ProductId = dd.idProduct,
                    SerialNumber = serial ?? "",
                    Manufacturer = manufacturer,
                    Product = product,
                    DeviceRelease = dd.bcdDevice,
                    DeviceClass = dd.bDeviceClass,
                    Interfaces = interfaces,
                    Location = $"bus {bus} address {address}",
                    BackendData = (bus, address),
                });
            }
        }
        finally
        {
            Native.libusb_free_device_list(list, 1);
        }
        return result;
    }

    private static unsafe string? ReadString(nint handle, byte index)
    {
        if (index == 0) return null;
        byte* buffer = stackalloc byte[256];
        int n = Native.libusb_get_string_descriptor_ascii(handle, index, buffer, 256);
        return n > 0 ? new string((sbyte*)buffer, 0, n) : null;
    }

    /// <summary>Walks libusb's parsed configuration descriptor structures.</summary>
    private static unsafe List<UsbInterfaceInfo> ReadInterfaces(nint dev)
    {
        var interfaces = new List<UsbInterfaceInfo>();
        nint config;
        if (Native.libusb_get_active_config_descriptor(dev, &config) != 0 &&
            Native.libusb_get_config_descriptor(dev, 0, &config) != 0)
            return interfaces;
        try
        {
            // struct libusb_config_descriptor: 9 bytes of fields, then 'const struct libusb_interface *interface' (pointer aligned).
            byte numInterfaces = Marshal.ReadByte(config, 4);
            int ptr = IntPtr.Size;
            nint interfaceArray = Marshal.ReadIntPtr(config, Align(9, ptr));
            int interfaceStructSize = Align(ptr + 4, ptr); // { altsetting*, int num_altsetting }
            for (int i = 0; i < numInterfaces; i++)
            {
                nint iface = interfaceArray + i * interfaceStructSize;
                nint alt = Marshal.ReadIntPtr(iface, 0);
                int numAlt = Marshal.ReadInt32(iface, ptr);
                if (numAlt < 1 || alt == 0) continue;
                // struct libusb_interface_descriptor: 9 bytes, then endpoint pointer (aligned).
                byte number = Marshal.ReadByte(alt, 2);
                byte altSetting = Marshal.ReadByte(alt, 3);
                byte numEndpoints = Marshal.ReadByte(alt, 4);
                byte cls = Marshal.ReadByte(alt, 5);
                byte sub = Marshal.ReadByte(alt, 6);
                byte proto = Marshal.ReadByte(alt, 7);
                nint endpointArray = Marshal.ReadIntPtr(alt, Align(9, ptr));
                // struct libusb_endpoint_descriptor: 9 bytes (with 2-byte wMaxPacketSize at offset 4), then extra pointer, int.
                int endpointStructSize = Align(Align(9, ptr) + ptr + 4, ptr);
                var endpoints = new List<UsbEndpointInfo>();
                for (int e = 0; e < numEndpoints; e++)
                {
                    nint ep = endpointArray + e * endpointStructSize;
                    endpoints.Add(new UsbEndpointInfo
                    {
                        Address = Marshal.ReadByte(ep, 2),
                        Type = (UsbTransferType)(Marshal.ReadByte(ep, 3) & 3),
                        MaxPacketSize = (ushort)(Marshal.ReadInt16(ep, 4) & 0x7FF),
                        Interval = Marshal.ReadByte(ep, 6),
                    });
                }
                interfaces.Add(new UsbInterfaceInfo
                {
                    Number = number, AlternateSetting = altSetting, Class = cls, SubClass = sub, Protocol = proto, Endpoints = endpoints,
                });
            }
        }
        finally
        {
            Native.libusb_free_config_descriptor(config);
        }
        return interfaces;
    }

    private static int Align(int offset, int alignment) => (offset + alignment - 1) / alignment * alignment;

    /// <summary>Translates a libusb error code of a transfer.</summary>
    private static Exception Error(int rc, string operation) => rc switch
    {
        Native.LIBUSB_ERROR_TIMEOUT => UsbErrors.TimedOut(operation),
        Native.LIBUSB_ERROR_NO_DEVICE => UsbErrors.Disconnected(operation, rc),
        Native.LIBUSB_ERROR_PIPE => UsbErrors.Stalled(operation, rc),
        Native.LIBUSB_ERROR_ACCESS => UsbErrors.AccessDenied($"Access denied during USB {operation}.", rc),
        _ => UsbErrors.Failed(operation, $"libusb error {rc}", rc),
    };

    /// <summary>Translates a libusb error code of opening a device or claiming an interface.</summary>
    private static UsbException OpenError(int rc, string what) => UsbErrors.Create(rc switch
    {
        Native.LIBUSB_ERROR_ACCESS => UsbError.AccessDenied,
        Native.LIBUSB_ERROR_BUSY => UsbError.Busy,
        // NOT_SUPPORTED: no driver libusb can use (on Windows, not bound to WinUSB or libusbK).
        Native.LIBUSB_ERROR_NO_DEVICE or Native.LIBUSB_ERROR_NOT_FOUND or Native.LIBUSB_ERROR_NOT_SUPPORTED => UsbError.NotFound,
        _ => UsbError.IOError,
    }, $"{what} failed (libusb error {rc}).", rc);

    /// <inheritdoc/>
    public unsafe IUsbDeviceHandle Open(UsbDeviceInfo device, int interfaceNumber)
    {
        if (device.BackendData is not (byte bus, byte address))
            throw UsbErrors.ForeignDevice("libusb");
        var info = device.Interfaces.FirstOrDefault(x => x.Number == interfaceNumber);
        if (info is null)
        {
            if (device.Interfaces.Count > 0)
                throw UsbErrors.NotFound($"Interface {interfaceNumber} of {device} not found.");
            // libusb could not read the configuration during enumeration: the interface is known only by its number.
            info = new UsbInterfaceInfo { Number = (byte)interfaceNumber, Class = 0xFF, SubClass = 0, Protocol = 0 };
        }
        nint context = Context;
        nint* list;
        nint count = Native.libusb_get_device_list(context, &list);
        try
        {
            for (int i = 0; i < count; i++)
            {
                nint dev = list[i];
                if (Native.libusb_get_bus_number(dev) != bus || Native.libusb_get_device_address(dev) != address) continue;
                nint handle;
                int rc = Native.libusb_open(dev, &handle);
                if (rc != 0) throw OpenError(rc, "libusb_open");
                Native.libusb_set_auto_detach_kernel_driver(handle, 1);
                rc = Native.libusb_claim_interface(handle, interfaceNumber);
                if (rc != 0)
                {
                    Native.libusb_close(handle);
                    throw OpenError(rc, $"Claiming USB interface {interfaceNumber}");
                }
                var result = new Handle(this, context, handle, device, info);
                lock (_sync)
                {
                    if (!_disposed)
                    {
                        _handles.Add(result);
                        return result;
                    }
                }
                result.Dispose();
                throw new ObjectDisposedException(GetType().FullName);
            }
        }
        finally
        {
            Native.libusb_free_device_list(list, 1);
        }
        throw UsbErrors.NotFound($"{device} is no longer attached.");
    }

    /// <summary>Closes the handles opened through this backend, stops its event thread and releases libusb.</summary>
    public void Dispose()
    {
        Handle[] handles;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            handles = [.. _handles];
        }
        // Closing a handle waits for its cancelled transfers, which needs the event thread: close them first.
        foreach (var handle in handles) handle.Dispose();
        EventLoop? events;
        nint context;
        lock (_sync)
        {
            events = _events;
            _events = null;
            context = _context;
            _context = 0;
        }
        events?.Stop();
        if (context != 0) Native.libusb_exit(context);
    }

    /// <summary>Starts the event thread of <paramref name="context"/> if it is not running.</summary>
    private void EnsureEventLoop(nint context)
    {
        lock (_sync)
        {
            if (_events is not null || context != _context) return;
            _events = new EventLoop(context);
        }
    }

    private bool IsEventThread
    {
        get
        {
            lock (_sync) return _events?.IsCurrentThread == true;
        }
    }

    /// <summary>Makes the event thread restart its wait, so that it sees the timeout of a newly submitted transfer.</summary>
    private void WakeForTimeout(nint context)
    {
        if (_wakeForTimeouts) EventLoop.Interrupt(context);
    }

    /// <summary>
    /// The thread that runs libusb's event handling for one context: it reaps completed transfers, handles their
    /// timeouts and calls their completion callbacks.
    /// </summary>
    private sealed class EventLoop
    {
        private readonly nint _context;
        private readonly Thread _thread;
        private volatile bool _stop;

        public EventLoop(nint context)
        {
            _context = context;
            _thread = new Thread(Run) { IsBackground = true, Name = "VisaCs libusb events" };
            _thread.Start();
        }

        public bool IsCurrentThread => Thread.CurrentThread == _thread;

        private unsafe void Run()
        {
            // struct timeval of one second. tv_sec is a C long (4 or 8 bytes) followed by tv_usec, so a zeroed
            // little-endian buffer with 1 in its first word reads as { 1, 0 } in every layout.
            long* timeout = stackalloc long[2];
            timeout[0] = 1;
            timeout[1] = 0;
            while (!_stop)
            {
                int rc = Native.libusb_handle_events_timeout_completed(_context, timeout, null);
                if (rc < 0 && rc != Native.LIBUSB_ERROR_INTERRUPTED && !_stop) Thread.Sleep(10); // do not spin on a persistent error
            }
        }

        public void Stop()
        {
            _stop = true;
            Interrupt(_context);
            // Without libusb_interrupt_event_handler (libusb < 1.0.21) the thread notices within the one-second wait.
            if (!IsCurrentThread) _thread.Join();
        }

        public static void Interrupt(nint context)
        {
            try
            {
                Native.libusb_interrupt_event_handler(context);
            }
            catch (EntryPointNotFoundException)
            {
                // libusb older than 1.0.21: the event thread wakes up at least once a second anyway.
            }
        }
    }
}

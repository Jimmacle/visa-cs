namespace VisaCs.Usb;

/// <summary>
/// An attached USB device, as reported by <see cref="IUsbBackend.EnumerateDevices"/> or by
/// <see cref="IUsbDeviceHandle.Device"/>. How much a backend can fill in before opening a device is described on
/// <see cref="IUsbBackend.EnumerateDevices"/>.
/// </summary>
public sealed class UsbDeviceInfo
{
    /// <summary>USB vendor ID (idVendor).</summary>
    public required ushort VendorId { get; init; }
    /// <summary>USB product ID (idProduct).</summary>
    public required ushort ProductId { get; init; }
    /// <summary>Serial number string; empty if the device has none or the backend could not read it.</summary>
    public string SerialNumber { get; init; } = "";
    /// <summary>Manufacturer string; null if the device has none or the backend could not read it.</summary>
    public string? Manufacturer { get; init; }
    /// <summary>Product string; null if the device has none or the backend could not read it.</summary>
    public string? Product { get; init; }
    /// <summary>Device release number (bcdDevice, binary-coded decimal: 0x0100 is 1.00).</summary>
    public ushort DeviceRelease { get; init; }
    /// <summary>Device class from the device descriptor (0 when each interface declares its own class).</summary>
    public byte DeviceClass { get; init; }
    /// <summary>The interfaces that the backend can open (alternate setting 0), in interface number order.</summary>
    public IReadOnlyList<UsbInterfaceInfo> Interfaces { get; init; } = Array.Empty<UsbInterfaceInfo>();

    /// <summary>
    /// Where the device is attached, in the backend's terms: the Windows device instance ID (WinUSB), the device
    /// node such as <c>/dev/bus/usb/001/004</c> (usbfs), or "bus 1 address 4" (libusb). Valid while the device stays
    /// attached.
    /// </summary>
    public string Location { get; init; } = "";

    /// <summary>
    /// Data that the enumerating backend needs to open the device. Backends set it in
    /// <see cref="IUsbBackend.EnumerateDevices"/> and read it back in <see cref="IUsbBackend.Open"/>; pass the object
    /// along unchanged.
    /// </summary>
    public object? BackendData { get; init; }

    /// <summary>True if any interface is a USBTMC interface (see <see cref="UsbInterfaceInfo.IsUsbTmc"/>).</summary>
    public bool IsUsbTmc => Interfaces.Any(i => i.IsUsbTmc);

    /// <inheritdoc/>
    public override string ToString() => $"USB {VendorId:X4}:{ProductId:X4} {SerialNumber} {Manufacturer} {Product}".TrimEnd();
}

/// <summary>One interface of a USB device (alternate setting 0).</summary>
public sealed class UsbInterfaceInfo
{
    /// <summary>Interface number (bInterfaceNumber).</summary>
    public required byte Number { get; init; }
    /// <summary>Alternate setting (bAlternateSetting).</summary>
    public byte AlternateSetting { get; init; }
    /// <summary>Interface class (bInterfaceClass): 0xFE is application specific, 0xFF vendor specific.</summary>
    public required byte Class { get; init; }
    /// <summary>Interface subclass (bInterfaceSubClass): 0x03 for USBTMC.</summary>
    public required byte SubClass { get; init; }
    /// <summary>Interface protocol (bInterfaceProtocol): 1 for USB488.</summary>
    public required byte Protocol { get; init; }
    /// <summary>
    /// The interface's endpoints, without the default control endpoint. Empty when the backend could not read them
    /// (WinUSB, for a device that another program has open).
    /// </summary>
    public IReadOnlyList<UsbEndpointInfo> Endpoints { get; init; } = Array.Empty<UsbEndpointInfo>();

    /// <summary>True for a USB Test &amp; Measurement Class interface (class 0xFE, subclass 0x03).</summary>
    public bool IsUsbTmc => Class == 0xFE && SubClass == 0x03;

    /// <summary>Returns the first endpoint of a transfer type and direction, or null if the interface has none.</summary>
    public UsbEndpointInfo? FindEndpoint(UsbTransferType type, bool input) =>
        Endpoints.FirstOrDefault(e => e.Type == type && e.IsInput == input);
}

/// <summary>One endpoint of a USB interface.</summary>
public sealed class UsbEndpointInfo
{
    /// <summary>Endpoint address (bEndpointAddress): the endpoint number, with bit 7 set for IN endpoints.</summary>
    public required byte Address { get; init; }
    /// <summary>Transfer type.</summary>
    public required UsbTransferType Type { get; init; }
    /// <summary>Maximum packet size in bytes (wMaxPacketSize without the high-bandwidth multiplier bits).</summary>
    public required ushort MaxPacketSize { get; init; }
    /// <summary>Polling interval of an interrupt endpoint (bInterval, in frames or encoded microframes).</summary>
    public byte Interval { get; init; }
    /// <summary>True for IN (device to host) endpoints.</summary>
    public bool IsInput => (Address & 0x80) != 0;
    /// <inheritdoc/>
    public override string ToString() => $"EP 0x{Address:X2} {Type} {(IsInput ? "IN" : "OUT")} {MaxPacketSize}";
}

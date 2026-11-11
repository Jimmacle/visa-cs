using System.Globalization;
using System.Runtime.Versioning;

namespace VisaCs.Usb;

/// <summary>
/// USB access on Linux through usbfs (<c>/dev/bus/usb</c>) and sysfs. The process needs read/write permission on
/// the device node (typically granted by a udev rule).
/// </summary>
/// <remarks>
/// <para>
/// Enumeration reads sysfs only, so it lists devices whose node the process cannot open, and does no I/O to them.
/// </para>
/// <para>
/// <see cref="Open"/> claims the interface. If a kernel driver (usually usbtmc) is bound to it, the driver is detached
/// while the handle is open and reattached when the handle is disposed. An interface that another program has
/// claimed through usbfs (or libusb) is never taken over: Open then fails with
/// <see cref="UsbException"/> with <see cref="UsbError.Busy"/>.
/// </para>
/// <para>
/// All transfers, synchronous ones included, are submitted as USB request blocks (URBs). The library's shared poll
/// thread waits for completed URBs and a thread-pool thread reaps them, so an asynchronous transfer holds no thread
/// while it is pending. This backend holds no resources of its own: disposing it does nothing.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class UsbFsBackend : IUsbBackend
{
    private const string SysfsDevices = "/sys/bus/usb/devices";
    private const string DeviceNodes = "/dev/bus/usb/";

    /// <inheritdoc/>
    public string Name => "usbfs";

    /// <summary>
    /// True on Linux when sysfs (<c>/sys/bus/usb/devices</c>) and usbfs (<c>/dev/bus/usb</c>) are present. Access
    /// rights to the individual devices are not checked.
    /// </summary>
    public static bool IsAvailable => OperatingSystem.IsLinux() && Directory.Exists(SysfsDevices) && Directory.Exists(DeviceNodes);

    /// <inheritdoc/>
    public IReadOnlyList<UsbDeviceInfo> EnumerateDevices()
    {
        var result = new List<UsbDeviceInfo>();
        if (!Directory.Exists(SysfsDevices)) return result;
        foreach (var dir in Directory.EnumerateDirectories(SysfsDevices))
        {
            string name = Path.GetFileName(dir);
            if (name.Contains(':')) continue;
            string? vid = ReadText(dir, "idVendor"), pid = ReadText(dir, "idProduct");
            if (vid is null || pid is null) continue;
            try
            {
                int bus = int.Parse(ReadText(dir, "busnum") ?? "0", CultureInfo.InvariantCulture);
                int address = int.Parse(ReadText(dir, "devnum") ?? "0", CultureInfo.InvariantCulture);
                var interfaces = new List<UsbInterfaceInfo>();
                foreach (var intfDir in Directory.EnumerateDirectories(dir, name + ":*"))
                {
                    if (Hex(ReadText(intfDir, "bAlternateSetting")) != 0) continue;
                    var endpoints = new List<UsbEndpointInfo>();
                    foreach (var epDir in Directory.EnumerateDirectories(intfDir, "ep_*"))
                    {
                        endpoints.Add(new UsbEndpointInfo
                        {
                            Address = (byte)Hex(ReadText(epDir, "bEndpointAddress")),
                            Type = (UsbTransferType)(Hex(ReadText(epDir, "bmAttributes")) & 3),
                            MaxPacketSize = (ushort)(Hex(ReadText(epDir, "wMaxPacketSize")) & 0x7FF),
                            Interval = (byte)Hex(ReadText(epDir, "bInterval")),
                        });
                    }
                    interfaces.Add(new UsbInterfaceInfo
                    {
                        Number = (byte)Hex(ReadText(intfDir, "bInterfaceNumber")),
                        Class = (byte)Hex(ReadText(intfDir, "bInterfaceClass")),
                        SubClass = (byte)Hex(ReadText(intfDir, "bInterfaceSubClass")),
                        Protocol = (byte)Hex(ReadText(intfDir, "bInterfaceProtocol")),
                        Endpoints = endpoints,
                    });
                }
                result.Add(new UsbDeviceInfo
                {
                    VendorId = (ushort)Hex(vid),
                    ProductId = (ushort)Hex(pid),
                    SerialNumber = ReadText(dir, "serial") ?? "",
                    Manufacturer = ReadText(dir, "manufacturer"),
                    Product = ReadText(dir, "product"),
                    DeviceRelease = (ushort)Hex(ReadText(dir, "bcdDevice")),
                    DeviceClass = (byte)Hex(ReadText(dir, "bDeviceClass")),
                    Interfaces = interfaces.OrderBy(i => i.Number).ToList(),
                    Location = $"{DeviceNodes}{bus:D3}/{address:D3}",
                });
            }
            catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
            {
                // Device vanished or unreadable: skip it.
            }
        }
        return result;
    }

    private static string? ReadText(string dir, string file)
    {
        try
        {
            string path = Path.Combine(dir, file);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int Hex(string? s) =>
        string.IsNullOrEmpty(s) ? 0 : int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public IUsbDeviceHandle Open(UsbDeviceInfo device, int interfaceNumber)
    {
        if (!device.Location.StartsWith(DeviceNodes, StringComparison.Ordinal))
            throw UsbErrors.ForeignDevice("usbfs");
        return new UsbFsDeviceHandle(device, interfaceNumber);
    }

    /// <summary>Does nothing: this backend holds no resources of its own.</summary>
    public void Dispose()
    {
    }
}

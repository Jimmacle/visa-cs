using System.Globalization;
using System.Runtime.Versioning;
using VisaCs.Pci;

namespace VisaCs;

/// <summary>Kind of a PCI base address register (values match VI_PXI_ADDR_NONE / _MEM / _IO).</summary>
public enum PciBarType
{
    /// <summary>The BAR is not implemented (or is the upper half of a 64-bit BAR).</summary>
    None = 0,
    /// <summary>Memory-mapped BAR (VI_PXI_ADDR_MEM).</summary>
    Memory = 1,
    /// <summary>I/O port BAR (VI_PXI_ADDR_IO).</summary>
    IO = 2,
}

/// <summary>A PCI base address register, as the kernel assigned it.</summary>
/// <param name="Index">The BAR number, 0 to 5.</param>
/// <param name="Type">Memory, I/O, or none.</param>
/// <param name="Address">The bus address the BAR is mapped at.</param>
/// <param name="Size">Size in bytes; 0 when not implemented.</param>
/// <param name="Prefetchable">True for prefetchable memory.</param>
/// <param name="Is64Bit">True for a 64-bit memory BAR (the next BAR is its upper half and reports <see cref="PciBarType.None"/>).</param>
public readonly record struct PciBar(int Index, PciBarType Type, ulong Address, ulong Size, bool Prefetchable, bool Is64Bit);

/// <summary>A PCI(e) function as seen in Linux sysfs (/sys/bus/pci/devices/DDDD:BB:DD.F).</summary>
public sealed class PciDeviceInfo
{
    /// <summary>The sysfs directory of the function.</summary>
    public required string Path { get; init; }
    /// <summary>PCI domain (segment).</summary>
    public required int Domain { get; init; }
    /// <summary>PCI bus number.</summary>
    public required int Bus { get; init; }
    /// <summary>PCI device number.</summary>
    public required int Device { get; init; }
    /// <summary>PCI function number.</summary>
    public required int Function { get; init; }
    /// <summary>PCI vendor ID.</summary>
    public ushort VendorId { get; init; }
    /// <summary>PCI device ID.</summary>
    public ushort DeviceId { get; init; }
    /// <summary>PCI subsystem vendor ID.</summary>
    public ushort SubsystemVendorId { get; init; }
    /// <summary>PCI subsystem ID.</summary>
    public ushort SubsystemId { get; init; }
    /// <summary>PCI class code (base class, subclass, programming interface).</summary>
    public uint ClassCode { get; init; }

    /// <summary>The bound kernel driver, or null when no driver is bound.</summary>
    public string? Driver { get; init; }

    /// <summary>The six base address registers.</summary>
    public IReadOnlyList<PciBar> Bars { get; init; } = Array.Empty<PciBar>();
    /// <summary>True for PCI Express functions (sysfs reports a link width).</summary>
    public bool IsExpress { get; init; }
    /// <summary>Negotiated PCIe link width, if known.</summary>
    public int? CurrentLinkWidth { get; init; }
    /// <summary>Maximum PCIe link width, if known.</summary>
    public int? MaxLinkWidth { get; init; }

    /// <summary>The Linux PCI address, e.g. <c>0000:03:12.0</c>.</summary>
    public string Address => $"{Domain:x4}:{Bus:x2}:{Device:x2}.{Function:x1}";

    /// <summary>
    /// True if user space may own the function: no driver is bound, or a driver meant for user-space access
    /// (uio_pci_generic, vfio-pci, pci-stub). Only such functions can be opened as a VISA PxiSession.
    /// </summary>
    public bool IsAvailableToUserSpace => Driver is null or "uio_pci_generic" or "vfio-pci" or "pci-stub";

    /// <summary>
    /// Reads one function by its Linux PCI address (e.g. <c>0000:03:12.0</c>) from a sysfs devices directory; null when
    /// it does not exist.
    /// </summary>
    public static PciDeviceInfo? FromAddress(string address, string root = "/sys/bus/pci/devices")
    {
        string path = System.IO.Path.Combine(root, address);
        return Directory.Exists(path) ? Read(path) : null;
    }

    /// <summary>
    /// Opens the configuration space (the sysfs <c>config</c> file). It is opened for writing only at the first write,
    /// so reads need no privileges; beyond the first 64 bytes they need root, as writes do.
    /// </summary>
    /// <exception cref="VisaException"><see cref="VisaStatus.ErrorNoPermission"/> or <see cref="VisaStatus.ErrorResourceNotFound"/>.</exception>
    [SupportedOSPlatform("linux")]
    public PciRegion OpenConfigurationSpace() => new PciFileRegion(System.IO.Path.Combine(Path, "config"), 0, isConfig: true);

    /// <summary>
    /// Opens a BAR (the sysfs <c>resourceN</c> file): memory BARs are mapped into the process, I/O BARs are accessed
    /// with file reads and writes. Needs root or a udev rule granting access to the file. This does not check
    /// <see cref="IsAvailableToUserSpace"/>: writing to a function a kernel driver owns can disturb that driver.
    /// </summary>
    /// <param name="index">The BAR number, 0 to 5.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not 0 to 5.</exception>
    /// <exception cref="VisaException">
    /// <see cref="VisaStatus.ErrorInvalidSpace"/> when the BAR is not implemented, <see cref="VisaStatus.ErrorNoPermission"/>,
    /// <see cref="VisaStatus.ErrorResourceNotFound"/> or <see cref="VisaStatus.ErrorResourceBusy"/> when it cannot be opened.
    /// </exception>
    [SupportedOSPlatform("linux")]
    public PciRegion OpenBar(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, 5);
        var bar = index < Bars.Count ? Bars[index] : new PciBar(index, PciBarType.None, 0, 0, false, false);
        string file = System.IO.Path.Combine(Path, $"resource{index}");
        return bar.Type switch
        {
            PciBarType.Memory => new PciMappedRegion(file, (long)bar.Size),
            PciBarType.IO => new PciFileRegion(file, (long)bar.Size, isConfig: false),
            _ => throw PciAccess.BarNotImplemented(index),
        };
    }

    internal static PciDeviceInfo? Read(string directory)
    {
        string name = System.IO.Path.GetFileName(directory);
        // DDDD:BB:DD.F
        var colon = name.Split(':');
        if (colon.Length != 3) return null;
        var df = colon[2].Split('.');
        if (df.Length != 2) return null;
        if (!int.TryParse(colon[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int domain) ||
            !int.TryParse(colon[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int bus) ||
            !int.TryParse(df[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int device) ||
            !int.TryParse(df[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int function))
            return null;

        string? driver = null;
        try
        {
            var link = new DirectoryInfo(System.IO.Path.Combine(directory, "driver")).ResolveLinkTarget(true);
            driver = link?.Name;
        }
        catch (IOException)
        {
        }

        return new PciDeviceInfo
        {
            Path = directory,
            Domain = domain,
            Bus = bus,
            Device = device,
            Function = function,
            VendorId = (ushort)Hex(directory, "vendor"),
            DeviceId = (ushort)Hex(directory, "device"),
            SubsystemVendorId = (ushort)Hex(directory, "subsystem_vendor"),
            SubsystemId = (ushort)Hex(directory, "subsystem_device"),
            ClassCode = (uint)Hex(directory, "class"),
            Driver = driver,
            Bars = ReadBars(directory),
            IsExpress = File.Exists(System.IO.Path.Combine(directory, "max_link_width")),
            CurrentLinkWidth = Decimal(directory, "current_link_width"),
            MaxLinkWidth = Decimal(directory, "max_link_width"),
        };
    }

    private static string? Text(string directory, string file)
    {
        try
        {
            string path = System.IO.Path.Combine(directory, file);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long Hex(string directory, string file)
    {
        string? text = Text(directory, file);
        if (text is null) return 0;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long v) ? v : 0;
    }

    private static int? Decimal(string directory, string file) =>
        int.TryParse(Text(directory, file), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;

    private static List<PciBar> ReadBars(string directory)
    {
        var bars = new List<PciBar>();
        var lines = Text(directory, "resource")?.Split('\n') ?? [];
        for (int index = 0; index < 6; index++)
        {
            if (index >= lines.Length)
            {
                bars.Add(new PciBar(index, PciBarType.None, 0, 0, false, false));
                continue;
            }
            var fields = lines[index].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            ulong start = fields.Length > 0 ? ParseHex(fields[0]) : 0;
            ulong end = fields.Length > 1 ? ParseHex(fields[1]) : 0;
            ulong flags = fields.Length > 2 ? ParseHex(fields[2]) : 0;
            // IORESOURCE_IO 0x100, IORESOURCE_MEM 0x200, IORESOURCE_PREFETCH 0x2000, IORESOURCE_MEM_64 0x100000
            var type = (flags & 0x100) != 0 ? PciBarType.IO : (flags & 0x200) != 0 ? PciBarType.Memory : PciBarType.None;
            ulong size = type == PciBarType.None || end < start ? 0 : end - start + 1;
            bars.Add(new PciBar(index, size == 0 ? PciBarType.None : type, start, size, (flags & 0x2000) != 0, (flags & 0x100000) != 0));
        }
        return bars;
    }

    private static ulong ParseHex(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong v) ? v : 0;
    }

    /// <summary>Lists the PCI functions under a sysfs devices directory, in address order.</summary>
    public static IReadOnlyList<PciDeviceInfo> Enumerate(string root = "/sys/bus/pci/devices")
    {
        var result = new List<PciDeviceInfo>();
        if (!Directory.Exists(root)) return result;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var info = Read(directory);
            if (info is not null) result.Add(info);
        }
        return result.OrderBy(d => d.Domain).ThenBy(d => d.Bus).ThenBy(d => d.Device).ThenBy(d => d.Function).ToList();
    }
}

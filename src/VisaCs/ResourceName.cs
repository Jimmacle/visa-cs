using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace VisaCs;

/// <summary>
/// A parsed VISA resource name. Use <see cref="Parse"/> or <see cref="ResourceManager.Parse(string)"/>.
/// <see cref="Canonical"/> is the expanded, unaliased form (e.g. <c>TCPIP0::192.168.1.5::inst0::INSTR</c>).
/// Two names are equal when their canonical forms are equal, ignoring case, however they were written.
/// </summary>
public abstract partial record ResourceName
{
    /// <summary>The hardware interface (VI_ATTR_INTF_TYPE).</summary>
    public required HardwareInterfaceType InterfaceType { get; init; }

    /// <summary>Board (interface) number; 0 when omitted.</summary>
    public required int Board { get; init; }

    /// <summary>The resource class (VI_ATTR_RSRC_CLASS).</summary>
    public required ResourceClass ResourceClass { get; init; }

    /// <summary>The string that was parsed (the alias itself for an alias); not part of equality.</summary>
    public string OriginalName { get; init; } = "";

    /// <summary>The alias used to open this resource, if any; not part of equality.</summary>
    public string? Alias { get; init; }

    /// <summary>Expanded unaliased resource name (VI_ATTR_RSRC_NAME).</summary>
    public abstract string Canonical { get; }

    /// <summary>Returns <see cref="Canonical"/> (sealed so derived records do not print their members).</summary>
    public sealed override string ToString() => Canonical;

    /// <summary>True if <paramref name="other"/> names the same resource: equal <see cref="Canonical"/> forms, ignoring case.</summary>
    public virtual bool Equals(ResourceName? other) =>
        other is not null && string.Equals(Canonical, other.Canonical, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Canonical);

    /// <summary>The interface prefix with board number, e.g. <c>TCPIP0</c>.</summary>
    public string InterfaceName => InterfaceType switch
    {
        HardwareInterfaceType.Tcpip => $"TCPIP{Board}",
        HardwareInterfaceType.Usb => $"USB{Board}",
        HardwareInterfaceType.Gpib => $"GPIB{Board}",
        HardwareInterfaceType.Serial => $"ASRL{Board}",
        HardwareInterfaceType.Vicp => $"VICP{Board}",
        HardwareInterfaceType.Vxi => $"VXI{Board}",
        HardwareInterfaceType.GpibVxi => $"GPIB-VXI{Board}",
        HardwareInterfaceType.Pxi => $"PXI{Board}",
        _ => $"{InterfaceType}{Board}",
    };

    [GeneratedRegex(@"^\s*(?<intf>TCPIP|USB|ASRL|GPIB-VXI|GPIB|VICP|VXI|PXI)(?<board>.*?)(?:::(?<rest>.*?))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Prefix { get; }

    /// <summary>Parses a resource name; throws <see cref="VisaException"/> (VI_ERROR_INV_RSRC_NAME) if it is invalid.</summary>
    public static ResourceName Parse(string resourceName)
    {
        if (!TryParse(resourceName, out var result, out var error))
            throw new VisaException(VisaStatus.ErrorInvalidResourceName, $"'{resourceName}': {error}");
        return result;
    }

    /// <summary>Parses a resource name; returns false if it is invalid.</summary>
    public static bool TryParse(string resourceName, [NotNullWhen(true)] out ResourceName? result) =>
        TryParse(resourceName, out result, out _);

    /// <summary>Parses a resource name; returns false if it is invalid.</summary>
    public static bool TryParse(string resourceName, [NotNullWhen(true)] out ResourceName? result, out string error)
    {
        result = null;
        error = "";
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            error = "empty resource name";
            return false;
        }

        var match = Prefix.Match(resourceName);
        if (!match.Success)
        {
            error = "unknown interface type";
            return false;
        }

        string intf = match.Groups["intf"].Value.ToUpperInvariant();
        string boardText = match.Groups["board"].Value;
        var parts = match.Groups["rest"].Success ? SplitParts(match.Groups["rest"].Value) : new List<string>();

        // The board of ASRL may be a device name (ASRL/dev/ttyUSB0, ASRLCOM3); all others are numeric.
        int board = 0;
        if (intf != "ASRL" && boardText.Length > 0 &&
            !int.TryParse(boardText, NumberStyles.None, CultureInfo.InvariantCulture, out board))
        {
            error = $"invalid board number '{boardText}'";
            return false;
        }

        try
        {
            result = intf switch
            {
                "TCPIP" => ParseTcpip(board, parts, resourceName),
                "USB" => ParseUsb(board, parts, resourceName),
                "ASRL" => ParseSerial(boardText, parts, resourceName),
                "GPIB" => ParseGpib(board, parts, resourceName),
                "VICP" => ParseVicp(board, parts, resourceName),
                "VXI" => ParseUnsupported(HardwareInterfaceType.Vxi, board, parts, resourceName),
                "GPIB-VXI" => ParseUnsupported(HardwareInterfaceType.GpibVxi, board, parts, resourceName),
                "PXI" => ParsePxi(board, parts, resourceName),
                _ => throw new FormatException("unknown interface type"),
            };
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Splits on "::" while keeping bracketed IPv6 addresses intact.</summary>
    internal static List<string> SplitParts(string rest)
    {
        var parts = new List<string>();
        int start = 0, depth = 0;
        for (int i = 0; i < rest.Length; i++)
        {
            char c = rest[i];
            if (c == '[') depth++;
            else if (c == ']') depth = Math.Max(0, depth - 1);
            else if (depth == 0 && c == ':' && i + 1 < rest.Length && rest[i + 1] == ':')
            {
                parts.Add(rest[start..i]);
                i++;
                start = i + 1;
            }
        }
        parts.Add(rest[start..]);
        return parts;
    }

    private static bool IsClass(string part, string cls) => part.Equals(cls, StringComparison.OrdinalIgnoreCase);

    private static string RequireHost(string host)
    {
        host = host.Trim();
        if (host.Length == 0) throw new FormatException("missing host address");
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        return host;
    }

    private static ResourceName ParseTcpip(int board, List<string> parts, string original)
    {
        if (parts.Count == 0 || parts[0].Length == 0) throw new FormatException("missing host address");

        if (IsClass(parts[^1], "SOCKET"))
        {
            if (parts.Count != 3) throw new FormatException("expected TCPIP[board]::host::port::SOCKET");
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
                throw new FormatException($"invalid port '{parts[1]}'");
            return new TcpipSocketResourceName
            {
                InterfaceType = HardwareInterfaceType.Tcpip, Board = board, ResourceClass = ResourceClass.Socket,
                OriginalName = original, Host = RequireHost(parts[0]), Port = port,
            };
        }

        var items = new List<string>(parts);
        if (IsClass(items[^1], "INSTR")) items.RemoveAt(items.Count - 1);
        if (items.Count is < 1 or > 2) throw new FormatException("expected TCPIP[board]::host[::device name][::INSTR]");
        string device = items.Count == 2 ? items[1].Trim() : "inst0";
        if (device.Length == 0) throw new FormatException("empty LAN device name");

        return new TcpipInstrumentResourceName
        {
            InterfaceType = HardwareInterfaceType.Tcpip, Board = board, ResourceClass = ResourceClass.Instrument,
            OriginalName = original, Host = RequireHost(items[0]), DeviceName = device,
        };
    }

    internal static bool TryParseNumber(string text, out int value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static ResourceName ParseUsb(int board, List<string> parts, string original)
    {
        var items = new List<string>(parts);
        var cls = ResourceClass.Instrument;
        if (items.Count > 0 && IsClass(items[^1], "INSTR")) items.RemoveAt(items.Count - 1);
        else if (items.Count > 0 && IsClass(items[^1], "RAW"))
        {
            cls = ResourceClass.Raw;
            items.RemoveAt(items.Count - 1);
        }
        if (items.Count is < 3 or > 4)
            throw new FormatException("expected USB[board]::manufacturer ID::model code::serial number[::interface number][::INSTR|RAW]");
        if (!TryParseNumber(items[0], out int vid) || vid is < 0 or > 0xFFFF) throw new FormatException($"invalid manufacturer ID '{items[0]}'");
        if (!TryParseNumber(items[1], out int pid) || pid is < 0 or > 0xFFFF) throw new FormatException($"invalid model code '{items[1]}'");
        int? intf = null;
        if (items.Count == 4)
        {
            if (!TryParseNumber(items[3], out int n) || n is < 0 or > 255) throw new FormatException($"invalid interface number '{items[3]}'");
            intf = n;
        }
        return new UsbResourceName
        {
            InterfaceType = HardwareInterfaceType.Usb, Board = board, ResourceClass = cls, OriginalName = original,
            VendorId = (ushort)vid, ProductId = (ushort)pid, SerialNumber = items[2].Trim(), InterfaceNumber = intf,
        };
    }

    private static ResourceName ParseSerial(string boardText, List<string> parts, string original)
    {
        if (parts.Count > 1 || (parts.Count == 1 && !IsClass(parts[0], "INSTR")))
            throw new FormatException("expected ASRL[board][::INSTR]");
        boardText = boardText.Trim();
        int board = 0;
        bool numeric = boardText.Length == 0 || int.TryParse(boardText, NumberStyles.None, CultureInfo.InvariantCulture, out board);
        string portName;
        if (numeric)
        {
            portName = OperatingSystem.IsWindows() ? $"COM{board}" : $"/dev/ttyS{Math.Max(0, board - 1)}";
        }
        else
        {
            portName = boardText;
            // ASRLCOM3 -> board 3. On Windows it is the same port as ASRL3, so it gets the same canonical name.
            if (boardText.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(boardText.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out board) &&
                OperatingSystem.IsWindows())
            {
                numeric = true;
                portName = $"COM{board}";
            }
        }
        return new SerialResourceName
        {
            InterfaceType = HardwareInterfaceType.Serial, Board = board, ResourceClass = ResourceClass.Instrument,
            OriginalName = original, PortName = portName, BoardText = numeric ? board.ToString(CultureInfo.InvariantCulture) : boardText,
        };
    }

    private static ResourceName ParseGpib(int board, List<string> parts, string original)
    {
        if (parts.Count == 1 && IsClass(parts[0], "INTFC"))
        {
            return new GpibInterfaceResourceName
            {
                InterfaceType = HardwareInterfaceType.Gpib, Board = board, ResourceClass = ResourceClass.Interface, OriginalName = original,
            };
        }
        if (parts.Count == 1 && IsClass(parts[0], "SERVANT"))
        {
            return new UnsupportedResourceName
            {
                InterfaceType = HardwareInterfaceType.Gpib, Board = board, ResourceClass = ResourceClass.Servant,
                OriginalName = original, Text = $"GPIB{board}::SERVANT",
            };
        }

        var items = new List<string>(parts);
        if (items.Count > 0 && IsClass(items[^1], "INSTR")) items.RemoveAt(items.Count - 1);
        if (items.Count is < 1 or > 2) throw new FormatException("expected GPIB[board]::primary address[::secondary address][::INSTR]");
        if (!int.TryParse(items[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int pad) || pad is < 0 or > 30)
            throw new FormatException($"invalid primary address '{items[0]}'");
        int? sad = null;
        if (items.Count == 2)
        {
            if (!int.TryParse(items[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int s) || s is < 0 or > 30)
                throw new FormatException($"invalid secondary address '{items[1]}'");
            sad = s;
        }
        return new GpibInstrumentResourceName
        {
            InterfaceType = HardwareInterfaceType.Gpib, Board = board, ResourceClass = ResourceClass.Instrument, OriginalName = original,
            PrimaryAddress = pad, SecondaryAddress = sad,
        };
    }

    private static ResourceName ParseVicp(int board, List<string> parts, string original)
    {
        var items = new List<string>(parts);
        if (items.Count > 0 && IsClass(items[^1], "INSTR")) items.RemoveAt(items.Count - 1);
        if (items.Count != 1) throw new FormatException("expected VICP[board]::host address[::INSTR]");
        return new VicpResourceName
        {
            InterfaceType = HardwareInterfaceType.Vicp, Board = board, ResourceClass = ResourceClass.Instrument,
            OriginalName = original, Host = RequireHost(items[0]),
        };
    }

    [GeneratedRegex(@"^(?:(?<domain>[0-9a-f]{1,4}):)?(?<bus>\d+)-(?<device>\d+)(?:\.(?<function>\d+))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PxiAddress { get; }

    [GeneratedRegex(@"^CHASSIS(?<n>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PxiChassis { get; }

    [GeneratedRegex(@"^SLOT(?<n>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PxiSlot { get; }

    [GeneratedRegex(@"^FUNC(?<n>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PxiFunction { get; }

    /// <summary>
    /// PXI[bus]::device[::function][::INSTR], PXI[interface]::[domain:]bus-device[.function][::INSTR],
    /// PXI[interface]::CHASSISn::SLOTm[::FUNCf][::INSTR] and PXI[interface]::MEMACC.
    /// </summary>
    private static ResourceName ParsePxi(int board, List<string> parts, string original)
    {
        var items = new List<string>(parts);
        if (items.Count == 1 && IsClass(items[0], "MEMACC")) return ParseUnsupported(HardwareInterfaceType.Pxi, board, parts, original);
        if (items.Count > 0 && IsClass(items[^1], "INSTR")) items.RemoveAt(items.Count - 1);
        if (items.Count == 0) throw new FormatException("expected PXI[interface]::bus-device[.function][::INSTR]");

        static int Number(string text, string what, int max)
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n > max)
                throw new FormatException($"invalid PXI {what} '{text}'");
            return n;
        }

        var address = PxiAddress.Match(items[0]);
        if (address.Success && items.Count == 1)
        {
            return new PxiResourceName
            {
                InterfaceType = HardwareInterfaceType.Pxi, Board = board, ResourceClass = ResourceClass.Instrument, OriginalName = original,
                Domain = address.Groups["domain"].Success ? int.Parse(address.Groups["domain"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : 0,
                Bus = Number(address.Groups["bus"].Value, "bus", 255),
                Device = Number(address.Groups["device"].Value, "device", 31),
                Function = address.Groups["function"].Success ? Number(address.Groups["function"].Value, "function", 7) : 0,
            };
        }
        var chassis = PxiChassis.Match(items[0]);
        if (chassis.Success)
        {
            if (items.Count is < 2 or > 3 || PxiSlot.Match(items[1]) is not { Success: true } slot)
                throw new FormatException("expected PXI[interface]::CHASSISn::SLOTm[::FUNCf][::INSTR]");
            int function = 0;
            if (items.Count == 3)
            {
                var f = PxiFunction.Match(items[2]);
                if (!f.Success) throw new FormatException($"invalid PXI function '{items[2]}'");
                function = Number(f.Groups["n"].Value, "function", 7);
            }
            return new PxiResourceName
            {
                InterfaceType = HardwareInterfaceType.Pxi, Board = board, ResourceClass = ResourceClass.Instrument, OriginalName = original,
                Chassis = Number(chassis.Groups["n"].Value, "chassis", 255), Slot = Number(slot.Groups["n"].Value, "slot", 255), Function = function,
            };
        }
        if (items.Count is 1 or 2)
        {
            // PXI[bus]::device[::function]
            return new PxiResourceName
            {
                InterfaceType = HardwareInterfaceType.Pxi, Board = 0, ResourceClass = ResourceClass.Instrument, OriginalName = original,
                Bus = board, Device = Number(items[0], "device", 31), Function = items.Count == 2 ? Number(items[1], "function", 7) : 0,
            };
        }
        throw new FormatException("expected PXI[interface]::bus-device[.function][::INSTR]");
    }

    private static ResourceName ParseUnsupported(HardwareInterfaceType type, int board, List<string> parts, string original)
    {
        var cls = ResourceClass.Instrument;
        if (parts.Count > 0 && ResourceClass.TryParseVisaString(parts[^1], out var c)) cls = c;
        return new UnsupportedResourceName
        {
            InterfaceType = type, Board = board, ResourceClass = cls, OriginalName = original,
            Text = original.Trim().ToUpperInvariant(),
        };
    }
}

/// <summary><c>TCPIP[board]::host[::LAN device name][::INSTR]</c> — VXI-11 or HiSLIP (device name <c>hislipN[,port]</c>).</summary>
public sealed record TcpipInstrumentResourceName : ResourceName
{
    /// <summary>True if <paramref name="other"/> names the same resource; see <see cref="ResourceName.Equals(ResourceName?)"/>.</summary>
    public bool Equals(TcpipInstrumentResourceName? other) => base.Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => base.GetHashCode();

    /// <summary>Host name or IP address (IPv6 without brackets).</summary>
    public required string Host { get; init; }

    /// <summary>LAN device name: <c>inst0</c>, <c>gpib0,5</c>, <c>hislip0</c>, <c>hislip0,4880</c>...</summary>
    public required string DeviceName { get; init; }

    /// <summary>True for HiSLIP (device name <c>hislipN[,port]</c>), false for VXI-11.</summary>
    public bool IsHislip => DeviceName.StartsWith("hislip", StringComparison.OrdinalIgnoreCase);

    /// <summary>HiSLIP sub-address sent in the Initialize message (device name without the port).</summary>
    public string HislipSubAddress
    {
        get
        {
            int comma = DeviceName.IndexOf(',');
            return comma < 0 ? DeviceName : DeviceName[..comma];
        }
    }

    /// <summary>Explicit HiSLIP port from <c>hislip0,PORT</c>, if present.</summary>
    public int? HislipPort
    {
        get
        {
            int comma = DeviceName.IndexOf(',');
            if (!IsHislip || comma < 0) return null;
            return int.TryParse(DeviceName.AsSpan(comma + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int p) ? p : null;
        }
    }

    /// <inheritdoc/>
    public override string Canonical => $"TCPIP{Board}::{Internal.NetUtil.FormatHost(Host)}::{DeviceName}::INSTR";
}

/// <summary><c>TCPIP[board]::host::port::SOCKET</c> — raw TCP socket.</summary>
public sealed record TcpipSocketResourceName : ResourceName
{
    /// <summary>True if <paramref name="other"/> names the same resource; see <see cref="ResourceName.Equals(ResourceName?)"/>.</summary>
    public bool Equals(TcpipSocketResourceName? other) => base.Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => base.GetHashCode();

    /// <summary>Host name or IP address (IPv6 without brackets).</summary>
    public required string Host { get; init; }
    /// <summary>TCP port.</summary>
    public required int Port { get; init; }
    /// <inheritdoc/>
    public override string Canonical => $"TCPIP{Board}::{Internal.NetUtil.FormatHost(Host)}::{Port}::SOCKET";
}

/// <summary><c>USB[board]::VID::PID::serial[::interface][::INSTR|RAW]</c>.</summary>
public sealed record UsbResourceName : ResourceName
{
    /// <summary>True if <paramref name="other"/> names the same resource; see <see cref="ResourceName.Equals(ResourceName?)"/>.</summary>
    public bool Equals(UsbResourceName? other) => base.Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => base.GetHashCode();

    /// <summary>USB vendor ID (manufacturer ID).</summary>
    public required ushort VendorId { get; init; }
    /// <summary>USB product ID (model code).</summary>
    public required ushort ProductId { get; init; }
    /// <summary>USB serial number string.</summary>
    public required string SerialNumber { get; init; }
    /// <summary>
    /// USB interface number; null selects the first suitable interface when the session opens (the canonical name then
    /// says 0 until the session replaces <see cref="VisaSession.Resource"/> with the interface it claimed).
    /// </summary>
    public int? InterfaceNumber { get; init; }

    /// <inheritdoc/>
    public override string Canonical =>
        $"USB{Board}::0x{VendorId:X4}::0x{ProductId:X4}::{SerialNumber}::{InterfaceNumber ?? 0}::{ResourceClass.ToVisaString()}";
}

/// <summary><c>ASRL[board][::INSTR]</c>. The board may also be a device path such as <c>ASRL/dev/ttyUSB0</c>.</summary>
public sealed record SerialResourceName : ResourceName
{
    /// <summary>True if <paramref name="other"/> names the same resource; see <see cref="ResourceName.Equals(ResourceName?)"/>.</summary>
    public bool Equals(SerialResourceName? other) => base.Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => base.GetHashCode();

    /// <summary>Operating system port name (<c>COM3</c>, <c>/dev/ttyUSB0</c>).</summary>
    public required string PortName { get; init; }

    /// <summary>The board part: a number, or a device name as written (<c>COMn</c> is turned into n on Windows).</summary>
    public required string BoardText { get; init; }

    /// <inheritdoc/>
    public override string Canonical => $"ASRL{BoardText}::INSTR";
}

/// <summary><c>GPIB[board]::primary[::secondary][::INSTR]</c>.</summary>
public sealed record GpibInstrumentResourceName : ResourceName
{
    /// <summary>True if <paramref name="other"/> names the same resource; see <see cref="ResourceName.Equals(ResourceName?)"/>.</summary>
    public bool Equals(GpibInstrumentResourceName? other) => base.Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => base.GetHashCode();

    /// <summary>Primary address (0-30).</summary>
    public required int PrimaryAddress { get; init; }
    /// <summary>Secondary address (0-30), if any.</summary>
    public int? SecondaryAddress { get; init; }

    /// <inheritdoc/>
    public override string Canonical => SecondaryAddress is { } sad
        ? $"GPIB{Board}::{PrimaryAddress}::{sad}::INSTR"
        : $"GPIB{Board}::{PrimaryAddress}::INSTR";
}

/// <summary><c>GPIB[board]::INTFC</c>.</summary>
public sealed record GpibInterfaceResourceName : ResourceName
{
    /// <summary>True if <paramref name="other"/> names the same resource; see <see cref="ResourceName.Equals(ResourceName?)"/>.</summary>
    public bool Equals(GpibInterfaceResourceName? other) => base.Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => base.GetHashCode();

    /// <inheritdoc/>
    public override string Canonical => $"GPIB{Board}::INTFC";
}

/// <summary><c>VICP[board]::host[::INSTR]</c> — Teledyne LeCroy oscilloscopes.</summary>
public sealed record VicpResourceName : ResourceName
{
    /// <summary>True if <paramref name="other"/> names the same resource; see <see cref="ResourceName.Equals(ResourceName?)"/>.</summary>
    public bool Equals(VicpResourceName? other) => base.Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => base.GetHashCode();

    /// <summary>Host name or IP address.</summary>
    public required string Host { get; init; }
    /// <inheritdoc/>
    public override string Canonical => $"VICP{Board}::{Internal.NetUtil.FormatHost(Host)}::INSTR";
}

/// <summary>
/// A PXI / PCI(e) instrument (register based): <c>PXI0::3-18.0::INSTR</c> (bus 3, device 18, function 0),
/// <c>PXI3::18::INSTR</c>, or <c>PXI0::CHASSIS1::SLOT4::INSTR</c>. A non-zero PCI domain may be written as a hex
/// prefix, e.g. <c>PXI0::1a2b:3-0.0::INSTR</c> (an extension of the VISA syntax).
/// </summary>
public sealed record PxiResourceName : ResourceName
{
    /// <summary>True if <paramref name="other"/> names the same resource; see <see cref="ResourceName.Equals(ResourceName?)"/>.</summary>
    public bool Equals(PxiResourceName? other) => base.Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => base.GetHashCode();

    /// <summary>PCI domain (segment); 0 on most systems.</summary>
    public int Domain { get; init; }
    /// <summary>PCI bus number.</summary>
    public int Bus { get; init; }
    /// <summary>PCI device number.</summary>
    public int Device { get; init; }
    /// <summary>PCI function number.</summary>
    public int Function { get; init; }

    /// <summary>Chassis number, when the resource was named by chassis and slot.</summary>
    public int? Chassis { get; init; }

    /// <summary>Slot number, when the resource was named by chassis and slot.</summary>
    public int? Slot { get; init; }

    /// <summary>The Linux PCI address, e.g. <c>0000:03:12.0</c> (only meaningful for bus/device names).</summary>
    public string PciAddress => $"{Domain:x4}:{Bus:x2}:{Device:x2}.{Function:x1}";

    /// <inheritdoc/>
    public override string Canonical => Chassis is { } c
        ? $"PXI{Board}::CHASSIS{c}::SLOT{Slot}::FUNC{Function}::INSTR"
        : Domain == 0
            ? $"PXI{Board}::{Bus}-{Device}.{Function}::INSTR"
            : $"PXI{Board}::{Domain:x4}:{Bus}-{Device}.{Function}::INSTR";
}

/// <summary>
/// A syntactically recognized resource this implementation cannot open: VXI and GPIB-VXI (they need vendor hardware
/// drivers), PXI MEMACC and GPIB SERVANT.
/// </summary>
public sealed record UnsupportedResourceName : ResourceName
{
    /// <summary>True if <paramref name="other"/> names the same resource; see <see cref="ResourceName.Equals(ResourceName?)"/>.</summary>
    public bool Equals(UnsupportedResourceName? other) => base.Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => base.GetHashCode();

    /// <summary>The resource name in upper case.</summary>
    public required string Text { get; init; }
    /// <inheritdoc/>
    public override string Canonical => Text;
}

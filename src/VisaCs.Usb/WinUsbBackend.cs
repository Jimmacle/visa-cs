using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace VisaCs.Usb;

/// <summary>
/// USB access through WinUSB, the generic driver built into Windows. Devices must be bound to WinUSB: devices with
/// Microsoft OS descriptors bind automatically; others can be bound with Zadig or an INF file.
/// </summary>
/// <remarks>
/// <para>
/// Enumeration finds the devices bound to WinUSB through SetupAPI and opens each one briefly to read its device,
/// configuration and string descriptors. A device that another program has open cannot be read: it is reported with
/// what Windows knows (its interfaces without endpoints, and the Windows device description as
/// <see cref="UsbDeviceInfo.Product"/>), or with the descriptors this backend read earlier. Of a composite device,
/// only the interfaces bound to WinUSB are listed.
/// </para>
/// <para>
/// Transfers are overlapped I/O. Asynchronous ones complete through the thread pool's I/O completion port, so no
/// thread waits while they are pending. This backend holds no resources of its own: disposing it does nothing.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WinUsbBackend : IUsbBackend
{
    /// <summary>Timeout of the descriptor requests made while enumerating and opening devices.</summary>
    private const int DescriptorTimeoutMs = 1000;

    /// <summary>Device information read from the devices, by location, for when a device cannot be opened (in use).</summary>
    private readonly ConcurrentDictionary<string, UsbDeviceInfo> _known = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public string Name => "WinUSB";

    /// <summary>True on Windows, which includes WinUSB.</summary>
    public static bool IsAvailable => OperatingSystem.IsWindows();

    /// <summary>The device path of an interface bound to WinUSB.</summary>
    private sealed record InterfacePath(string DevicePath, string InstanceId);

    /// <summary>
    /// What <see cref="Open"/> needs: the device paths of the interfaces bound to WinUSB, by interface number. A device
    /// bound to WinUSB as a whole (not composite) has one path, through which all its interfaces are reached.
    /// </summary>
    private sealed record DeviceData(IReadOnlyDictionary<int, InterfacePath> Interfaces, bool Composite);

    /// <inheritdoc/>
    public unsafe IReadOnlyList<UsbDeviceInfo> EnumerateDevices()
    {
        var result = new List<UsbDeviceInfo>();
        var groups = new Dictionary<string, (ushort Vid, ushort Pid, string Serial, List<(UsbInterfaceInfo Info, string Path, string InstanceId)> Interfaces,
            string? Description, bool Composite)>(StringComparer.OrdinalIgnoreCase);

        nint set = Native.SetupDiGetClassDevsW(null, "USB", 0, Native.DIGCF_PRESENT | Native.DIGCF_ALLCLASSES);
        if (set == -1) return result;
        try
        {
            var data = new Native.SP_DEVINFO_DATA { cbSize = (uint)sizeof(Native.SP_DEVINFO_DATA) };
            for (uint index = 0; Native.SetupDiEnumDeviceInfo(set, index, ref data); index++)
            {
                string? service = GetStringProperty(set, ref data, Native.SPDRP_SERVICE);
                if (!string.Equals(service, "WinUSB", StringComparison.OrdinalIgnoreCase)) continue;

                string instanceId = GetInstanceId(set, ref data);
                var hardwareIds = GetMultiStringProperty(set, ref data, Native.SPDRP_HARDWAREID);
                var compatibleIds = GetMultiStringProperty(set, ref data, Native.SPDRP_COMPATIBLEIDS);
                if (!TryParseVidPid(hardwareIds.Concat([instanceId]), out ushort vid, out ushort pid, out int? mi)) continue;

                var (cls, sub, proto) = ParseClass(compatibleIds);
                string? path = GetDevicePath(set, ref data, instanceId);
                if (path is null) continue;

                string serial = GetSerialNumber(instanceId, data.DevInst, mi.HasValue);
                string key = mi.HasValue ? ParentInstanceId(data.DevInst) ?? instanceId : instanceId;
                var info = new UsbInterfaceInfo { Number = (byte)(mi ?? 0), Class = cls, SubClass = sub, Protocol = proto };

                if (!groups.TryGetValue(key, out var group))
                {
                    group = (vid, pid, serial, new(), GetStringProperty(set, ref data, Native.SPDRP_DEVICEDESC), mi.HasValue);
                    groups[key] = group;
                }
                group.Interfaces.Add((info, path, instanceId));
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(set);
        }

        foreach (var (key, g) in groups)
        {
            var registry = new UsbDeviceInfo
            {
                VendorId = g.Vid,
                ProductId = g.Pid,
                SerialNumber = g.Serial,
                Product = g.Description,
                Interfaces = g.Interfaces.Select(i => i.Info).OrderBy(i => i.Number).ToList(),
                Location = key,
                BackendData = new DeviceData(g.Interfaces.ToDictionary(i => (int)i.Info.Number, i => new InterfacePath(i.Path, i.InstanceId)), g.Composite),
            };
            result.Add(ReadDevice(registry));
        }
        return result;
    }

    /// <summary>
    /// Completes what the registry tells about a device with its descriptors, read by opening it briefly. Falls back to
    /// the descriptors read earlier, or to the registry data, when the device cannot be opened.
    /// </summary>
    private UsbDeviceInfo ReadDevice(UsbDeviceInfo registry)
    {
        var data = (DeviceData)registry.BackendData!;
        try
        {
            using var handle = new WinUsbDeviceHandle(registry, data, registry.Interfaces[0].Number, DescriptorTimeoutMs);
            _known[registry.Location] = handle.Device;
            return handle.Device;
        }
        catch (Exception)
        {
            // In use by another program (or by a handle of this process), or not answering: enumeration goes on.
        }
        if (!_known.TryGetValue(registry.Location, out var known)) return registry;
        return new UsbDeviceInfo
        {
            VendorId = known.VendorId, ProductId = known.ProductId, SerialNumber = known.SerialNumber, Manufacturer = known.Manufacturer,
            Product = known.Product, DeviceRelease = known.DeviceRelease, DeviceClass = known.DeviceClass,
            Interfaces = known.Interfaces.Where(i => !data.Composite || data.Interfaces.ContainsKey(i.Number)).ToList(),
            Location = registry.Location, BackendData = data,
        };
    }

    /// <inheritdoc/>
    public IUsbDeviceHandle Open(UsbDeviceInfo device, int interfaceNumber)
    {
        if (device.BackendData is not DeviceData data)
            throw UsbErrors.ForeignDevice("WinUSB");
        var handle = new WinUsbDeviceHandle(device, data, interfaceNumber, DescriptorTimeoutMs);
        _known[device.Location] = handle.Device;
        return handle;
    }

    /// <summary>Does nothing: this backend holds no resources of its own.</summary>
    public void Dispose()
    {
    }

    [GeneratedRegex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(?:&REV_[0-9A-F]{4})?(?:&MI_([0-9A-F]{2}))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VidPidRegex { get; }

    private static bool TryParseVidPid(IEnumerable<string> ids, out ushort vid, out ushort pid, out int? mi)
    {
        foreach (var id in ids)
        {
            var m = VidPidRegex.Match(id);
            if (!m.Success) continue;
            vid = ushort.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            pid = ushort.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            mi = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : null;
            return true;
        }
        vid = pid = 0;
        mi = null;
        return false;
    }

    [GeneratedRegex(@"Class_([0-9A-F]{2})(?:&SubClass_([0-9A-F]{2}))?(?:&Prot_([0-9A-F]{2}))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClassRegex { get; }

    private static (byte Class, byte SubClass, byte Protocol) ParseClass(IEnumerable<string> compatibleIds)
    {
        foreach (var id in compatibleIds)
        {
            var m = ClassRegex.Match(id);
            if (!m.Success || !m.Groups[2].Success) continue;
            byte P(Group g) => g.Success ? byte.Parse(g.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : (byte)0;
            return (P(m.Groups[1]), P(m.Groups[2]), P(m.Groups[3]));
        }
        return (0xFF, 0, 0);
    }

    private static string GetSerialNumber(string instanceId, uint devInst, bool composite)
    {
        string id = instanceId;
        if (composite) id = ParentInstanceId(devInst) ?? instanceId;
        // USB\VID_xxxx&PID_xxxx\SERIAL ; generated IDs contain '&'
        int slash = id.LastIndexOf('\\');
        string last = slash >= 0 ? id[(slash + 1)..] : "";
        return last.Contains('&') ? "" : last;
    }

    private static unsafe string? ParentInstanceId(uint devInst)
    {
        if (Native.CM_Get_Parent(out uint parent, devInst, 0) != 0) return null;
        char* buffer = stackalloc char[512];
        return Native.CM_Get_Device_IDW(parent, buffer, 512, 0) == 0 ? new string(buffer) : null;
    }

    private static unsafe string GetInstanceId(nint set, ref Native.SP_DEVINFO_DATA data)
    {
        char* buffer = stackalloc char[512];
        return Native.SetupDiGetDeviceInstanceIdW(set, ref data, buffer, 512, out _) ? new string(buffer) : "";
    }

    private static unsafe byte[]? GetProperty(nint set, ref Native.SP_DEVINFO_DATA data, uint property)
    {
        Native.SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, null, 0, out uint required);
        if (required == 0) return null;
        var buffer = new byte[required];
        fixed (byte* p = buffer)
        {
            if (!Native.SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, p, required, out _)) return null;
        }
        return buffer;
    }

    private static string? GetStringProperty(nint set, ref Native.SP_DEVINFO_DATA data, uint property)
    {
        var bytes = GetProperty(set, ref data, property);
        return bytes is null ? null : Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    private static string[] GetMultiStringProperty(nint set, ref Native.SP_DEVINFO_DATA data, uint property)
    {
        var bytes = GetProperty(set, ref data, property);
        return bytes is null ? [] : Encoding.Unicode.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Finds the device interface path registered by WinUSB (DeviceInterfaceGUIDs in the device registry key).</summary>
    private static unsafe string? GetDevicePath(nint set, ref Native.SP_DEVINFO_DATA data, string instanceId)
    {
        var guids = new List<Guid>();
        nint key = Native.SetupDiOpenDevRegKey(set, ref data, Native.DICS_FLAG_GLOBAL, 0, Native.DIREG_DEV, Native.KEY_READ);
        if (key != -1 && key != 0)
        {
            try
            {
                foreach (var name in new[] { "DeviceInterfaceGUIDs", "DeviceInterfaceGUID" })
                {
                    uint size = 0;
                    if (Native.RegQueryValueExW(key, name, 0, out _, null, ref size) != 0 || size == 0) continue;
                    var buffer = new byte[size];
                    fixed (byte* p = buffer)
                    {
                        if (Native.RegQueryValueExW(key, name, 0, out _, p, ref size) != 0) continue;
                    }
                    foreach (var s in Encoding.Unicode.GetString(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                        if (Guid.TryParse(s, out var g)) guids.Add(g);
                }
            }
            finally
            {
                Native.RegCloseKey(key);
            }
        }
        guids.Add(Native.GUID_DEVINTERFACE_USB_DEVICE);

        foreach (var guid in guids)
        {
            var g = guid;
            nint ifSet = Native.SetupDiGetClassDevsW(&g, null, 0, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (ifSet == -1) continue;
            try
            {
                var ifData = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = (uint)sizeof(Native.SP_DEVICE_INTERFACE_DATA) };
                for (uint i = 0; Native.SetupDiEnumDeviceInterfaces(ifSet, null, ref g, i, ref ifData); i++)
                {
                    var devInfo = new Native.SP_DEVINFO_DATA { cbSize = (uint)sizeof(Native.SP_DEVINFO_DATA) };
                    Native.SetupDiGetDeviceInterfaceDetailW(ifSet, ref ifData, null, 0, out uint required, null);
                    if (required == 0) continue;
                    var detail = new byte[required];
                    fixed (byte* p = detail)
                    {
                        *(uint*)p = (uint)(IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetailW(ifSet, ref ifData, p, required, out _, &devInfo)) continue;
                        if (!string.Equals(GetInstanceId(ifSet, ref devInfo), instanceId, StringComparison.OrdinalIgnoreCase)) continue;
                        return new string((char*)(p + 4));
                    }
                }
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(ifSet);
            }
        }
        return null;
    }
}

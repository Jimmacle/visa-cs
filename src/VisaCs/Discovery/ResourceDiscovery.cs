using System.Globalization;
using System.Net;
using System.Net.Sockets;
using VisaCs.Gpib;
using VisaCs.Internal;
using VisaCs.Lan;
using VisaCs.Pci;
using VisaCs.Serial;
using VisaCs.Usb;

namespace VisaCs.Discovery;

/// <summary>The resource sources behind <see cref="ResourceManager.FindResources(string)"/>.</summary>
internal static class ResourceDiscovery
{
    public static ResourceInfo Describe(ResourceName name, string? description, Dictionary<VisaAttribute, object> attributes)
    {
        attributes[VisaAttribute.ResourceName] = name.Canonical;
        attributes[VisaAttribute.InterfaceType] = name.InterfaceType;
        attributes[VisaAttribute.InterfaceNumber] = name.Board;
        attributes[VisaAttribute.ResourceClass] = name.ResourceClass.ToVisaString();
        return new ResourceInfo(name.Canonical, name, null, description, attributes);
    }

    public static List<ResourceInfo> DiscoverUsb(IUsbBackend? backend)
    {
        var result = new List<ResourceInfo>();
        if (backend is null) return result;
        foreach (var device in backend.EnumerateDevices())
        {
            foreach (var intf in device.Interfaces)
            {
                ResourceClass cls;
                if (intf.IsUsbTmc) cls = ResourceClass.Instrument;
                else if (intf.Class == 0xFF && intf.Endpoints.Any(e => e.Type == UsbTransferType.Bulk)) cls = ResourceClass.Raw;
                else continue;
                var name = new UsbResourceName
                {
                    InterfaceType = HardwareInterfaceType.Usb, Board = 0, ResourceClass = cls,
                    VendorId = device.VendorId, ProductId = device.ProductId, SerialNumber = device.SerialNumber,
                    InterfaceNumber = intf.Number, OriginalName = "",
                };
                string description = string.Join(" ", new[] { device.Manufacturer, device.Product }.Where(s => !string.IsNullOrWhiteSpace(s)));
                result.Add(Describe(name, description.Length > 0 ? description : null, new Dictionary<VisaAttribute, object>
                {
                    [VisaAttribute.ManufacturerId] = (int)device.VendorId,
                    [VisaAttribute.ModelCode] = (int)device.ProductId,
                    [VisaAttribute.UsbSerialNumber] = device.SerialNumber,
                    [VisaAttribute.UsbInterfaceNumber] = (int)intf.Number,
                    [VisaAttribute.UsbProtocol] = (int)intf.Protocol,
                    [VisaAttribute.UsbClass] = (int)intf.Class,
                    [VisaAttribute.UsbSubclass] = (int)intf.SubClass,
                    [VisaAttribute.ManufacturerName] = device.Manufacturer ?? "",
                    [VisaAttribute.ModelName] = device.Product ?? "",
                }));
            }
        }
        return result;
    }

    public static List<ResourceInfo> DiscoverSerial()
    {
        var result = new List<ResourceInfo>();
        foreach (var port in NativeSerialPort.GetPortNames())
        {
            string resource = OperatingSystem.IsWindows() && port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
                              int.TryParse(port.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                ? $"ASRL{n}::INSTR"
                : $"ASRL{port}::INSTR";
            if (!ResourceName.TryParse(resource, out var name)) continue;
            result.Add(Describe(name, port, new Dictionary<VisaAttribute, object>()));
        }
        return result;
    }

    public static List<ResourceInfo> DiscoverGpib(IGpibDriver? driver)
    {
        var result = new List<ResourceInfo>();
        if (driver is null) return result;
        foreach (int board in driver.ListBoards())
        {
            result.Add(Describe(ResourceName.Parse($"GPIB{board}::INTFC"), $"GPIB board {board}", new Dictionary<VisaAttribute, object>()));
            foreach (var (pad, sad) in driver.FindListeners(board))
            {
                var name = ResourceName.Parse(sad is { } s ? $"GPIB{board}::{pad}::{s}::INSTR" : $"GPIB{board}::{pad}::INSTR");
                result.Add(Describe(name, null, new Dictionary<VisaAttribute, object>
                {
                    [VisaAttribute.GpibPrimaryAddress] = pad,
                    [VisaAttribute.GpibSecondaryAddress] = sad ?? 0xFFFF,
                }));
            }
        }
        return result;
    }

    public static List<ResourceInfo> DiscoverPxi(string pciDevicesPath)
    {
        var result = new List<ResourceInfo>();
        foreach (var device in PciDeviceInfo.Enumerate(pciDevicesPath))
        {
            if (!device.IsAvailableToUserSpace) continue;
            string domain = device.Domain == 0 ? "" : $"{device.Domain:x4}:";
            if (!ResourceName.TryParse($"PXI0::{domain}{device.Bus}-{device.Device}.{device.Function}::INSTR", out var name)) continue;
            result.Add(Describe(name, $"PCI {device.VendorId:X4}:{device.DeviceId:X4}" + (device.Driver is { } d ? $" ({d})" : ""),
                new Dictionary<VisaAttribute, object>
                {
                    [VisaAttribute.ManufacturerId] = (int)device.VendorId,
                    [VisaAttribute.ModelCode] = (int)device.DeviceId,
                    [VisaAttribute.PxiBusNumber] = device.Bus,
                    [VisaAttribute.PxiDeviceNumber] = device.Device,
                    [VisaAttribute.PxiFunctionNumber] = device.Function,
                    [VisaAttribute.PxiIsExpress] = device.IsExpress,
                }));
        }
        return result;
    }
}

/// <summary>
/// One LAN search: VXI-11 portmapper broadcast and mDNS browsing, started together so that their listening times
/// overlap with each other and with the local enumerations. Asynchronous searches hold no thread; synchronous ones
/// use one thread-pool thread per protocol.
/// </summary>
internal sealed class LanSearch
{
    private static readonly string[] MdnsServices = [MdnsServiceTypes.Hislip, MdnsServiceTypes.Vxi11, MdnsServiceTypes.ScpiRaw];

    private readonly ResourceManagerOptions _options;
    private readonly Task<IReadOnlyList<IPAddress>>? _vxi11;
    private readonly Task<IReadOnlyList<MdnsServiceInstance>>? _mdns;

    private LanSearch(ResourceManagerOptions options, CancellationToken cancellationToken, bool async)
    {
        _options = options;
        int timeout = options.LanDiscoveryTimeoutMilliseconds;
        if (options.EnableLanDiscovery || options.LanDiscoveryTargets.Count > 0)
        {
            bool broadcast = options.EnableLanDiscovery && options.LanDiscoveryUsesBroadcast;
            var targets = options.LanDiscoveryTargets.ToList();
            _vxi11 = async
                ? Vxi11Discovery.DiscoverAsync(timeout, options.Vxi11PortmapperPort, targets, broadcast, cancellationToken)
                : Task.Run(() => Vxi11Discovery.Discover(timeout, options.Vxi11PortmapperPort, targets, broadcast));
        }
        if (options.EnableMdnsDiscovery)
        {
            var endPoint = options.MdnsEndPoint;
            _mdns = async
                ? MdnsDiscovery.BrowseAsync(MdnsServices, timeout, endPoint, cancellationToken)
                : Task.Run(() => MdnsDiscovery.Browse(MdnsServices, timeout, endPoint));
        }
    }

    /// <summary>Starts a search, or returns null when every LAN source is turned off.</summary>
    public static LanSearch? Start(ResourceManagerOptions options, CancellationToken cancellationToken, bool async) =>
        options.EnableLanDiscovery || options.EnableMdnsDiscovery || options.LanDiscoveryTargets.Count > 0
            ? new LanSearch(options, cancellationToken, async)
            : null;

    /// <summary>Waits for the search and returns what it found. A failing protocol does not hide the other's results.</summary>
    public async ValueTask<List<ResourceInfo>> CollectAsync(bool async)
    {
        var result = new List<ResourceInfo>();
        if (_vxi11 is not null)
        {
            try
            {
                foreach (var ip in async ? await _vxi11.ConfigureAwait(false) : _vxi11.GetAwaiter().GetResult())
                {
                    var name = ResourceName.Parse($"TCPIP0::{NetUtil.FormatHost(ip.ToString())}::inst0::INSTR");
                    result.Add(ResourceDiscovery.Describe(name, "VXI-11 instrument", new Dictionary<VisaAttribute, object>
                    {
                        [VisaAttribute.TcpipAddress] = ip.ToString(),
                        [VisaAttribute.TcpipDeviceName] = "inst0",
                        [VisaAttribute.TcpipIsHislip] = false,
                    }));
                }
            }
            catch (Exception)
            {
                // A failing source must not hide the others.
            }
        }

        if (_mdns is not null)
        {
            try
            {
                foreach (var service in async ? await _mdns.ConfigureAwait(false) : _mdns.GetAwaiter().GetResult())
                {
                    if (Describe(service) is { } info) result.Add(info);
                }
            }
            catch (Exception)
            {
                // A failing source must not hide the others.
            }
        }
        return result;
    }

    private ResourceInfo? Describe(MdnsServiceInstance service)
    {
        var ip = service.Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? service.Addresses.FirstOrDefault();
        string host = NetUtil.FormatHost(ip?.ToString() ?? service.HostName.TrimEnd('.'));
        string? resource = service.ServiceType.ToLowerInvariant() switch
        {
            "_hislip._tcp.local" => service.Port == _options.HislipPort || service.Port == 0
                ? $"TCPIP0::{host}::hislip0::INSTR"
                : $"TCPIP0::{host}::hislip0,{service.Port}::INSTR",
            "_vxi-11._tcp.local" => $"TCPIP0::{host}::inst0::INSTR",
            "_scpi-raw._tcp.local" => $"TCPIP0::{host}::{service.Port}::SOCKET",
            _ => null,
        };
        if (resource is null || !ResourceName.TryParse(resource, out var name)) return null;
        return ResourceDiscovery.Describe(name, service.InstanceName, new Dictionary<VisaAttribute, object>
        {
            [VisaAttribute.TcpipAddress] = ip?.ToString() ?? "",
            [VisaAttribute.TcpipHostName] = service.HostName.TrimEnd('.'),
            [VisaAttribute.TcpipPort] = service.Port,
            [VisaAttribute.TcpipIsHislip] = service.ServiceType.StartsWith("_hislip", StringComparison.OrdinalIgnoreCase),
        });
    }
}

using System.Net;
using VisaCs.Gpib;
using VisaCs.Lan;
using VisaCs.Usb;

namespace VisaCs;

/// <summary>Configuration for a <see cref="ResourceManager"/>.</summary>
/// <remarks>
/// The resource manager reads these settings when it uses them, so changes made after it was created apply to later
/// calls, except <see cref="UsbBackend"/> and <see cref="GpibDriver"/>, which are chosen once, on first use. Do not
/// change the options while another thread opens or finds resources.
/// </remarks>
public sealed class ResourceManagerOptions
{
    /// <summary>Initial I/O timeout of new sessions (VI_ATTR_TMO_VALUE), in milliseconds; -1 for no limit. Default 2000.</summary>
    public int DefaultTimeoutMilliseconds { get; set; } = 2000;

    /// <summary>
    /// How long opening a session may take to establish the connection (TCP connect, protocol handshake), in
    /// milliseconds. Waiting for a lock requested at open is bounded separately by the open timeout of
    /// <see cref="ResourceManager.Open(string, AccessModes, int)"/>. Default 5000.
    /// </summary>
    public int ConnectTimeoutMilliseconds { get; set; } = 5000;

    /// <summary>Portmapper port used to locate VXI-11 servers. Standard: 111.</summary>
    public int Vxi11PortmapperPort { get; set; } = 111;

    /// <summary>Default HiSLIP port when the resource name does not specify one. Standard: 4880.</summary>
    public int HislipPort { get; set; } = 4880;

    /// <summary>Credentials, certificate trust and encryption policy for HiSLIP 2.0 secure connections.</summary>
    public HislipSecurityOptions HislipSecurity { get; set; } = new();

    /// <summary>VICP port. Standard: 1861.</summary>
    public int VicpPort { get; set; } = 1861;

    /// <summary>
    /// Whether <see cref="ResourceManager.Find"/> broadcasts a VXI-11 portmapper query to find LAN instruments.
    /// Default true.
    /// </summary>
    public bool EnableLanDiscovery { get; set; } = true;

    /// <summary>Whether <see cref="ResourceManager.Find"/> finds HiSLIP, VXI-11 and SCPI-RAW services with mDNS. Default true.</summary>
    public bool EnableMdnsDiscovery { get; set; } = true;

    /// <summary>How long LAN discovery (VXI-11 and mDNS together) listens for replies, in milliseconds. Default 1000.</summary>
    public int LanDiscoveryTimeoutMilliseconds { get; set; } = 1000;

    /// <summary>Extra addresses (unicast or broadcast) probed by VXI-11 discovery, even when <see cref="EnableLanDiscovery"/> is false.</summary>
    public IList<IPAddress> LanDiscoveryTargets { get; } = new List<IPAddress>();

    /// <summary>Whether VXI-11 discovery also sends to the broadcast addresses of the local networks. Default true.</summary>
    public bool LanDiscoveryUsesBroadcast { get; set; } = true;

    /// <summary>Overrides the mDNS destination (for example a unicast responder). Null means multicast on every interface.</summary>
    public IPEndPoint? MdnsEndPoint { get; set; }

    /// <summary>Whether <see cref="ResourceManager.Find"/> lists USB devices. Default true.</summary>
    public bool EnableUsb { get; set; } = true;

    /// <summary>
    /// USB backend. Null selects the process-wide default (<see cref="UsbBackends.CreateDefault"/>: WinUSB on Windows,
    /// usbfs on Linux, libusb elsewhere). A backend set here stays owned by the caller. Sessions share a USB device only
    /// within one backend instance.
    /// </summary>
    public IUsbBackend? UsbBackend { get; set; }

    /// <summary>Whether <see cref="ResourceManager.Find"/> lists serial ports. Default true.</summary>
    public bool EnableSerial { get; set; } = true;

    /// <summary>Whether <see cref="ResourceManager.Find"/> lists GPIB boards and the listeners on them. Default true.</summary>
    public bool EnableGpib { get; set; } = true;

    /// <summary>GPIB driver. Null selects the NI-488.2 compatible driver if one is installed.</summary>
    public IGpibDriver? GpibDriver { get; set; }

    /// <summary>Whether <see cref="ResourceManager.Find"/> lists PXI/PCI functions available to user space (Linux only). Default true.</summary>
    public bool EnablePxi { get; set; } = true;

    /// <summary>Directory of PCI functions in sysfs. Default /sys/bus/pci/devices.</summary>
    public string PciDevicesPath { get; set; } = "/sys/bus/pci/devices";

    /// <summary>Aliases (alias → resource name, alias case-insensitive), resolved by Open and Parse.</summary>
    public IDictionary<string, string> Aliases { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resources that <see cref="ResourceManager.Find"/> always reports (like statically configured LAN instruments) when they match.</summary>
    public IList<string> StaticResources { get; } = new List<string>();
}

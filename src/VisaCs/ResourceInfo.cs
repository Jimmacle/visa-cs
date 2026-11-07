namespace VisaCs;

/// <summary>A resource found by <see cref="ResourceManager.FindResources(string)"/>.</summary>
/// <param name="ResourceName">The canonical resource name, as <see cref="ResourceManager.Find"/> returns it.</param>
/// <param name="Parsed">The parsed form of <paramref name="ResourceName"/>.</param>
/// <param name="Alias">An alias from <see cref="ResourceManagerOptions.Aliases"/> that names this resource, if any.</param>
/// <param name="Description">A human-readable description when discovery provides one (USB manufacturer and product, mDNS instance name, serial port name), otherwise null.</param>
/// <param name="Attributes">
/// The attributes known without opening the resource, with the value types documented on <see cref="VisaAttribute"/>:
/// always VI_ATTR_RSRC_NAME, VI_ATTR_INTF_TYPE, VI_ATTR_INTF_NUM and VI_ATTR_RSRC_CLASS, plus what discovery reported
/// (TCP/IP address and port, USB IDs and serial number, GPIB addresses, PCI location). Find expressions with an
/// attribute filter are evaluated against these.
/// </param>
public sealed record ResourceInfo(
    string ResourceName,
    ResourceName Parsed,
    string? Alias,
    string? Description,
    IReadOnlyDictionary<VisaAttribute, object> Attributes);

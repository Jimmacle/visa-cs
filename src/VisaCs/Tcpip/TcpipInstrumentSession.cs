namespace VisaCs;

/// <summary>Common base of TCPIP INSTR sessions (VXI-11 and HiSLIP).</summary>
public abstract class TcpipInstrumentSession : MessageBasedSession
{
    private protected TcpipInstrumentSession(ResourceManager manager, TcpipInstrumentResourceName name, int inputBufferSize)
        : base(manager, name, inputBufferSize)
    {
        Name = name;
    }

    private protected TcpipInstrumentResourceName Name { get; }

    /// <summary>Remote IP address (VI_ATTR_TCPIP_ADDR).</summary>
    public string Address { get; private protected set; } = "";

    /// <summary>Host name as given in the resource string (VI_ATTR_TCPIP_HOSTNAME).</summary>
    public string HostName => Name.Host;

    /// <summary>LAN device name, e.g. <c>inst0</c> or <c>hislip0</c> (VI_ATTR_TCPIP_DEVICE_NAME).</summary>
    public string DeviceName => Name.DeviceName;

    /// <summary>True for HiSLIP sessions (VI_ATTR_TCPIP_IS_HISLIP).</summary>
    public abstract bool IsHislip { get; }

    /// <summary>
    /// TCP keep-alive (VI_ATTR_TCPIP_KEEPALIVE): on the VXI-11 core channel, or on both HiSLIP channels. Default false.
    /// </summary>
    public abstract bool KeepAlive { get; set; }

    /// <inheritdoc/>
    public override IReadOnlyCollection<EventType> SupportedEventTypes { get; } = [EventType.ServiceRequest];

    private protected override void RegisterAttributes(AttributeTable table)
    {
        base.RegisterAttributes(table);
        table.Add(VisaAttribute.TcpipAddress, () => Address);
        table.Add(VisaAttribute.TcpipHostName, () => HostName);
        table.Add(VisaAttribute.TcpipDeviceName, () => DeviceName);
        table.Add(VisaAttribute.TcpipIsHislip, () => IsHislip);
        table.Add(VisaAttribute.TcpipKeepAlive, () => KeepAlive, v => KeepAlive = v);
    }
}

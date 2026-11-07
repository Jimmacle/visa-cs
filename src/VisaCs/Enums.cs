namespace VisaCs;

/// <summary>Hardware interface types (VI_ATTR_INTF_TYPE). Values match visa.h.</summary>
public enum HardwareInterfaceType
{
    /// <summary>GPIB (IEEE 488) through an NI-488.2 compatible driver.</summary>
    Gpib = 1,
    /// <summary>VXI backplane (needs vendor drivers; not supported).</summary>
    Vxi = 2,
    /// <summary>VXI through a GPIB-VXI command module (not supported).</summary>
    GpibVxi = 3,
    /// <summary>Serial port (ASRL).</summary>
    Serial = 4,
    /// <summary>PXI / PCI(e) register-based instrument.</summary>
    Pxi = 5,
    /// <summary>TCP/IP: VXI-11, HiSLIP and raw sockets.</summary>
    Tcpip = 6,
    /// <summary>USB Test &amp; Measurement Class (USBTMC/USB488) and raw USB.</summary>
    Usb = 7,
    /// <summary>NI RIO (not supported).</summary>
    Rio = 8,
    /// <summary>IEEE 1394 (not supported).</summary>
    Firewire = 9,
    /// <summary>Rohde &amp; Schwarz NRP power sensors (not supported).</summary>
    RsNrp = 33024,
    /// <summary>Teledyne LeCroy VICP (non-standard; value matches PyVISA).</summary>
    Vicp = 36000,
}

/// <summary>VISA resource classes (VI_ATTR_RSRC_CLASS).</summary>
public enum ResourceClass
{
    /// <summary>An instrument (INSTR).</summary>
    Instrument,
    /// <summary>A raw TCP socket (SOCKET).</summary>
    Socket,
    /// <summary>Raw USB (RAW).</summary>
    Raw,
    /// <summary>An interface board (INTFC).</summary>
    Interface,
    /// <summary>A backplane (BACKPLANE).</summary>
    Backplane,
    /// <summary>A servant/device-side resource (SERVANT).</summary>
    Servant,
    /// <summary>Memory access (MEMACC).</summary>
    MemoryAccess,
}

/// <summary>Access modes for <see cref="ResourceManager.Open(string, AccessModes, int)"/> (viOpen).</summary>
[Flags]
public enum AccessModes
{
    /// <summary>No lock at open.</summary>
    None = 0,
    /// <summary>Acquire an exclusive lock when opening (VI_EXCLUSIVE_LOCK).</summary>
    ExclusiveLock = 1,
    /// <summary>Acquire a shared lock when opening (VI_SHARED_LOCK).</summary>
    SharedLock = 2,
    /// <summary>Load configured attribute values (VI_LOAD_CONFIG). Accepted and ignored: there is no configuration store.</summary>
    LoadConfig = 4,
}

/// <summary>Lock state of a resource (VI_ATTR_RSRC_LOCK_STATE).</summary>
public enum ResourceLockState
{
    /// <summary>The resource is not locked.</summary>
    NoLock = 0,
    /// <summary>A session holds an exclusive lock.</summary>
    ExclusiveLock = 1,
    /// <summary>One or more sessions hold a shared lock.</summary>
    SharedLock = 2,
}

/// <summary>Lock types (VI_EXCLUSIVE_LOCK, VI_SHARED_LOCK), as passed to the device-side locking hooks.</summary>
internal enum LockType
{
    /// <summary>Only the locking session may access the resource.</summary>
    Exclusive = 1,
    /// <summary>Sessions that present the same access key may access the resource.</summary>
    Shared = 2,
}

/// <summary>Why a read operation completed.</summary>
public enum ReadStatus
{
    /// <summary>The END indicator was received (VI_SUCCESS).</summary>
    EndReceived,
    /// <summary>The termination character was read (VI_SUCCESS_TERM_CHAR).</summary>
    TerminationCharacterEncountered,
    /// <summary>The requested number of bytes was read (VI_SUCCESS_MAX_CNT).</summary>
    MaximumCountReached,
}

/// <summary>VISA event types (ViEventType). Values match visa.h.</summary>
public enum EventType : uint
{
    /// <summary>An asynchronous VISA job completed (VI_EVENT_IO_COMPLETION). Not delivered by this implementation.</summary>
    IOCompletion = 0x3FFF2009,
    /// <summary>A trigger was received (VI_EVENT_TRIG).</summary>
    Trigger = 0xBFFF200A,
    /// <summary>The device requested service (SRQ).</summary>
    ServiceRequest = 0x3FFF200B,
    /// <summary>A device clear was received (servant sessions).</summary>
    Clear = 0x3FFF200D,
    /// <summary>An error occurred in a callback or operation.</summary>
    Exception = 0xBFFF200E,
    /// <summary>The GPIB board became or stopped being controller in charge.</summary>
    GpibControllerInCharge = 0x3FFF2012,
    /// <summary>The GPIB board was addressed to talk.</summary>
    GpibTalk = 0x3FFF2013,
    /// <summary>The GPIB board was addressed to listen.</summary>
    GpibListen = 0x3FFF2014,
    /// <summary>VXI SYSFAIL asserted.</summary>
    VxiVmeSysfail = 0x3FFF201D,
    /// <summary>VXI SYSRESET asserted.</summary>
    VxiVmeSysreset = 0x3FFF201E,
    /// <summary>VXI signal register write.</summary>
    VxiSignalProcessor = 0x3FFF2020,
    /// <summary>VME interrupt.</summary>
    VxiVmeInterrupt = 0xBFFF2021,
    /// <summary>PXI interrupt.</summary>
    PxiInterrupt = 0x3FFF2022,
    /// <summary>A TCP/IP connection was accepted (server sockets).</summary>
    TcpipConnect = 0x3FFF2036,
    /// <summary>A vendor-specific USB interrupt-IN packet arrived.</summary>
    UsbInterrupt = 0x3FFF2037,
    /// <summary>Wildcard for all enabled events (VI_ALL_ENABLED_EVENTS).</summary>
    AllEnabled = 0x3FFF7FFF,
}

/// <summary>Event delivery mechanisms (VI_QUEUE, VI_HNDLR, VI_SUSPEND_HNDLR).</summary>
[Flags]
public enum EventMechanism
{
    /// <summary>No mechanism.</summary>
    None = 0,
    /// <summary>Events are queued for <see cref="VisaSession.WaitOnEvent(EventType, int)"/>.</summary>
    Queue = 1,
    /// <summary>Events are passed to the <see cref="VisaSession.ServiceRequest"/> and <see cref="VisaSession.VisaEvent"/> handlers.</summary>
    Handler = 2,
    /// <summary>Events are held and passed to handlers when the handler mechanism is enabled.</summary>
    SuspendHandler = 4,
    /// <summary>Queue and handler.</summary>
    All = Queue | Handler,
}

/// <summary>Serial END indicator handling (VI_ATTR_ASRL_END_IN / VI_ATTR_ASRL_END_OUT).</summary>
public enum SerialTerminationMethod
{
    /// <summary>No END indicator.</summary>
    None = 0,
    /// <summary>The highest data bit marks the last byte.</summary>
    LastBit = 1,
    /// <summary>The termination character marks the end.</summary>
    TerminationCharacter = 2,
    /// <summary>A break marks the end (output only).</summary>
    Break = 3,
}

/// <summary>State of a hardware line (VI_STATE_*).</summary>
public enum LineState
{
    /// <summary>The state cannot be determined.</summary>
    Unknown = -1,
    /// <summary>The line is not asserted.</summary>
    Unasserted = 0,
    /// <summary>The line is asserted.</summary>
    Asserted = 1,
}

/// <summary>I/O protocol (VI_ATTR_IO_PROT).</summary>
public enum IOProtocol
{
    /// <summary>Normal protocol.</summary>
    Normal = 1,
    /// <summary>VXI fast data channel. Not supported (VXI is not implemented).</summary>
    Fdc = 2,
    /// <summary>GPIB HS488 high-speed handshake. Not supported; GPIB sessions use the normal IEEE 488.1 handshake.</summary>
    HS488 = 3,
    /// <summary>Serial and socket sessions: also send <c>*CLS</c> on device clear. Status byte and trigger always use <c>*STB?</c> and <c>*TRG</c> there.</summary>
    Ieee4882Strings = 4,
    /// <summary>USB INSTR sessions: send and request data with USBTMC VENDOR_SPECIFIC_OUT / REQUEST_VENDOR_SPECIFIC_IN messages.</summary>
    UsbTmcVendor = 5,
}

/// <summary>Remote/local control modes (VI_GPIB_REN_*), used by GPIB, USB488, VXI-11 and HiSLIP.</summary>
public enum RemoteLocalMode
{
    /// <summary>Deassert REN (VI_GPIB_REN_DEASSERT).</summary>
    Deassert = 0,
    /// <summary>Assert REN (VI_GPIB_REN_ASSERT).</summary>
    Assert = 1,
    /// <summary>Send Go To Local and deassert REN (VI_GPIB_REN_DEASSERT_GTL).</summary>
    DeassertGoToLocal = 2,
    /// <summary>Assert REN and address the device (VI_GPIB_REN_ASSERT_ADDRESS).</summary>
    AssertAddress = 3,
    /// <summary>Assert REN and send Local Lockout (VI_GPIB_REN_ASSERT_LLO).</summary>
    AssertLocalLockout = 4,
    /// <summary>Assert REN, address the device and send Local Lockout (VI_GPIB_REN_ASSERT_ADDRESS_LLO).</summary>
    AssertAddressLocalLockout = 5,
    /// <summary>Address the device and send Go To Local (VI_GPIB_REN_ADDRESS_GTL).</summary>
    AddressGoToLocal = 6,
}

/// <summary>Buffer selection for <see cref="MessageBasedSession.Flush(BufferTypes)"/>.</summary>
[Flags]
public enum BufferTypes
{
    /// <summary>Flush the formatted read buffer (VI_READ_BUF).</summary>
    ReadBuffer = 0x01,
    /// <summary>Flush the formatted write buffer (VI_WRITE_BUF).</summary>
    WriteBuffer = 0x02,
    /// <summary>Discard the formatted read buffer (VI_READ_BUF_DISCARD).</summary>
    ReadBufferDiscard = 0x04,
    /// <summary>Discard the formatted write buffer (VI_WRITE_BUF_DISCARD).</summary>
    WriteBufferDiscard = 0x08,
    /// <summary>Flush the low-level receive buffer (VI_IO_IN_BUF).</summary>
    InBuffer = 0x10,
    /// <summary>Flush the low-level transmit buffer (VI_IO_OUT_BUF).</summary>
    OutBuffer = 0x20,
    /// <summary>Discard the low-level receive buffer (VI_IO_IN_BUF_DISCARD).</summary>
    InBufferDiscard = 0x40,
    /// <summary>Discard the low-level transmit buffer (VI_IO_OUT_BUF_DISCARD).</summary>
    OutBufferDiscard = 0x80,
}

/// <summary>USB Test &amp; Measurement protocol (VI_ATTR_USB_PROTOCOL).</summary>
public enum UsbProtocol
{
    /// <summary>Plain USBTMC (interface protocol 0).</summary>
    UsbTmc = 0,
    /// <summary>USBTMC USB488 subclass (interface protocol 1): status byte, SRQ, trigger, REN.</summary>
    Usb488 = 1,
}


internal static class EnumExtensions
{
    extension(ResourceClass rc)
    {
        /// <summary>The resource class as written in resource names, e.g. <c>INSTR</c>.</summary>
        public string ToVisaString() => rc switch
        {
            ResourceClass.Instrument => "INSTR",
            ResourceClass.Socket => "SOCKET",
            ResourceClass.Raw => "RAW",
            ResourceClass.Interface => "INTFC",
            ResourceClass.Backplane => "BACKPLANE",
            ResourceClass.Servant => "SERVANT",
            ResourceClass.MemoryAccess => "MEMACC",
            _ => rc.ToString().ToUpperInvariant(),
        };
    }

    extension(ResourceClass)
    {
        /// <summary>Parses a resource class as written in resource names (case-insensitive).</summary>
        public static bool TryParseVisaString(string s, out ResourceClass rc)
        {
            switch (s.ToUpperInvariant())
            {
                case "INSTR": rc = ResourceClass.Instrument; return true;
                case "SOCKET": rc = ResourceClass.Socket; return true;
                case "RAW": rc = ResourceClass.Raw; return true;
                case "INTFC": rc = ResourceClass.Interface; return true;
                case "BACKPLANE": rc = ResourceClass.Backplane; return true;
                case "SERVANT": rc = ResourceClass.Servant; return true;
                case "MEMACC": rc = ResourceClass.MemoryAccess; return true;
                default: rc = default; return false;
            }
        }
    }
}

namespace VisaCs;

/// <summary>
/// VISA attribute identifiers (ViAttr) for <see cref="VisaSession.GetAttribute(VisaAttribute)"/> and
/// <see cref="VisaSession.SetAttribute(VisaAttribute, object?)"/>. Values match visa.h (checked against PyVISA's
/// constants and NI's attribute list).
/// </summary>
/// <remarks>
/// Each member says the type of the value <c>GetAttribute</c> returns, whether it can be set (RW) or only read (R),
/// and the typed property that holds the same value. VISA enumerations are returned as their .NET enum, flags as
/// <see cref="bool"/>, numbers as <see cref="int"/> or <see cref="long"/>. Which session types support which
/// attribute is listed in docs/attributes.md and by <see cref="VisaSession.SupportedAttributes"/>; members marked
/// "not supported" exist only to name visa.h constants and throw VI_ERROR_NSUP_ATTR on every session.
/// </remarks>
public enum VisaAttribute : uint
{
    // Generic resource attributes
    /// <summary>VI_ATTR_RSRC_CLASS: the resource class as written in names (<c>INSTR</c>, <c>SOCKET</c>, <c>RAW</c>, <c>INTFC</c>). <see cref="string"/>, R. Typed: <see cref="VisaSession.ResourceClass"/> (enum).</summary>
    ResourceClass = 0xBFFF0001,
    /// <summary>VI_ATTR_RSRC_NAME: the canonical resource name. <see cref="string"/>, R. Typed: <see cref="VisaSession.ResourceName"/>.</summary>
    ResourceName = 0xBFFF0002,
    /// <summary>VI_ATTR_RSRC_IMPL_VERSION: this library's version, encoded as major &lt;&lt; 20 | minor &lt;&lt; 8 | build. <see cref="int"/>, R. Typed: <see cref="ResourceManager.ImplementationVersion"/>.</summary>
    ResourceImplementationVersion = 0x3FFF0003,
    /// <summary>VI_ATTR_RSRC_LOCK_STATE: lock state of the resource in this process. <see cref="VisaCs.ResourceLockState"/>, R. Typed: <see cref="VisaSession.ResourceLockState"/>.</summary>
    ResourceLockState = 0x3FFF0004,
    /// <summary>VI_ATTR_MAX_QUEUE_LENGTH: capacity of the event queue (at least 1). <see cref="int"/>, RW. Typed: <see cref="VisaSession.MaximumQueueLength"/>.</summary>
    MaxQueueLength = 0x3FFF0005,
    /// <summary>VI_ATTR_USER_DATA (the 64-bit form, the default on 64-bit platforms): application data. Any object or null, RW. Typed: <see cref="VisaSession.UserData"/>.</summary>
    UserData = 0x3FFF000A,
    /// <summary>VI_ATTR_USER_DATA_32: the same value as <see cref="UserData"/>. Any object or null, RW.</summary>
    UserData32 = 0x3FFF0007,
    /// <summary>VI_ATTR_RSRC_SPEC_VERSION: the VISA specification version implemented (7.2), encoded like <see cref="ResourceImplementationVersion"/>. <see cref="int"/>, R.</summary>
    ResourceSpecVersion = 0x3FFF0170,
    /// <summary>VI_ATTR_INTF_TYPE: the hardware interface. <see cref="HardwareInterfaceType"/>, R. Typed: <see cref="VisaSession.HardwareInterfaceType"/>.</summary>
    InterfaceType = 0x3FFF0171,
    /// <summary>VI_ATTR_RSRC_MANF_NAME: the name of this implementation, <c>VisaCs</c>. <see cref="string"/>, R.</summary>
    ResourceManufacturerName = 0xBFFF0174,
    /// <summary>VI_ATTR_RSRC_MANF_ID: this implementation's VISA vendor ID, 0x3FFF (reserved for unregistered vendors). <see cref="int"/>, R.</summary>
    ResourceManufacturerId = 0x3FFF0175,
    /// <summary>VI_ATTR_INTF_NUM: the board number. <see cref="int"/>, R. Typed: <see cref="VisaSession.HardwareInterfaceNumber"/>.</summary>
    InterfaceNumber = 0x3FFF0176,
    /// <summary>VI_ATTR_TRIG_ID. Not supported.</summary>
    TriggerId = 0x3FFF0177,
    /// <summary>VI_ATTR_INTF_INST_NAME: the interface prefix with board number, e.g. <c>GPIB0</c>. <see cref="string"/>, R. Typed: <see cref="VisaCs.ResourceName.InterfaceName"/>.</summary>
    InterfaceInstanceName = 0xBFFF00E9,
    /// <summary>VI_ATTR_MANF_NAME: the device's manufacturer string (USB), empty if it has none. <see cref="string"/>, R. Typed: <see cref="UsbSessionBase.ManufacturerName"/>.</summary>
    ManufacturerName = 0xBFFF0072,
    /// <summary>VI_ATTR_MODEL_NAME: the device's product string (USB), empty if it has none. <see cref="string"/>, R. Typed: <see cref="UsbSessionBase.ModelName"/>.</summary>
    ModelName = 0xBFFF0077,
    /// <summary>VI_ATTR_MANF_ID: the USB vendor ID or PCI vendor ID. <see cref="int"/>, R. Typed: <c>ManufacturerId</c> of <see cref="UsbSessionBase"/> and <see cref="PxiSession"/>.</summary>
    ManufacturerId = 0x3FFF00D9,
    /// <summary>VI_ATTR_MODEL_CODE: the USB product ID or PCI device ID. <see cref="int"/>, R. Typed: <c>ModelCode</c> of <see cref="UsbSessionBase"/> and <see cref="PxiSession"/>.</summary>
    ModelCode = 0x3FFF00DF,
    /// <summary>VI_ATTR_SLOT: the PXI slot; -1 when unknown (always, as chassis information is not read). <see cref="int"/>, R.</summary>
    Slot = 0x3FFF00E8,

    // Message based
    /// <summary>VI_ATTR_SEND_END_EN: whether writes end with END. <see cref="bool"/>, RW. Typed: <see cref="MessageBasedSession.SendEndEnabled"/>.</summary>
    SendEndEnabled = 0x3FFF0016,
    /// <summary>VI_ATTR_TERMCHAR: the termination character. <see cref="byte"/>, RW. Typed: <see cref="MessageBasedSession.TerminationCharacter"/>.</summary>
    TerminationCharacter = 0x3FFF0018,
    /// <summary>
    /// VI_ATTR_TMO_VALUE: the I/O timeout in milliseconds; 0xFFFFFFFF (VI_TMO_INFINITE) means no limit. <see cref="long"/>, RW.
    /// Typed: <see cref="VisaSession.TimeoutMilliseconds"/> (-1 for no limit).
    /// </summary>
    TimeoutValue = 0x3FFF001A,
    /// <summary>VI_ATTR_IO_PROT: the I/O protocol. <see cref="VisaCs.IOProtocol"/>, RW. Typed: <see cref="MessageBasedSession.IOProtocol"/>.</summary>
    IOProtocol = 0x3FFF001C,
    /// <summary>VI_ATTR_DMA_ALLOW_EN. Not supported.</summary>
    DmaAllowEnabled = 0x3FFF001E,
    /// <summary>VI_ATTR_RD_BUF_OPER_MODE. Not supported.</summary>
    ReadBufferOperationMode = 0x3FFF002A,
    /// <summary>
    /// VI_ATTR_RD_BUF_SIZE: here the chunk size used when reading a complete response (there is no separate formatted
    /// read buffer). <see cref="int"/>, RW. Typed: <see cref="MessageBasedSession.ReadChunkSize"/>.
    /// </summary>
    ReadBufferSize = 0x3FFF002B,
    /// <summary>VI_ATTR_WR_BUF_OPER_MODE. Not supported.</summary>
    WriteBufferOperationMode = 0x3FFF002D,
    /// <summary>VI_ATTR_WR_BUF_SIZE. Not supported.</summary>
    WriteBufferSize = 0x3FFF002E,
    /// <summary>VI_ATTR_SUPPRESS_END_EN: whether reads ignore END. <see cref="bool"/>, RW. Typed: <see cref="MessageBasedSession.SuppressEndEnabled"/>.</summary>
    SuppressEndEnabled = 0x3FFF0036,
    /// <summary>VI_ATTR_TERMCHAR_EN: whether reads stop at the termination character. <see cref="bool"/>, RW. Typed: <see cref="MessageBasedSession.TerminationCharacterEnabled"/>.</summary>
    TerminationCharacterEnabled = 0x3FFF0038,
    /// <summary>VI_ATTR_FILE_APPEND_EN. Not supported.</summary>
    FileAppendEnabled = 0x3FFF0192,
    /// <summary>VI_ATTR_4882_COMPLIANT: whether the device follows IEEE 488.2 (always true). <see cref="bool"/>, R. Typed: <see cref="MessageBasedSession.Is4882Compliant"/>.</summary>
    Is4882Compliant = 0x3FFF019F,

    // Register based
    /// <summary>VI_ATTR_DEST_ACCESS_PRIV. Not supported.</summary>
    DestinationAccessPrivilege = 0x3FFF0039,
    /// <summary>VI_ATTR_DEST_BYTE_ORDER: byte order of register writes (PXI). <see cref="ByteOrder"/>, RW. Typed: <see cref="PxiSession.DestinationByteOrder"/>.</summary>
    DestinationByteOrder = 0x3FFF003A,
    /// <summary>VI_ATTR_SRC_ACCESS_PRIV. Not supported.</summary>
    SourceAccessPrivilege = 0x3FFF003C,
    /// <summary>VI_ATTR_SRC_BYTE_ORDER: byte order of register reads (PXI). <see cref="ByteOrder"/>, RW. Typed: <see cref="PxiSession.SourceByteOrder"/>.</summary>
    SourceByteOrder = 0x3FFF003D,
    /// <summary>VI_ATTR_SRC_INCREMENT: 1 if block reads advance through registers, 0 to read one register repeatedly (PXI). <see cref="int"/>, RW. Typed: <see cref="PxiSession.SourceIncrement"/>.</summary>
    SourceIncrement = 0x3FFF0040,
    /// <summary>VI_ATTR_DEST_INCREMENT: 1 if block writes advance through registers, 0 to write one register repeatedly (PXI). <see cref="int"/>, RW. Typed: <see cref="PxiSession.DestinationIncrement"/>.</summary>
    DestinationIncrement = 0x3FFF0041,
    /// <summary>VI_ATTR_WIN_ACCESS_PRIV. Not supported.</summary>
    WindowAccessPrivilege = 0x3FFF0045,
    /// <summary>VI_ATTR_WIN_BYTE_ORDER: byte order of accesses through a mapped window (PXI). <see cref="ByteOrder"/>, RW. Typed: <see cref="PxiSession.WindowByteOrder"/>.</summary>
    WindowByteOrder = 0x3FFF0047,
    /// <summary>VI_ATTR_WIN_BASE_ADDR (64-bit form): offset of the mapped window, 0 when none is mapped (PXI). <see cref="long"/>, R. Typed: <see cref="PxiSession.Window"/>.</summary>
    WindowBaseAddress = 0x3FFF009B,
    /// <summary>VI_ATTR_WIN_BASE_ADDR_32. Not supported; use <see cref="WindowBaseAddress"/>.</summary>
    WindowBaseAddress32 = 0x3FFF0098,
    /// <summary>VI_ATTR_WIN_SIZE (64-bit form): size of the mapped window in bytes, 0 when none is mapped (PXI). <see cref="long"/>, R. Typed: <see cref="PxiSession.Window"/>.</summary>
    WindowSize = 0x3FFF009C,
    /// <summary>VI_ATTR_WIN_SIZE_32. Not supported; use <see cref="WindowSize"/>.</summary>
    WindowSize32 = 0x3FFF009A,
    /// <summary>VI_ATTR_WIN_ACCESS: 1 (VI_NMAPPED) without a window, 2 (VI_USE_OPERS) or 3 (VI_DEREF_ADDR) with one (PXI). <see cref="int"/>, R.</summary>
    WindowAccess = 0x3FFF00C3,
    /// <summary>VI_ATTR_MEM_BASE (64-bit form). Not supported (VXI).</summary>
    MemoryBase = 0x3FFF00D0,
    /// <summary>VI_ATTR_MEM_BASE_32. Not supported (VXI).</summary>
    MemoryBase32 = 0x3FFF00AD,
    /// <summary>VI_ATTR_MEM_SIZE (64-bit form). Not supported (VXI).</summary>
    MemorySize = 0x3FFF00D1,
    /// <summary>VI_ATTR_MEM_SIZE_32. Not supported (VXI).</summary>
    MemorySize32 = 0x3FFF00DD,
    /// <summary>VI_ATTR_MEM_SPACE. Not supported (VXI).</summary>
    MemorySpace = 0x3FFF00DE,

    // GPIB
    /// <summary>VI_ATTR_GPIB_READDR_EN: whether the device is addressed again before every transfer (GPIB INSTR). <see cref="bool"/>, RW. Typed: <see cref="GpibSession.ReaddressingEnabled"/>.</summary>
    GpibReaddressingEnabled = 0x3FFF001B,
    /// <summary>VI_ATTR_GPIB_ATN_STATE: the ATN line (GPIB INTFC). <see cref="LineState"/>, R. Typed: <see cref="GpibInterfaceSession.AtnState"/>.</summary>
    GpibAtnState = 0x3FFF0057,
    /// <summary>VI_ATTR_GPIB_ADDR_STATE: 0 unaddressed, 1 talker, 2 listener (GPIB INTFC). <see cref="int"/>, R. Typed: <see cref="GpibInterfaceSession.AddressState"/>.</summary>
    GpibAddressState = 0x3FFF005C,
    /// <summary>VI_ATTR_GPIB_CIC_STATE: whether the board is controller in charge (GPIB INTFC). <see cref="bool"/>, R. Typed: <see cref="GpibInterfaceSession.IsControllerInCharge"/>.</summary>
    GpibCicState = 0x3FFF005E,
    /// <summary>VI_ATTR_GPIB_NDAC_STATE: the NDAC line (GPIB INTFC). <see cref="LineState"/>, R. Typed: <see cref="GpibInterfaceSession.NdacState"/>.</summary>
    GpibNdacState = 0x3FFF0062,
    /// <summary>VI_ATTR_GPIB_SRQ_STATE: the SRQ line (GPIB INTFC). <see cref="LineState"/>, R. Typed: <see cref="GpibInterfaceSession.SrqState"/>.</summary>
    GpibSrqState = 0x3FFF0067,
    /// <summary>VI_ATTR_GPIB_SYS_CNTRL_STATE: whether the board is system controller (GPIB INTFC). <see cref="bool"/>, R. Typed: <see cref="GpibInterfaceSession.IsSystemController"/>.</summary>
    GpibSystemControllerState = 0x3FFF0068,
    /// <summary>VI_ATTR_GPIB_HS488_CBL_LEN. Not supported.</summary>
    GpibHS488CableLength = 0x3FFF0069,
    /// <summary>VI_ATTR_GPIB_PRIMARY_ADDR: the device's (INSTR, R) or the board's (INTFC, RW) primary address. <see cref="int"/>. Typed: <c>PrimaryAddress</c> of <see cref="GpibSession"/> and <see cref="GpibInterfaceSession"/>.</summary>
    GpibPrimaryAddress = 0x3FFF0172,
    /// <summary>VI_ATTR_GPIB_SECONDARY_ADDR: the device's (INSTR, R) or the board's (INTFC, RW) secondary address; 0xFFFF for none. <see cref="int"/>. Typed: <c>SecondaryAddress</c> (null for none).</summary>
    GpibSecondaryAddress = 0x3FFF0173,
    /// <summary>VI_ATTR_GPIB_REN_STATE: the REN line (GPIB). <see cref="LineState"/>, R. Typed: <c>RenState</c> of <see cref="GpibSession"/> and <see cref="GpibInterfaceSession"/>.</summary>
    GpibRenState = 0x3FFF0181,
    /// <summary>VI_ATTR_GPIB_UNADDR_EN: whether the device is unaddressed after every transfer (GPIB INSTR). <see cref="bool"/>, RW. Typed: <see cref="GpibSession.UnaddressingEnabled"/>.</summary>
    GpibUnaddressingEnabled = 0x3FFF0184,
    /// <summary>VI_ATTR_DEV_STATUS_BYTE. Not supported.</summary>
    DeviceStatusByte = 0x3FFF0189,

    // Serial (ASRL INSTR). The typed properties are on SerialSession.
    /// <summary>VI_ATTR_ASRL_BAUD: baud rate. <see cref="int"/>, RW. Typed: <c>SerialSession.BaudRate</c>.</summary>
    SerialBaudRate = 0x3FFF0021,
    /// <summary>VI_ATTR_ASRL_DATA_BITS: data bits per character (5 to 8). <see cref="int"/>, RW. Typed: <c>SerialSession.DataBits</c>.</summary>
    SerialDataBits = 0x3FFF0022,
    /// <summary>VI_ATTR_ASRL_PARITY: parity. The serial parity enum, RW. Typed: <c>SerialSession.Parity</c>.</summary>
    SerialParity = 0x3FFF0023,
    /// <summary>VI_ATTR_ASRL_STOP_BITS: stop bits. The serial stop bits enum, RW. Typed: <c>SerialSession.StopBits</c>.</summary>
    SerialStopBits = 0x3FFF0024,
    /// <summary>VI_ATTR_ASRL_FLOW_CNTRL: flow control. The serial flow control flags enum, RW. Typed: <c>SerialSession.FlowControl</c>.</summary>
    SerialFlowControl = 0x3FFF0025,
    /// <summary>VI_ATTR_ASRL_AVAIL_NUM: bytes received and not yet read. <see cref="int"/>, R. Typed: <c>SerialSession.BytesAvailable</c>.</summary>
    SerialAvailableNumber = 0x3FFF00AC,
    /// <summary>VI_ATTR_ASRL_CTS_STATE: the CTS input line. <see cref="LineState"/>, R. Typed: <c>SerialSession.CtsState</c>.</summary>
    SerialCtsState = 0x3FFF00AE,
    /// <summary>VI_ATTR_ASRL_DCD_STATE: the DCD input line. <see cref="LineState"/>, R. Typed: <c>SerialSession.DcdState</c>.</summary>
    SerialDcdState = 0x3FFF00AF,
    /// <summary>VI_ATTR_ASRL_DISCARD_NULL: whether received NUL bytes are dropped. <see cref="bool"/>, RW. Typed: <c>SerialSession.DiscardNull</c>.</summary>
    SerialDiscardNull = 0x3FFF00B0,
    /// <summary>VI_ATTR_ASRL_DSR_STATE: the DSR input line. <see cref="LineState"/>, R. Typed: <c>SerialSession.DsrState</c>.</summary>
    SerialDsrState = 0x3FFF00B1,
    /// <summary>VI_ATTR_ASRL_DTR_STATE: the DTR output line. <see cref="LineState"/>, RW. Typed: <c>SerialSession.DtrState</c>.</summary>
    SerialDtrState = 0x3FFF00B2,
    /// <summary>VI_ATTR_ASRL_END_IN: how reads detect END. <see cref="SerialTerminationMethod"/>, RW. Typed: <c>SerialSession.EndInput</c>.</summary>
    SerialEndIn = 0x3FFF00B3,
    /// <summary>VI_ATTR_ASRL_END_OUT: how writes send END. <see cref="SerialTerminationMethod"/>, RW. Typed: <c>SerialSession.EndOutput</c>.</summary>
    SerialEndOut = 0x3FFF00B4,
    /// <summary>VI_ATTR_ASRL_REPLACE_CHAR: the character that replaces bytes received with errors. <see cref="byte"/>, RW. Typed: <c>SerialSession.ReplacementCharacter</c>.</summary>
    SerialReplaceCharacter = 0x3FFF00BE,
    /// <summary>VI_ATTR_ASRL_RI_STATE: the RI input line. <see cref="LineState"/>, R. Typed: <c>SerialSession.RiState</c>.</summary>
    SerialRiState = 0x3FFF00BF,
    /// <summary>VI_ATTR_ASRL_RTS_STATE: the RTS output line. <see cref="LineState"/>, RW. Typed: <c>SerialSession.RtsState</c>.</summary>
    SerialRtsState = 0x3FFF00C0,
    /// <summary>VI_ATTR_ASRL_XON_CHAR: the XON character of software flow control. <see cref="byte"/>, RW. Typed: <c>SerialSession.XOnCharacter</c>.</summary>
    SerialXOnCharacter = 0x3FFF00C1,
    /// <summary>VI_ATTR_ASRL_XOFF_CHAR: the XOFF character of software flow control. <see cref="byte"/>, RW. Typed: <c>SerialSession.XOffCharacter</c>.</summary>
    SerialXOffCharacter = 0x3FFF00C2,
    /// <summary>VI_ATTR_ASRL_CONNECTED. Not supported.</summary>
    SerialConnected = 0x3FFF01BB,
    /// <summary>VI_ATTR_ASRL_BREAK_STATE: whether a break is being sent. <see cref="LineState"/>, RW. Typed: <c>SerialSession.BreakState</c>.</summary>
    SerialBreakState = 0x3FFF01BC,
    /// <summary>VI_ATTR_ASRL_BREAK_LEN: length of the break sent for END, in milliseconds. <see cref="int"/>, RW. Typed: <c>SerialSession.BreakLength</c>.</summary>
    SerialBreakLength = 0x3FFF01BD,
    /// <summary>VI_ATTR_ASRL_ALLOW_TRANSMIT: whether transmission is allowed (false suspends it as if XOFF had been received). <see cref="bool"/>, RW. Typed: <c>SerialSession.AllowTransmit</c>.</summary>
    SerialAllowTransmit = 0x3FFF01BE,
    /// <summary>VI_ATTR_ASRL_WIRE_MODE. Not supported.</summary>
    SerialWireMode = 0x3FFF01BF,

    // TCP/IP
    /// <summary>VI_ATTR_TCPIP_ADDR: the IP address connected to, as text. <see cref="string"/>, R. Typed: <c>Address</c> of the TCP/IP and VICP sessions.</summary>
    TcpipAddress = 0xBFFF0195,
    /// <summary>VI_ATTR_TCPIP_HOSTNAME: the host as written in the resource name. <see cref="string"/>, R. Typed: <c>HostName</c> of the TCP/IP and VICP sessions.</summary>
    TcpipHostName = 0xBFFF0196,
    /// <summary>VI_ATTR_TCPIP_PORT: the TCP port connected to. <see cref="int"/>, R. Typed: <c>Port</c> of the socket, HiSLIP and VICP sessions.</summary>
    TcpipPort = 0x3FFF0197,
    /// <summary>VI_ATTR_TCPIP_DEVICE_NAME: the LAN device name, e.g. <c>inst0</c> or <c>hislip0</c>. <see cref="string"/>, R. Typed: <see cref="TcpipInstrumentSession.DeviceName"/>.</summary>
    TcpipDeviceName = 0xBFFF0199,
    /// <summary>VI_ATTR_TCPIP_NODELAY: whether the Nagle algorithm is off (TCPIP SOCKET). <see cref="bool"/>, RW. Typed: <see cref="TcpipSocketSession.NoDelay"/>.</summary>
    TcpipNoDelay = 0x3FFF019A,
    /// <summary>VI_ATTR_TCPIP_KEEPALIVE: whether TCP keep-alive is on. <see cref="bool"/>, RW. Typed: <c>KeepAlive</c> of the TCP/IP sessions.</summary>
    TcpipKeepAlive = 0x3FFF019B,
    /// <summary>VI_ATTR_TCPIP_SERVER_CERT_ISSUER_NAME: issuer of the HiSLIP server certificate, empty without TLS. <see cref="string"/>, R.</summary>
    TcpipServerCertIssuerName = 0xBFFF0270,
    /// <summary>VI_ATTR_TCPIP_SERVER_CERT_SUBJECT_NAME: subject of the HiSLIP server certificate, empty without TLS. <see cref="string"/>, R.</summary>
    TcpipServerCertSubjectName = 0xBFFF0271,
    /// <summary>VI_ATTR_TCPIP_SERVER_CERT_EXPIRATION_DATE: expiry of the HiSLIP server certificate (ISO 8601, UTC), empty without TLS. <see cref="string"/>, R.</summary>
    TcpipServerCertExpirationDate = 0xBFFF0272,
    /// <summary>VI_ATTR_TCPIP_SERVER_CERT_IS_PERPETUAL: whether the HiSLIP server certificate never expires. <see cref="bool"/>, R.</summary>
    TcpipServerCertIsPerpetual = 0x3FFF0273,
    /// <summary>VI_ATTR_TCPIP_SASL_MECHANISM: the SASL mechanism HiSLIP authenticated with, empty if none. <see cref="string"/>, R. Typed: <see cref="TcpipHislipSession.SaslMechanism"/>.</summary>
    TcpipSaslMechanism = 0xBFFF0274,
    /// <summary>VI_ATTR_TCPIP_TLS_CIPHER_SUITE: the TLS cipher suite of a secure HiSLIP connection, empty without TLS. <see cref="string"/>, R. Typed: <see cref="TcpipHislipSession.TlsCipherSuite"/>.</summary>
    TcpipTlsCipherSuite = 0xBFFF0275,
    /// <summary>VI_ATTR_TCPIP_HISLIP_OVERLAP_EN: HiSLIP overlapped mode; setting it talks to the device. <see cref="bool"/>, RW. Typed: <see cref="TcpipHislipSession.OverlapEnabled"/>.</summary>
    TcpipHislipOverlapEnabled = 0x3FFF0300,
    /// <summary>VI_ATTR_TCPIP_HISLIP_VERSION: the HiSLIP protocol version agreed with the server. <see cref="int"/>, R. Typed: <see cref="TcpipHislipSession.ProtocolVersion"/>.</summary>
    TcpipHislipVersion = 0x3FFF0301,
    /// <summary>VI_ATTR_TCPIP_HISLIP_MAX_MESSAGE_KB: the largest message the client accepts, in KiB. <see cref="int"/>, RW. Typed: <see cref="TcpipHislipSession.MaximumMessageKB"/>.</summary>
    TcpipHislipMaxMessageKB = 0x3FFF0302,
    /// <summary>VI_ATTR_TCPIP_IS_HISLIP: whether the session uses HiSLIP (rather than VXI-11). <see cref="bool"/>, R. Typed: <see cref="TcpipInstrumentSession.IsHislip"/>.</summary>
    TcpipIsHislip = 0x3FFF0303,
    /// <summary>VI_ATTR_TCPIP_HISLIP_ENCRYPTION_EN: whether the HiSLIP connection is encrypted; setting it talks to the device. <see cref="bool"/>, RW. Typed: <see cref="TcpipHislipSession.EncryptionEnabled"/>.</summary>
    TcpipHislipEncryptionEnabled = 0x3FFF0304,

    // USB (USB INSTR and RAW). The typed properties are on UsbSessionBase.
    /// <summary>VI_ATTR_USB_SERIAL_NUM: the device's serial number. <see cref="string"/>, R. Typed: <see cref="UsbSessionBase.SerialNumber"/>.</summary>
    UsbSerialNumber = 0xBFFF01A0,
    /// <summary>VI_ATTR_USB_INTFC_NUM: the claimed interface number. <see cref="int"/>, R. Typed: <see cref="UsbSessionBase.UsbInterfaceNumber"/>.</summary>
    UsbInterfaceNumber = 0x3FFF01A1,
    /// <summary>VI_ATTR_USB_BULK_OUT_PIPE: the bulk-OUT endpoint address, -1 if none. <see cref="int"/>, R.</summary>
    UsbBulkOutPipe = 0x3FFF01A2,
    /// <summary>VI_ATTR_USB_BULK_IN_PIPE: the bulk-IN endpoint address, -1 if none. <see cref="int"/>, R.</summary>
    UsbBulkInPipe = 0x3FFF01A3,
    /// <summary>VI_ATTR_USB_INTR_IN_PIPE: the interrupt-IN endpoint address, -1 if none. <see cref="int"/>, R.</summary>
    UsbInterruptInPipe = 0x3FFF01A4,
    /// <summary>VI_ATTR_USB_CLASS: the interface class code. <see cref="int"/>, R. Typed: <see cref="UsbSessionBase.UsbClass"/>.</summary>
    UsbClass = 0x3FFF01A5,
    /// <summary>VI_ATTR_USB_SUBCLASS: the interface subclass code. <see cref="int"/>, R. Typed: <see cref="UsbSessionBase.UsbSubClass"/>.</summary>
    UsbSubclass = 0x3FFF01A6,
    /// <summary>VI_ATTR_USB_PROTOCOL: the interface protocol code (USBTMC: 0 plain, 1 USB488). <see cref="int"/>, R. Typed: <see cref="UsbSession.UsbProtocol"/> (enum).</summary>
    UsbProtocol = 0x3FFF01A7,
    /// <summary>VI_ATTR_USB_ALT_SETTING: the interface's alternate setting. <see cref="int"/>, R.</summary>
    UsbAlternateSetting = 0x3FFF01A8,
    /// <summary>VI_ATTR_USB_END_IN. Not supported.</summary>
    UsbEndIn = 0x3FFF01A9,
    /// <summary>VI_ATTR_USB_NUM_INTFCS: the number of interfaces of the device. <see cref="int"/>, R.</summary>
    UsbNumberOfInterfaces = 0x3FFF01AA,
    /// <summary>VI_ATTR_USB_NUM_PIPES: the number of endpoints of the claimed interface. <see cref="int"/>, R.</summary>
    UsbNumberOfPipes = 0x3FFF01AB,
    /// <summary>VI_ATTR_USB_BULK_OUT_STATUS. Not supported.</summary>
    UsbBulkOutStatus = 0x3FFF01AC,
    /// <summary>VI_ATTR_USB_BULK_IN_STATUS. Not supported.</summary>
    UsbBulkInStatus = 0x3FFF01AD,
    /// <summary>VI_ATTR_USB_INTR_IN_STATUS. Not supported.</summary>
    UsbInterruptInStatus = 0x3FFF01AE,
    /// <summary>VI_ATTR_USB_MAX_INTR_SIZE: the size of interrupt-IN reads, in bytes. <see cref="int"/>, R.</summary>
    UsbMaxInterruptSize = 0x3FFF01AF,
    /// <summary>VI_ATTR_USB_CTRL_PIPE: the control endpoint address (always 0). <see cref="int"/>, R.</summary>
    UsbControlPipe = 0x3FFF01B0,

    // PXI (PXI INSTR). The typed properties are on PxiSession.
    /// <summary>VI_ATTR_PXI_DEV_NUM: the PCI device number. <see cref="int"/>, R. Typed: <see cref="PxiSession.DeviceNumber"/>.</summary>
    PxiDeviceNumber = 0x3FFF0201,
    /// <summary>VI_ATTR_PXI_FUNC_NUM: the PCI function number. <see cref="int"/>, R. Typed: <see cref="PxiSession.Function"/>.</summary>
    PxiFunctionNumber = 0x3FFF0202,
    /// <summary>VI_ATTR_PXI_BUS_NUM: the PCI bus number. <see cref="int"/>, R. Typed: <see cref="PxiSession.Bus"/>.</summary>
    PxiBusNumber = 0x3FFF0205,
    /// <summary>VI_ATTR_PXI_CHASSIS: the chassis number; -1 (unknown) because chassis information is not read. <see cref="int"/>, R.</summary>
    PxiChassis = 0x3FFF0206,
    /// <summary>VI_ATTR_PXI_SLOTPATH: the slot path; empty because chassis information is not read. <see cref="string"/>, R.</summary>
    PxiSlotPath = 0xBFFF0207,
    /// <summary>VI_ATTR_PXI_SLOT_LBUS_LEFT. Not supported.</summary>
    PxiSlotLocalBusLeft = 0x3FFF0208,
    /// <summary>VI_ATTR_PXI_SLOT_LBUS_RIGHT. Not supported.</summary>
    PxiSlotLocalBusRight = 0x3FFF0209,
    /// <summary>VI_ATTR_PXI_TRIG_BUS. Not supported.</summary>
    PxiTriggerBus = 0x3FFF020A,
    /// <summary>VI_ATTR_PXI_STAR_TRIG_BUS. Not supported.</summary>
    PxiStarTriggerBus = 0x3FFF020B,
    /// <summary>VI_ATTR_PXI_STAR_TRIG_LINE. Not supported.</summary>
    PxiStarTriggerLine = 0x3FFF020C,
    /// <summary>VI_ATTR_PXI_SRC_TRIG_BUS. Not supported.</summary>
    PxiSourceTriggerBus = 0x3FFF020D,
    /// <summary>VI_ATTR_PXI_DEST_TRIG_BUS. Not supported.</summary>
    PxiDestinationTriggerBus = 0x3FFF020E,
    /// <summary>VI_ATTR_PXI_MEM_TYPE_BAR0: the kind of BAR 0 (0 none, 1 memory, 2 I/O). <see cref="int"/>, R.</summary>
    PxiMemoryTypeBar0 = 0x3FFF0211,
    /// <summary>VI_ATTR_PXI_MEM_TYPE_BAR1; see <see cref="PxiMemoryTypeBar0"/>.</summary>
    PxiMemoryTypeBar1 = 0x3FFF0212,
    /// <summary>VI_ATTR_PXI_MEM_TYPE_BAR2; see <see cref="PxiMemoryTypeBar0"/>.</summary>
    PxiMemoryTypeBar2 = 0x3FFF0213,
    /// <summary>VI_ATTR_PXI_MEM_TYPE_BAR3; see <see cref="PxiMemoryTypeBar0"/>.</summary>
    PxiMemoryTypeBar3 = 0x3FFF0214,
    /// <summary>VI_ATTR_PXI_MEM_TYPE_BAR4; see <see cref="PxiMemoryTypeBar0"/>.</summary>
    PxiMemoryTypeBar4 = 0x3FFF0215,
    /// <summary>VI_ATTR_PXI_MEM_TYPE_BAR5; see <see cref="PxiMemoryTypeBar0"/>.</summary>
    PxiMemoryTypeBar5 = 0x3FFF0216,
    /// <summary>VI_ATTR_PXI_MEM_BASE_BAR0_32: the low 32 bits of BAR 0's bus address. <see cref="int"/>, R.</summary>
    PxiMemoryBaseBar0_32 = 0x3FFF0221,
    /// <summary>VI_ATTR_PXI_MEM_BASE_BAR1_32; see <see cref="PxiMemoryBaseBar0_32"/>.</summary>
    PxiMemoryBaseBar1_32 = 0x3FFF0222,
    /// <summary>VI_ATTR_PXI_MEM_BASE_BAR2_32; see <see cref="PxiMemoryBaseBar0_32"/>.</summary>
    PxiMemoryBaseBar2_32 = 0x3FFF0223,
    /// <summary>VI_ATTR_PXI_MEM_BASE_BAR3_32; see <see cref="PxiMemoryBaseBar0_32"/>.</summary>
    PxiMemoryBaseBar3_32 = 0x3FFF0224,
    /// <summary>VI_ATTR_PXI_MEM_BASE_BAR4_32; see <see cref="PxiMemoryBaseBar0_32"/>.</summary>
    PxiMemoryBaseBar4_32 = 0x3FFF0225,
    /// <summary>VI_ATTR_PXI_MEM_BASE_BAR5_32; see <see cref="PxiMemoryBaseBar0_32"/>.</summary>
    PxiMemoryBaseBar5_32 = 0x3FFF0226,
    /// <summary>VI_ATTR_PXI_MEM_BASE_BAR0 (64-bit form): BAR 0's bus address. <see cref="long"/>, R.</summary>
    PxiMemoryBaseBar0 = 0x3FFF0228,
    /// <summary>VI_ATTR_PXI_MEM_BASE_BAR1; see <see cref="PxiMemoryBaseBar0"/>.</summary>
    PxiMemoryBaseBar1 = 0x3FFF0229,
    /// <summary>VI_ATTR_PXI_MEM_BASE_BAR2; see <see cref="PxiMemoryBaseBar0"/>.</summary>
    PxiMemoryBaseBar2 = 0x3FFF022A,
    /// <summary>VI_ATTR_PXI_MEM_BASE_BAR3; see <see cref="PxiMemoryBaseBar0"/>.</summary>
    PxiMemoryBaseBar3 = 0x3FFF022B,
    /// <summary>VI_ATTR_PXI_MEM_BASE_BAR4; see <see cref="PxiMemoryBaseBar0"/>.</summary>
    PxiMemoryBaseBar4 = 0x3FFF022C,
    /// <summary>VI_ATTR_PXI_MEM_BASE_BAR5; see <see cref="PxiMemoryBaseBar0"/>.</summary>
    PxiMemoryBaseBar5 = 0x3FFF022D,
    /// <summary>VI_ATTR_PXI_MEM_SIZE_BAR0_32: the low 32 bits of BAR 0's size in bytes. <see cref="int"/>, R.</summary>
    PxiMemorySizeBar0_32 = 0x3FFF0231,
    /// <summary>VI_ATTR_PXI_MEM_SIZE_BAR1_32; see <see cref="PxiMemorySizeBar0_32"/>.</summary>
    PxiMemorySizeBar1_32 = 0x3FFF0232,
    /// <summary>VI_ATTR_PXI_MEM_SIZE_BAR2_32; see <see cref="PxiMemorySizeBar0_32"/>.</summary>
    PxiMemorySizeBar2_32 = 0x3FFF0233,
    /// <summary>VI_ATTR_PXI_MEM_SIZE_BAR3_32; see <see cref="PxiMemorySizeBar0_32"/>.</summary>
    PxiMemorySizeBar3_32 = 0x3FFF0234,
    /// <summary>VI_ATTR_PXI_MEM_SIZE_BAR4_32; see <see cref="PxiMemorySizeBar0_32"/>.</summary>
    PxiMemorySizeBar4_32 = 0x3FFF0235,
    /// <summary>VI_ATTR_PXI_MEM_SIZE_BAR5_32; see <see cref="PxiMemorySizeBar0_32"/>.</summary>
    PxiMemorySizeBar5_32 = 0x3FFF0236,
    /// <summary>VI_ATTR_PXI_MEM_SIZE_BAR0 (64-bit form): BAR 0's size in bytes. <see cref="long"/>, R.</summary>
    PxiMemorySizeBar0 = 0x3FFF0238,
    /// <summary>VI_ATTR_PXI_MEM_SIZE_BAR1; see <see cref="PxiMemorySizeBar0"/>.</summary>
    PxiMemorySizeBar1 = 0x3FFF0239,
    /// <summary>VI_ATTR_PXI_MEM_SIZE_BAR2; see <see cref="PxiMemorySizeBar0"/>.</summary>
    PxiMemorySizeBar2 = 0x3FFF023A,
    /// <summary>VI_ATTR_PXI_MEM_SIZE_BAR3; see <see cref="PxiMemorySizeBar0"/>.</summary>
    PxiMemorySizeBar3 = 0x3FFF023B,
    /// <summary>VI_ATTR_PXI_MEM_SIZE_BAR4; see <see cref="PxiMemorySizeBar0"/>.</summary>
    PxiMemorySizeBar4 = 0x3FFF023C,
    /// <summary>VI_ATTR_PXI_MEM_SIZE_BAR5; see <see cref="PxiMemorySizeBar0"/>.</summary>
    PxiMemorySizeBar5 = 0x3FFF023D,
    /// <summary>VI_ATTR_PXI_IS_EXPRESS: whether the device is PCI Express. <see cref="bool"/>, R.</summary>
    PxiIsExpress = 0x3FFF0240,
    /// <summary>VI_ATTR_PXI_SLOT_LWIDTH. Not supported.</summary>
    PxiSlotLinkWidth = 0x3FFF0241,
    /// <summary>VI_ATTR_PXI_MAX_LWIDTH: the device's maximum PCIe link width, -1 if unknown. <see cref="int"/>, R.</summary>
    PxiMaxLinkWidth = 0x3FFF0242,
    /// <summary>VI_ATTR_PXI_ACTUAL_LWIDTH: the negotiated PCIe link width, -1 if unknown. <see cref="int"/>, R.</summary>
    PxiActualLinkWidth = 0x3FFF0243,
    /// <summary>VI_ATTR_PXI_DSTAR_BUS. Not supported.</summary>
    PxiDStarBus = 0x3FFF0244,
    /// <summary>VI_ATTR_PXI_DSTAR_SET. Not supported.</summary>
    PxiDStarSet = 0x3FFF0245,
    /// <summary>VI_ATTR_PXI_ALLOW_WRITE_COMBINE. Not supported.</summary>
    PxiAllowWriteCombine = 0x3FFF0246,

    // Event attributes: defined by visa.h for event objects. Here the event data are properties of
    // VisaEventArgs and its subclasses, so no session supports these.
    /// <summary>VI_ATTR_JOB_ID. Not supported (no asynchronous VISA jobs).</summary>
    JobId = 0x3FFF4006,
    /// <summary>VI_ATTR_EVENT_TYPE. Not a session attribute: see <see cref="VisaEventArgs.EventType"/>.</summary>
    EventType = 0x3FFF4010,
    /// <summary>VI_ATTR_STATUS. Not a session attribute: see <see cref="UsbInterruptEventArgs.Status"/>.</summary>
    Status = 0x3FFF4025,
    /// <summary>VI_ATTR_RET_COUNT (64-bit form). Not supported (no asynchronous VISA jobs).</summary>
    ReturnCount = 0x3FFF4028,
    /// <summary>VI_ATTR_RET_COUNT_32. Not supported (no asynchronous VISA jobs).</summary>
    ReturnCount32 = 0x3FFF4026,
    /// <summary>VI_ATTR_BUFFER. Not supported (no asynchronous VISA jobs).</summary>
    Buffer = 0x3FFF4027,
    /// <summary>VI_ATTR_OPER_NAME. Not supported (no asynchronous VISA jobs).</summary>
    OperationName = 0xBFFF4042,
    /// <summary>VI_ATTR_RECV_TCPIP_ADDR. Not supported (no TCP/IP server sessions).</summary>
    ReceivedTcpipAddress = 0xBFFF4198,
    /// <summary>VI_ATTR_USB_RECV_INTR_SIZE. Not a session attribute: see <see cref="UsbInterruptEventArgs.Data"/>.</summary>
    UsbReceivedInterruptSize = 0x3FFF41B0,
    /// <summary>VI_ATTR_USB_RECV_INTR_DATA. Not a session attribute: see <see cref="UsbInterruptEventArgs.Data"/>.</summary>
    UsbReceivedInterruptData = 0xBFFF41B1,
}

/// <summary>Maps <see cref="VisaAttribute"/> values to and from their visa.h names.</summary>
public static class VisaAttributeNames
{
    private static readonly Dictionary<VisaAttribute, string> Names = new()
    {
        [VisaAttribute.ResourceClass] = "VI_ATTR_RSRC_CLASS",
        [VisaAttribute.ResourceName] = "VI_ATTR_RSRC_NAME",
        [VisaAttribute.ResourceImplementationVersion] = "VI_ATTR_RSRC_IMPL_VERSION",
        [VisaAttribute.ResourceLockState] = "VI_ATTR_RSRC_LOCK_STATE",
        [VisaAttribute.MaxQueueLength] = "VI_ATTR_MAX_QUEUE_LENGTH",
        [VisaAttribute.UserData] = "VI_ATTR_USER_DATA",
        [VisaAttribute.UserData32] = "VI_ATTR_USER_DATA_32",
        [VisaAttribute.ResourceSpecVersion] = "VI_ATTR_RSRC_SPEC_VERSION",
        [VisaAttribute.InterfaceType] = "VI_ATTR_INTF_TYPE",
        [VisaAttribute.ResourceManufacturerName] = "VI_ATTR_RSRC_MANF_NAME",
        [VisaAttribute.ResourceManufacturerId] = "VI_ATTR_RSRC_MANF_ID",
        [VisaAttribute.InterfaceNumber] = "VI_ATTR_INTF_NUM",
        [VisaAttribute.TriggerId] = "VI_ATTR_TRIG_ID",
        [VisaAttribute.InterfaceInstanceName] = "VI_ATTR_INTF_INST_NAME",
        [VisaAttribute.ManufacturerName] = "VI_ATTR_MANF_NAME",
        [VisaAttribute.ModelName] = "VI_ATTR_MODEL_NAME",
        [VisaAttribute.ManufacturerId] = "VI_ATTR_MANF_ID",
        [VisaAttribute.ModelCode] = "VI_ATTR_MODEL_CODE",
        [VisaAttribute.Slot] = "VI_ATTR_SLOT",
        [VisaAttribute.SendEndEnabled] = "VI_ATTR_SEND_END_EN",
        [VisaAttribute.TerminationCharacter] = "VI_ATTR_TERMCHAR",
        [VisaAttribute.TimeoutValue] = "VI_ATTR_TMO_VALUE",
        [VisaAttribute.IOProtocol] = "VI_ATTR_IO_PROT",
        [VisaAttribute.DmaAllowEnabled] = "VI_ATTR_DMA_ALLOW_EN",
        [VisaAttribute.ReadBufferOperationMode] = "VI_ATTR_RD_BUF_OPER_MODE",
        [VisaAttribute.ReadBufferSize] = "VI_ATTR_RD_BUF_SIZE",
        [VisaAttribute.WriteBufferOperationMode] = "VI_ATTR_WR_BUF_OPER_MODE",
        [VisaAttribute.WriteBufferSize] = "VI_ATTR_WR_BUF_SIZE",
        [VisaAttribute.SuppressEndEnabled] = "VI_ATTR_SUPPRESS_END_EN",
        [VisaAttribute.TerminationCharacterEnabled] = "VI_ATTR_TERMCHAR_EN",
        [VisaAttribute.FileAppendEnabled] = "VI_ATTR_FILE_APPEND_EN",
        [VisaAttribute.Is4882Compliant] = "VI_ATTR_4882_COMPLIANT",
        [VisaAttribute.DestinationAccessPrivilege] = "VI_ATTR_DEST_ACCESS_PRIV",
        [VisaAttribute.DestinationByteOrder] = "VI_ATTR_DEST_BYTE_ORDER",
        [VisaAttribute.SourceAccessPrivilege] = "VI_ATTR_SRC_ACCESS_PRIV",
        [VisaAttribute.SourceByteOrder] = "VI_ATTR_SRC_BYTE_ORDER",
        [VisaAttribute.SourceIncrement] = "VI_ATTR_SRC_INCREMENT",
        [VisaAttribute.DestinationIncrement] = "VI_ATTR_DEST_INCREMENT",
        [VisaAttribute.WindowAccessPrivilege] = "VI_ATTR_WIN_ACCESS_PRIV",
        [VisaAttribute.WindowByteOrder] = "VI_ATTR_WIN_BYTE_ORDER",
        [VisaAttribute.WindowBaseAddress] = "VI_ATTR_WIN_BASE_ADDR",
        [VisaAttribute.WindowBaseAddress32] = "VI_ATTR_WIN_BASE_ADDR_32",
        [VisaAttribute.WindowSize] = "VI_ATTR_WIN_SIZE",
        [VisaAttribute.WindowSize32] = "VI_ATTR_WIN_SIZE_32",
        [VisaAttribute.WindowAccess] = "VI_ATTR_WIN_ACCESS",
        [VisaAttribute.MemoryBase] = "VI_ATTR_MEM_BASE",
        [VisaAttribute.MemoryBase32] = "VI_ATTR_MEM_BASE_32",
        [VisaAttribute.MemorySize] = "VI_ATTR_MEM_SIZE",
        [VisaAttribute.MemorySize32] = "VI_ATTR_MEM_SIZE_32",
        [VisaAttribute.MemorySpace] = "VI_ATTR_MEM_SPACE",
        [VisaAttribute.GpibReaddressingEnabled] = "VI_ATTR_GPIB_READDR_EN",
        [VisaAttribute.GpibAtnState] = "VI_ATTR_GPIB_ATN_STATE",
        [VisaAttribute.GpibAddressState] = "VI_ATTR_GPIB_ADDR_STATE",
        [VisaAttribute.GpibCicState] = "VI_ATTR_GPIB_CIC_STATE",
        [VisaAttribute.GpibNdacState] = "VI_ATTR_GPIB_NDAC_STATE",
        [VisaAttribute.GpibSrqState] = "VI_ATTR_GPIB_SRQ_STATE",
        [VisaAttribute.GpibSystemControllerState] = "VI_ATTR_GPIB_SYS_CNTRL_STATE",
        [VisaAttribute.GpibHS488CableLength] = "VI_ATTR_GPIB_HS488_CBL_LEN",
        [VisaAttribute.GpibPrimaryAddress] = "VI_ATTR_GPIB_PRIMARY_ADDR",
        [VisaAttribute.GpibSecondaryAddress] = "VI_ATTR_GPIB_SECONDARY_ADDR",
        [VisaAttribute.GpibRenState] = "VI_ATTR_GPIB_REN_STATE",
        [VisaAttribute.GpibUnaddressingEnabled] = "VI_ATTR_GPIB_UNADDR_EN",
        [VisaAttribute.DeviceStatusByte] = "VI_ATTR_DEV_STATUS_BYTE",
        [VisaAttribute.SerialBaudRate] = "VI_ATTR_ASRL_BAUD",
        [VisaAttribute.SerialDataBits] = "VI_ATTR_ASRL_DATA_BITS",
        [VisaAttribute.SerialParity] = "VI_ATTR_ASRL_PARITY",
        [VisaAttribute.SerialStopBits] = "VI_ATTR_ASRL_STOP_BITS",
        [VisaAttribute.SerialFlowControl] = "VI_ATTR_ASRL_FLOW_CNTRL",
        [VisaAttribute.SerialAvailableNumber] = "VI_ATTR_ASRL_AVAIL_NUM",
        [VisaAttribute.SerialCtsState] = "VI_ATTR_ASRL_CTS_STATE",
        [VisaAttribute.SerialDcdState] = "VI_ATTR_ASRL_DCD_STATE",
        [VisaAttribute.SerialDiscardNull] = "VI_ATTR_ASRL_DISCARD_NULL",
        [VisaAttribute.SerialDsrState] = "VI_ATTR_ASRL_DSR_STATE",
        [VisaAttribute.SerialDtrState] = "VI_ATTR_ASRL_DTR_STATE",
        [VisaAttribute.SerialEndIn] = "VI_ATTR_ASRL_END_IN",
        [VisaAttribute.SerialEndOut] = "VI_ATTR_ASRL_END_OUT",
        [VisaAttribute.SerialReplaceCharacter] = "VI_ATTR_ASRL_REPLACE_CHAR",
        [VisaAttribute.SerialRiState] = "VI_ATTR_ASRL_RI_STATE",
        [VisaAttribute.SerialRtsState] = "VI_ATTR_ASRL_RTS_STATE",
        [VisaAttribute.SerialXOnCharacter] = "VI_ATTR_ASRL_XON_CHAR",
        [VisaAttribute.SerialXOffCharacter] = "VI_ATTR_ASRL_XOFF_CHAR",
        [VisaAttribute.SerialConnected] = "VI_ATTR_ASRL_CONNECTED",
        [VisaAttribute.SerialBreakState] = "VI_ATTR_ASRL_BREAK_STATE",
        [VisaAttribute.SerialBreakLength] = "VI_ATTR_ASRL_BREAK_LEN",
        [VisaAttribute.SerialAllowTransmit] = "VI_ATTR_ASRL_ALLOW_TRANSMIT",
        [VisaAttribute.SerialWireMode] = "VI_ATTR_ASRL_WIRE_MODE",
        [VisaAttribute.TcpipAddress] = "VI_ATTR_TCPIP_ADDR",
        [VisaAttribute.TcpipHostName] = "VI_ATTR_TCPIP_HOSTNAME",
        [VisaAttribute.TcpipPort] = "VI_ATTR_TCPIP_PORT",
        [VisaAttribute.TcpipDeviceName] = "VI_ATTR_TCPIP_DEVICE_NAME",
        [VisaAttribute.TcpipNoDelay] = "VI_ATTR_TCPIP_NODELAY",
        [VisaAttribute.TcpipKeepAlive] = "VI_ATTR_TCPIP_KEEPALIVE",
        [VisaAttribute.TcpipServerCertIssuerName] = "VI_ATTR_TCPIP_SERVER_CERT_ISSUER_NAME",
        [VisaAttribute.TcpipServerCertSubjectName] = "VI_ATTR_TCPIP_SERVER_CERT_SUBJECT_NAME",
        [VisaAttribute.TcpipServerCertExpirationDate] = "VI_ATTR_TCPIP_SERVER_CERT_EXPIRATION_DATE",
        [VisaAttribute.TcpipServerCertIsPerpetual] = "VI_ATTR_TCPIP_SERVER_CERT_IS_PERPETUAL",
        [VisaAttribute.TcpipSaslMechanism] = "VI_ATTR_TCPIP_SASL_MECHANISM",
        [VisaAttribute.TcpipTlsCipherSuite] = "VI_ATTR_TCPIP_TLS_CIPHER_SUITE",
        [VisaAttribute.TcpipHislipOverlapEnabled] = "VI_ATTR_TCPIP_HISLIP_OVERLAP_EN",
        [VisaAttribute.TcpipHislipVersion] = "VI_ATTR_TCPIP_HISLIP_VERSION",
        [VisaAttribute.TcpipHislipMaxMessageKB] = "VI_ATTR_TCPIP_HISLIP_MAX_MESSAGE_KB",
        [VisaAttribute.TcpipIsHislip] = "VI_ATTR_TCPIP_IS_HISLIP",
        [VisaAttribute.TcpipHislipEncryptionEnabled] = "VI_ATTR_TCPIP_HISLIP_ENCRYPTION_EN",
        [VisaAttribute.UsbSerialNumber] = "VI_ATTR_USB_SERIAL_NUM",
        [VisaAttribute.UsbInterfaceNumber] = "VI_ATTR_USB_INTFC_NUM",
        [VisaAttribute.UsbBulkOutPipe] = "VI_ATTR_USB_BULK_OUT_PIPE",
        [VisaAttribute.UsbBulkInPipe] = "VI_ATTR_USB_BULK_IN_PIPE",
        [VisaAttribute.UsbInterruptInPipe] = "VI_ATTR_USB_INTR_IN_PIPE",
        [VisaAttribute.UsbClass] = "VI_ATTR_USB_CLASS",
        [VisaAttribute.UsbSubclass] = "VI_ATTR_USB_SUBCLASS",
        [VisaAttribute.UsbProtocol] = "VI_ATTR_USB_PROTOCOL",
        [VisaAttribute.UsbAlternateSetting] = "VI_ATTR_USB_ALT_SETTING",
        [VisaAttribute.UsbEndIn] = "VI_ATTR_USB_END_IN",
        [VisaAttribute.UsbNumberOfInterfaces] = "VI_ATTR_USB_NUM_INTFCS",
        [VisaAttribute.UsbNumberOfPipes] = "VI_ATTR_USB_NUM_PIPES",
        [VisaAttribute.UsbBulkOutStatus] = "VI_ATTR_USB_BULK_OUT_STATUS",
        [VisaAttribute.UsbBulkInStatus] = "VI_ATTR_USB_BULK_IN_STATUS",
        [VisaAttribute.UsbInterruptInStatus] = "VI_ATTR_USB_INTR_IN_STATUS",
        [VisaAttribute.UsbMaxInterruptSize] = "VI_ATTR_USB_MAX_INTR_SIZE",
        [VisaAttribute.UsbControlPipe] = "VI_ATTR_USB_CTRL_PIPE",
        [VisaAttribute.PxiDeviceNumber] = "VI_ATTR_PXI_DEV_NUM",
        [VisaAttribute.PxiFunctionNumber] = "VI_ATTR_PXI_FUNC_NUM",
        [VisaAttribute.PxiBusNumber] = "VI_ATTR_PXI_BUS_NUM",
        [VisaAttribute.PxiChassis] = "VI_ATTR_PXI_CHASSIS",
        [VisaAttribute.PxiSlotPath] = "VI_ATTR_PXI_SLOTPATH",
        [VisaAttribute.PxiSlotLocalBusLeft] = "VI_ATTR_PXI_SLOT_LBUS_LEFT",
        [VisaAttribute.PxiSlotLocalBusRight] = "VI_ATTR_PXI_SLOT_LBUS_RIGHT",
        [VisaAttribute.PxiTriggerBus] = "VI_ATTR_PXI_TRIG_BUS",
        [VisaAttribute.PxiStarTriggerBus] = "VI_ATTR_PXI_STAR_TRIG_BUS",
        [VisaAttribute.PxiStarTriggerLine] = "VI_ATTR_PXI_STAR_TRIG_LINE",
        [VisaAttribute.PxiSourceTriggerBus] = "VI_ATTR_PXI_SRC_TRIG_BUS",
        [VisaAttribute.PxiDestinationTriggerBus] = "VI_ATTR_PXI_DEST_TRIG_BUS",
        [VisaAttribute.PxiIsExpress] = "VI_ATTR_PXI_IS_EXPRESS",
        [VisaAttribute.PxiSlotLinkWidth] = "VI_ATTR_PXI_SLOT_LWIDTH",
        [VisaAttribute.PxiMaxLinkWidth] = "VI_ATTR_PXI_MAX_LWIDTH",
        [VisaAttribute.PxiActualLinkWidth] = "VI_ATTR_PXI_ACTUAL_LWIDTH",
        [VisaAttribute.PxiDStarBus] = "VI_ATTR_PXI_DSTAR_BUS",
        [VisaAttribute.PxiDStarSet] = "VI_ATTR_PXI_DSTAR_SET",
        [VisaAttribute.PxiAllowWriteCombine] = "VI_ATTR_PXI_ALLOW_WRITE_COMBINE",
        [VisaAttribute.JobId] = "VI_ATTR_JOB_ID",
        [VisaAttribute.EventType] = "VI_ATTR_EVENT_TYPE",
        [VisaAttribute.Status] = "VI_ATTR_STATUS",
        [VisaAttribute.ReturnCount] = "VI_ATTR_RET_COUNT",
        [VisaAttribute.ReturnCount32] = "VI_ATTR_RET_COUNT_32",
        [VisaAttribute.Buffer] = "VI_ATTR_BUFFER",
        [VisaAttribute.OperationName] = "VI_ATTR_OPER_NAME",
        [VisaAttribute.ReceivedTcpipAddress] = "VI_ATTR_RECV_TCPIP_ADDR",
        [VisaAttribute.UsbReceivedInterruptSize] = "VI_ATTR_USB_RECV_INTR_SIZE",
        [VisaAttribute.UsbReceivedInterruptData] = "VI_ATTR_USB_RECV_INTR_DATA",
    };

    private static readonly Dictionary<string, VisaAttribute> ByName = BuildByName();

    private static Dictionary<string, VisaAttribute> BuildByName()
    {
        var map = Names.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);
        // Per-BAR PXI attributes follow a regular numbering.
        for (int bar = 0; bar < 6; bar++)
        {
            map[$"VI_ATTR_PXI_MEM_TYPE_BAR{bar}"] = VisaAttribute.PxiMemoryTypeBar0 + (uint)bar;
            map[$"VI_ATTR_PXI_MEM_BASE_BAR{bar}"] = VisaAttribute.PxiMemoryBaseBar0 + (uint)bar;
            map[$"VI_ATTR_PXI_MEM_BASE_BAR{bar}_64"] = VisaAttribute.PxiMemoryBaseBar0 + (uint)bar;
            map[$"VI_ATTR_PXI_MEM_BASE_BAR{bar}_32"] = VisaAttribute.PxiMemoryBaseBar0_32 + (uint)bar;
            map[$"VI_ATTR_PXI_MEM_SIZE_BAR{bar}"] = VisaAttribute.PxiMemorySizeBar0 + (uint)bar;
            map[$"VI_ATTR_PXI_MEM_SIZE_BAR{bar}_64"] = VisaAttribute.PxiMemorySizeBar0 + (uint)bar;
            map[$"VI_ATTR_PXI_MEM_SIZE_BAR{bar}_32"] = VisaAttribute.PxiMemorySizeBar0_32 + (uint)bar;
        }
        map["VI_ATTR_USER_DATA_64"] = VisaAttribute.UserData;
        map["VI_ATTR_WIN_BASE_ADDR_64"] = VisaAttribute.WindowBaseAddress;
        map["VI_ATTR_WIN_SIZE_64"] = VisaAttribute.WindowSize;
        map["VI_ATTR_MEM_BASE_64"] = VisaAttribute.MemoryBase;
        map["VI_ATTR_MEM_SIZE_64"] = VisaAttribute.MemorySize;
        map["VI_ATTR_RET_COUNT_64"] = VisaAttribute.ReturnCount;
        return map;
    }

    /// <summary>Gets the visa.h name, e.g. <c>VI_ATTR_TMO_VALUE</c>.</summary>
    public static string GetName(VisaAttribute attribute)
    {
        if (Names.TryGetValue(attribute, out var n)) return n;
        uint value = (uint)attribute;
        if (value is >= 0x3FFF0211 and <= 0x3FFF0216) return $"VI_ATTR_PXI_MEM_TYPE_BAR{value - 0x3FFF0211}";
        if (value is >= 0x3FFF0221 and <= 0x3FFF0226) return $"VI_ATTR_PXI_MEM_BASE_BAR{value - 0x3FFF0221}_32";
        if (value is >= 0x3FFF0228 and <= 0x3FFF022D) return $"VI_ATTR_PXI_MEM_BASE_BAR{value - 0x3FFF0228}";
        if (value is >= 0x3FFF0231 and <= 0x3FFF0236) return $"VI_ATTR_PXI_MEM_SIZE_BAR{value - 0x3FFF0231}_32";
        if (value is >= 0x3FFF0238 and <= 0x3FFF023D) return $"VI_ATTR_PXI_MEM_SIZE_BAR{value - 0x3FFF0238}";
        return $"0x{value:X8}";
    }

    /// <summary>Looks up an attribute by its visa.h name (case-insensitive).</summary>
    public static bool TryParse(string name, out VisaAttribute attribute) => ByName.TryGetValue(name, out attribute);
}

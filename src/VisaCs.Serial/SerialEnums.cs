namespace VisaCs;

// These enums are part of the VISA session API (SerialSession), so they live in namespace VisaCs although they are
// defined in the serial library: code that uses NativeSerialPort alone needs both 'using VisaCs;' and
// 'using VisaCs.Serial;'.

/// <summary>Serial parity (VI_ATTR_ASRL_PARITY).</summary>
public enum SerialParity
{
    /// <summary>No parity bit.</summary>
    None = 0,
    /// <summary>Odd parity.</summary>
    Odd = 1,
    /// <summary>Even parity.</summary>
    Even = 2,
    /// <summary>Parity bit always 1. Not supported on macOS.</summary>
    Mark = 3,
    /// <summary>Parity bit always 0. Not supported on macOS.</summary>
    Space = 4,
}

/// <summary>Serial stop bits (VI_ATTR_ASRL_STOP_BITS). Values are tenths of a bit, as in VISA.</summary>
public enum SerialStopBitsMode
{
    /// <summary>One stop bit.</summary>
    One = 10,
    /// <summary>
    /// One and a half stop bits: Windows only, and only with 5 data bits. Linux and macOS have no such setting and
    /// send two stop bits.
    /// </summary>
    OneAndOneHalf = 15,
    /// <summary>Two stop bits.</summary>
    Two = 20,
}

/// <summary>Serial flow control (VI_ATTR_ASRL_FLOW_CNTRL). The flags can be combined.</summary>
[Flags]
public enum SerialFlowControlModes
{
    /// <summary>No flow control.</summary>
    None = 0,
    /// <summary>Software flow control with the XON and XOFF characters, in both directions.</summary>
    XOnXOff = 1,
    /// <summary>Hardware flow control with RTS/CTS. The driver then controls the RTS line.</summary>
    RtsCts = 2,
    /// <summary>Hardware flow control with DTR/DSR (Windows only). The driver then controls the DTR line.</summary>
    DtrDsr = 4,
}

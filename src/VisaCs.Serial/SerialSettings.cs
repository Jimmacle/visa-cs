namespace VisaCs.Serial;

/// <summary>Line settings applied to a serial port with <see cref="NativeSerialPort.Configure"/>.</summary>
/// <remarks>
/// <see cref="NativeSerialPort.Configure"/> throws <see cref="ArgumentOutOfRangeException"/> for values outside the
/// documented ranges, and <see cref="SerialPortException"/> with <see cref="SerialPortError.UnsupportedSetting"/>
/// for a combination the platform or the port does not support.
/// </remarks>
/// <param name="BaudRate">
/// Bits per second, at least 1. Any rate the port accepts; non-standard rates work on Linux (termios2) and, where the
/// driver supports them, on macOS and Windows.
/// </param>
/// <param name="DataBits">Data bits per character, 5 to 8.</param>
/// <param name="Parity">Parity bit. Mark and space parity are not supported on macOS.</param>
/// <param name="StopBits">Stop bits. 1.5 stop bits exist only on Windows (with 5 data bits); Linux and macOS send two.</param>
/// <param name="FlowControl">Flow control; the flags can be combined. DTR/DSR flow control is Windows only.</param>
/// <param name="XOnCharacter">Character that resumes transmission under XON/XOFF flow control (default 0x11).</param>
/// <param name="XOffCharacter">Character that suspends transmission under XON/XOFF flow control (default 0x13).</param>
/// <param name="ReplacementCharacter">
/// Windows only: replaces bytes received with a parity error; 0 keeps them as received. Linux and macOS always read
/// a byte received with a parity or framing error as NUL (0x00) and reject a non-zero value.
/// </param>
/// <param name="DiscardNull">Drop received NUL bytes (in the driver on Windows, while reading on Linux and macOS).</param>
public readonly record struct SerialSettings(
    int BaudRate,
    int DataBits,
    SerialParity Parity,
    SerialStopBitsMode StopBits,
    SerialFlowControlModes FlowControl,
    byte XOnCharacter,
    byte XOffCharacter,
    byte ReplacementCharacter,
    bool DiscardNull)
{
    /// <summary>9600 baud, 8 data bits, no parity, one stop bit, no flow control (the VISA defaults).</summary>
    public static SerialSettings Default =>
        new(9600, 8, SerialParity.None, SerialStopBitsMode.One, SerialFlowControlModes.None, 0x11, 0x13, 0, false);

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for a value outside the documented ranges.</summary>
    internal void Validate()
    {
        if (BaudRate < 1)
            throw new ArgumentOutOfRangeException(nameof(BaudRate), BaudRate, "The baud rate must be at least 1.");
        if (DataBits is < 5 or > 8)
            throw new ArgumentOutOfRangeException(nameof(DataBits), DataBits, "Data bits must be between 5 and 8.");
        if (!Enum.IsDefined(Parity))
            throw new ArgumentOutOfRangeException(nameof(Parity), Parity, "Unknown parity.");
        if (!Enum.IsDefined(StopBits))
            throw new ArgumentOutOfRangeException(nameof(StopBits), StopBits, "Unknown stop bits value.");
        const SerialFlowControlModes all = SerialFlowControlModes.XOnXOff | SerialFlowControlModes.RtsCts | SerialFlowControlModes.DtrDsr;
        if ((FlowControl & ~all) != 0)
            throw new ArgumentOutOfRangeException(nameof(FlowControl), FlowControl, "Unknown flow control flags.");
    }
}

namespace VisaCs.Serial;

/// <summary>What went wrong in a <see cref="SerialPortException"/>.</summary>
public enum SerialPortError
{
    /// <summary>An I/O error not covered by the other values: a read, write, ioctl or Win32 comm call failed.</summary>
    IOError,
    /// <summary>The port does not exist, or the name is not a serial port.</summary>
    NotFound,
    /// <summary>
    /// The port is open in this or another process. On Windows also when access to it is denied: Windows reports
    /// both the same way.
    /// </summary>
    InUse,
    /// <summary>The user may not open the device (Linux and macOS; on most Linux distributions the user must be in the 'dialout' or 'uucp' group).</summary>
    AccessDenied,
    /// <summary>The device is gone, for example a USB serial adapter was unplugged.</summary>
    Disconnected,
    /// <summary>
    /// <see cref="NativeSerialPort.Abort"/> was called from another thread during the operation, or (Windows) a write
    /// was ended by <see cref="NativeSerialPort.DiscardOutput"/>.
    /// </summary>
    Aborted,
    /// <summary>The port was disposed during the operation.</summary>
    Closed,
    /// <summary>The platform or the port does not support a setting or combination of settings, or a modem line cannot be set now.</summary>
    UnsupportedSetting,
    /// <summary>An operating-system call that should not fail did (creating an event, a pipe, a poll).</summary>
    SystemError,
}

/// <summary>A serial port operation failed because of the device, the driver or the operating system.</summary>
public class SerialPortException : IOException
{
    /// <summary>Creates the exception.</summary>
    public SerialPortException(SerialPortError error, string message, int? nativeError = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
        NativeError = nativeError;
    }

    /// <summary>What went wrong.</summary>
    public SerialPortError Error { get; }

    /// <summary>The errno (Linux, macOS) or Win32 error code, when known.</summary>
    public int? NativeError { get; }
}

/// <summary>A serial read, write or drain did not finish in time.</summary>
public class SerialTimeoutException : TimeoutException
{
    /// <summary>Creates the exception.</summary>
    public SerialTimeoutException(string? message, Exception? innerException = null) : base(message, innerException)
    {
    }

    /// <summary>Number of bytes written before the time ran out (0 for reads and drains).</summary>
    public int BytesTransferred { get; init; }
}

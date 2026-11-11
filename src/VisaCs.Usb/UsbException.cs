namespace VisaCs.Usb;

/// <summary>What went wrong in a <see cref="UsbException"/>.</summary>
public enum UsbError
{
    /// <summary>A transfer or request failed for a reason not covered by another value (a transfer error, an overflow, ...).</summary>
    IOError,

    /// <summary>An endpoint stalled; <see cref="IUsbDeviceHandle.ClearHalt"/> clears it.</summary>
    Stall,

    /// <summary>The device answered with a failure status or a malformed message (for example a USBTMC response with the wrong bTag).</summary>
    ProtocolError,

    /// <summary>The device or interface does not exist (any more), or the interface cannot be reached through this backend.</summary>
    NotFound,

    /// <summary>The interface is claimed by another handle or program.</summary>
    Busy,

    /// <summary>The operating system denied access to the device.</summary>
    AccessDenied,

    /// <summary>The device was unplugged (or its connection broke) during the operation.</summary>
    Disconnected,

    /// <summary>The device or interface does not support the operation (for example a USB488 request on a plain USBTMC interface).</summary>
    NotSupported,

    /// <summary>The native USB library (libusb-1.0) could not be loaded or initialized.</summary>
    LibraryNotFound,

    /// <summary>The native USB library could not allocate memory for a transfer.</summary>
    OutOfMemory,

    /// <summary>The operating system aborted the transfer (not because of a timeout, cancellation or disposal).</summary>
    Aborted,

    /// <summary>An operating system call failed unexpectedly; <see cref="UsbException.NativeError"/> holds its error code.</summary>
    SystemError,
}

/// <summary>A USB device, driver, operating system or USBTMC protocol failure.</summary>
/// <param name="error">What went wrong.</param>
/// <param name="message">The message; null for a default one.</param>
/// <param name="innerException">The underlying exception, if any.</param>
/// <remarks>
/// Timeouts throw <see cref="UsbTimeoutException"/>, cancellation by the caller <see cref="OperationCanceledException"/>,
/// and using a disposed handle <see cref="ObjectDisposedException"/>.
/// </remarks>
public class UsbException(UsbError error, string? message = null, Exception? innerException = null)
    : IOException(message ?? $"USB error: {error}.", innerException)
{
    /// <summary>What went wrong.</summary>
    public UsbError Error { get; } = error;

    /// <summary>The operating system or driver error code (Win32 error, errno or libusb error), when known.</summary>
    public int? NativeError { get; init; }
}

/// <summary>A USB transfer or USBTMC operation did not complete within its timeout.</summary>
/// <param name="message">The message; null for a default one.</param>
/// <param name="innerException">The underlying exception, if any.</param>
public class UsbTimeoutException(string? message = null, Exception? innerException = null)
    : TimeoutException(message ?? "The USB operation timed out.", innerException)
{
    /// <summary>
    /// Bytes transferred before the timeout. A timed-out transfer is cancelled and its data discarded, so this is 0
    /// for the built-in backends.
    /// </summary>
    public int BytesTransferred { get; init; }
}

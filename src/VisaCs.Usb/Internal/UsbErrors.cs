using VisaCs.Internal;

namespace VisaCs.Usb;

/// <summary>The exceptions this library throws for device, driver, operating system and protocol failures.</summary>
internal static class UsbErrors
{
    public static UsbException Create(UsbError error, string message, int? nativeError = null) =>
        new(error, message) { NativeError = nativeError };

    public static UsbException NotFound(string message, int? nativeError = null) => Create(UsbError.NotFound, message, nativeError);

    public static UsbException Busy(string message, int? nativeError = null) => Create(UsbError.Busy, message, nativeError);

    public static UsbException AccessDenied(string message, int? nativeError = null) => Create(UsbError.AccessDenied, message, nativeError);

    public static UsbException SystemError(string message, int? nativeError = null) => Create(UsbError.SystemError, message, nativeError);

    /// <summary>A device enumerated by another backend was passed to <see cref="IUsbBackend.Open"/>.</summary>
    public static ArgumentException ForeignDevice(string backend) =>
        new($"The device was not enumerated by the {backend} backend.", "device");

    // ------------------------------------------------------------------ transfers

    public static UsbTimeoutException TimedOut(string operation) => new($"USB {operation} timed out.");

    public static UsbException Disconnected(string operation, int? nativeError = null) =>
        Create(UsbError.Disconnected, $"USB device disconnected during {operation}.", nativeError);

    public static UsbException Stalled(string operation, int? nativeError = null) =>
        Create(UsbError.Stall, $"USB endpoint stalled during {operation}.", nativeError);

    public static UsbException Failed(string operation, string detail, int? nativeError = null) =>
        Create(UsbError.IOError, $"USB {operation} failed ({detail}).", nativeError);

    public static ObjectDisposedException Disposed() =>
        new(nameof(IUsbDeviceHandle), "The USB device handle was disposed during the transfer.");

    /// <summary>
    /// The exception for a transfer that the backend cancelled before it completed: the caller's token, the handle
    /// being disposed, or else the transfer's own timeout. Data that an IN transfer had received is lost.
    /// </summary>
    public static Exception Interrupted(string operation, CancellationToken cancellationToken, bool disposed)
    {
        if (cancellationToken.IsCancellationRequested) return AsyncIO.Canceled(TimedOut(operation), cancellationToken);
        if (disposed) return Disposed();
        return TimedOut(operation);
    }

    // ------------------------------------------------------------------ USBTMC client

    /// <summary>The device answered with a failure status or an invalid message.</summary>
    public static UsbException Protocol(string message) => Create(UsbError.ProtocolError, message);

    /// <summary>The operation ran out of time.</summary>
    public static UsbTimeoutException Timeout(string? message) => new(message);

    /// <summary>The interface lacks what the operation needs.</summary>
    public static UsbException NotSupported(string operation) =>
        Create(UsbError.NotSupported, $"{operation} is not supported by this device.");
}

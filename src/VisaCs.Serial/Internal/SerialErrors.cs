using VisaCs.Serial;

namespace VisaCs.Internal;

/// <summary>Creates every exception this library throws for device, driver and OS failures and timeouts.</summary>
internal static class SerialErrors
{
    public static SerialPortException Create(SerialPortError error, string message, int? nativeError = null, Exception? innerException = null) =>
        new(error, message, nativeError, innerException);

    public static SerialTimeoutException Timeout(string? message, int bytesTransferred = 0, Exception? innerException = null) =>
        new(message, innerException) { BytesTransferred = bytesTransferred };
}

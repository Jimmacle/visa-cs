using VisaCs.Serial;

namespace VisaCs.Internal;

// This library's exceptions for the shared sources in src/Shared (see src/Shared/SharedErrors.cs).
internal static partial class SharedErrors
{
    public static partial Exception Timeout(string? message, Exception? innerException, int bytesTransferred) =>
        SerialErrors.Timeout(message, bytesTransferred, innerException);

    public static partial Exception ConnectionLost(string message, Exception? innerException) =>
        SerialErrors.Create(SerialPortError.Disconnected, message, null, innerException);

    public static partial Exception NotFound(string message, Exception? innerException) =>
        SerialErrors.Create(SerialPortError.NotFound, message, null, innerException);

    public static partial Exception SystemError(string message, int errno) =>
        SerialErrors.Create(SerialPortError.SystemError, message, errno);

    public static partial bool IsTimeout(Exception ex) => ex is SerialTimeoutException;
}

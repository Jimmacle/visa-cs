using VisaCs.Lan;

namespace VisaCs.Internal;

// This library's exceptions for the shared sources in src/Shared (see src/Shared/SharedErrors.cs).
internal static partial class SharedErrors
{
    public static partial Exception Timeout(string? message, Exception? innerException, int bytesTransferred) =>
        LanErrors.Timeout(message, innerException, bytesTransferred);

    public static partial Exception ConnectionLost(string message, Exception? innerException) =>
        LanErrors.ConnectionLost(message, innerException);

    public static partial Exception NotFound(string message, Exception? innerException) =>
        LanErrors.HostNotFound(message, innerException);

    public static partial Exception SystemError(string message, int errno) => LanErrors.SystemError(message, errno);

    public static partial bool IsTimeout(Exception ex) => ex is LanTimeoutException;
}

using VisaCs.Lan;

namespace VisaCs.Internal;

// The simulator links the LAN wire code, so the shared sources in src/Shared throw the same exceptions as in
// VisaCs.Lan (see src/Shared/SharedErrors.cs and VisaCs.Lan/Internal/LanErrors.cs).
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

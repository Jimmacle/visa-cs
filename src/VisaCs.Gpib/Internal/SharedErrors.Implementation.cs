using VisaCs.Gpib;

namespace VisaCs.Internal;

// This library's exceptions for the shared sources in src/Shared (see src/Shared/SharedErrors.cs).
internal static partial class SharedErrors
{
    public static partial Exception Timeout(string? message, Exception? innerException, int bytesTransferred) =>
        new GpibTimeoutException(message, innerException) { BytesTransferred = bytesTransferred };

    public static partial Exception ConnectionLost(string message, Exception? innerException) =>
        new GpibException(GpibError.PowerLost, message, innerException);

    public static partial Exception NotFound(string message, Exception? innerException) =>
        new GpibException(GpibError.NoBoard, message, innerException);

    public static partial Exception SystemError(string message, int errno) =>
        new GpibException(GpibError.SystemError, message) { NativeError = errno };

    public static partial bool IsTimeout(Exception ex) => ex is GpibTimeoutException;
}

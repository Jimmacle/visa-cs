using System.Diagnostics.CodeAnalysis;
using VisaCs.Serial;

namespace VisaCs.Internal;

/// <summary>Translates the exceptions of VisaCs.Serial (<see cref="NativeSerialPort"/>) to VISA exceptions.</summary>
internal static class SerialErrorMapping
{
    /// <summary>The VISA status for each <see cref="SerialPortError"/>.</summary>
    public static VisaStatus ToStatus(SerialPortError error) => error switch
    {
        SerialPortError.NotFound => VisaStatus.ErrorResourceNotFound,
        SerialPortError.InUse => VisaStatus.ErrorResourceBusy,
        SerialPortError.AccessDenied => VisaStatus.ErrorNoPermission,
        SerialPortError.Disconnected => VisaStatus.ErrorConnectionLost,
        SerialPortError.Aborted or SerialPortError.Closed => VisaStatus.ErrorAbort,
        SerialPortError.UnsupportedSetting => VisaStatus.ErrorAttributeStateNotSupported,
        SerialPortError.SystemError => VisaStatus.ErrorSystemError,
        _ => VisaStatus.ErrorIO,
    };

    public static VisaException ToVisa(SerialPortException ex) =>
        new(ToStatus(ex.Error), ex.Message, ex) { NativeError = ex.NativeError };

    public static VisaTimeoutException ToVisa(SerialTimeoutException ex) =>
        new(ex.Message, ex) { BytesTransferred = ex.BytesTransferred };

    /// <summary>
    /// The VISA exception for an exception thrown by the serial library, or false when <paramref name="ex"/> needs no
    /// translation. A cancellation whose inner exception is a serial timeout keeps its token and gets the VISA timeout
    /// as its inner exception.
    /// </summary>
    public static bool TryMap(Exception ex, [NotNullWhen(true)] out Exception? mapped)
    {
        mapped = ex switch
        {
            SerialPortException p => ToVisa(p),
            SerialTimeoutException t => ToVisa(t),
            PlatformNotSupportedException => new VisaException(VisaStatus.ErrorOperationNotSupported, ex.Message, ex),
            OperationCanceledException { InnerException: SerialTimeoutException t } c =>
                new OperationCanceledException(c.Message, ToVisa(t), c.CancellationToken),
            _ => null,
        };
        return mapped is not null;
    }
}

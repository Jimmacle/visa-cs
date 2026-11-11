using VisaCs.Lan;

namespace VisaCs.Internal;

/// <summary>
/// Translates the exceptions of <c>VisaCs.Lan</c> into VISA exceptions, for the TCPIP and VICP sessions. The original
/// exception becomes the <see cref="Exception.InnerException"/>.
/// </summary>
internal static class LanErrorMapping
{
    /// <summary>
    /// Maps <paramref name="error"/> when it is a <c>VisaCs.Lan</c> exception. Use it as an exception filter around
    /// calls into the LAN library: <c>catch (Exception ex) when (LanErrorMapping.TryMap(ex, out var visa)) { throw visa; }</c>.
    /// </summary>
    public static bool TryMap(Exception error, out VisaException mapped)
    {
        switch (error)
        {
            case LanTimeoutException timeout:
                mapped = new VisaTimeoutException(timeout.Message, timeout) { BytesTransferred = timeout.BytesTransferred };
                return true;
            case LanSecurityException security:
                mapped = new VisaSecurityException(ToSecurityFailure(security.Failure), security.Message, security)
                {
                    NativeError = security.NativeError,
                };
                return true;
            case Vxi11Exception { Code: Vxi11Error.IOTimeout } vxi11:
                mapped = new VisaTimeoutException(vxi11.Message, vxi11);
                return true;
            case Vxi11Exception vxi11:
                mapped = new VisaException(ToStatus(vxi11.Code), vxi11.Message, vxi11) { NativeError = vxi11.NativeError };
                return true;
            case LanException lan:
                mapped = new VisaException(ToStatus(lan.Error), lan.Message, lan) { NativeError = lan.NativeError };
                return true;
            default:
                mapped = null!;
                return false;
        }
    }

    /// <summary>The VISA exception for <paramref name="error"/> (a <c>VisaCs.Lan</c> exception), or <paramref name="error"/> itself.</summary>
    public static Exception Map(Exception error) => TryMap(error, out var mapped) ? mapped : error;

    /// <summary>The VISA exception for a failed VXI-11 <paramref name="operation"/> that returned <paramref name="code"/>.</summary>
    public static Exception FromVxi11(Vxi11Error code, string operation) => Map(Vxi11Errors.ToException(code, operation));

    /// <summary>The VISA status for a <see cref="LanError"/>.</summary>
    public static VisaStatus ToStatus(LanError error) => error switch
    {
        LanError.ConnectionLost => VisaStatus.ErrorConnectionLost,
        LanError.HostNotFound or LanError.ConnectionRefused or LanError.DeviceNotFound => VisaStatus.ErrorResourceNotFound,
        LanError.ProtocolError or LanError.ServerError => VisaStatus.ErrorIO,
        LanError.NotSupported => VisaStatus.ErrorOperationNotSupported,
        LanError.InvalidRequest => VisaStatus.ErrorInvalidParameter,
        LanError.Locked => VisaStatus.ErrorResourceLocked,
        LanError.NotLocked => VisaStatus.ErrorSessionNotLocked,
        LanError.Busy => VisaStatus.ErrorResourceBusy,
        LanError.Aborted => VisaStatus.ErrorAbort,
        LanError.OutOfResources => VisaStatus.ErrorAllocation,
        LanError.Security => VisaStatus.ErrorNoPermission,
        LanError.SystemError => VisaStatus.ErrorSystemError,
        _ => VisaStatus.ErrorIO,
    };

    /// <summary>
    /// The VISA status for a VXI-11 error code (<see cref="Vxi11Error.IOTimeout"/>, which <c>VisaCs.Lan</c> reports
    /// as a timeout, is <see cref="VisaStatus.ErrorTimeout"/>).
    /// </summary>
    public static VisaStatus ToStatus(Vxi11Error code) => code switch
    {
        Vxi11Error.IOTimeout => VisaStatus.ErrorTimeout,
        Vxi11Error.DeviceLocked => VisaStatus.ErrorResourceLocked,
        Vxi11Error.NoLockHeld => VisaStatus.ErrorSessionNotLocked,
        Vxi11Error.Abort => VisaStatus.ErrorAbort,
        Vxi11Error.OperationNotSupported => VisaStatus.ErrorOperationNotSupported,
        Vxi11Error.InvalidLinkIdentifier or Vxi11Error.ChannelNotEstablished => VisaStatus.ErrorConnectionLost,
        Vxi11Error.DeviceNotAccessible or Vxi11Error.InvalidAddress => VisaStatus.ErrorResourceNotFound,
        Vxi11Error.Parameter or Vxi11Error.Syntax => VisaStatus.ErrorInvalidParameter,
        Vxi11Error.OutOfResources => VisaStatus.ErrorAllocation,
        _ => VisaStatus.ErrorIO,
    };

    /// <summary>The VISA security failure for a <see cref="LanSecurityFailure"/>.</summary>
    public static SecurityFailure ToSecurityFailure(LanSecurityFailure failure) => failure switch
    {
        LanSecurityFailure.TlsNegotiation => SecurityFailure.TlsNegotiation,
        LanSecurityFailure.ServerCertificate => SecurityFailure.ServerCertificate,
        LanSecurityFailure.Authentication => SecurityFailure.Authentication,
        LanSecurityFailure.Policy => SecurityFailure.Policy,
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null),
    };
}

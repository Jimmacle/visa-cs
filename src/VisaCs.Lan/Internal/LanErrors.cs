namespace VisaCs.Internal;

/// <summary>
/// Every exception the LAN protocol code creates, in one place. (Socket-level errors come from <see cref="NetUtil"/>.)
/// The simulator compiles this file too, together with the HiSLIP wire code.
/// </summary>
internal static class LanErrors
{
    /// <summary>The operation did not finish in time, or the caller cancelled it (VI_ERROR_TMO).</summary>
    public static VisaTimeoutException Timeout(string? message = null, Exception? innerException = null, int bytesTransferred = 0) =>
        new(message, innerException) { BytesTransferred = bytesTransferred };

    /// <summary>A protocol error or malformed data (VI_ERROR_IO).</summary>
    public static VisaException IO(string message, Exception? innerException = null) => new(VisaStatus.ErrorIO, message, innerException);

    /// <summary>The connection is closed or broken (VI_ERROR_CONN_LOST).</summary>
    public static VisaException ConnectionLost(string message, Exception? innerException = null) =>
        new(VisaStatus.ErrorConnectionLost, message, innerException);

    /// <summary>The host, service or RPC program cannot be reached (VI_ERROR_RSRC_NFOUND).</summary>
    public static VisaException NotFound(string message, Exception? innerException = null) =>
        new(VisaStatus.ErrorResourceNotFound, message, innerException);

    /// <summary>The peer or protocol version does not support the operation (VI_ERROR_NSUP_OPER).</summary>
    public static VisaException NotSupported(string message) => new(VisaStatus.ErrorOperationNotSupported, message);

    /// <summary>The peer could not decode the request (VI_ERROR_INV_PARAMETER).</summary>
    public static VisaException InvalidParameter(string message) => new(VisaStatus.ErrorInvalidParameter, message);

    /// <summary>The resource is locked by another client (VI_ERROR_RSRC_LOCKED).</summary>
    public static VisaException Locked(string message) => new(VisaStatus.ErrorResourceLocked, message);

    /// <summary>The client holds no lock to release (VI_ERROR_SESN_NLOCKED).</summary>
    public static VisaException NotLocked(string message) => new(VisaStatus.ErrorSessionNotLocked, message);

    /// <summary>The server is busy and refused the request (VI_ERROR_RSRC_BUSY).</summary>
    public static VisaException Busy(string message) => new(VisaStatus.ErrorResourceBusy, message);

    /// <summary>The exception for a VXI-11 error code, with the VISA status that matches it.</summary>
    public static VisaException Vxi11(VisaCs.Lan.Vxi11Error error, string message) => error switch
    {
        VisaCs.Lan.Vxi11Error.IOTimeout => new VisaTimeoutException(message),
        VisaCs.Lan.Vxi11Error.DeviceLocked => new VisaException(VisaStatus.ErrorResourceLocked, message),
        VisaCs.Lan.Vxi11Error.NoLockHeld => new VisaException(VisaStatus.ErrorSessionNotLocked, message),
        VisaCs.Lan.Vxi11Error.Abort => new VisaException(VisaStatus.ErrorAbort, message),
        VisaCs.Lan.Vxi11Error.OperationNotSupported => new VisaException(VisaStatus.ErrorOperationNotSupported, message),
        VisaCs.Lan.Vxi11Error.InvalidLinkIdentifier or VisaCs.Lan.Vxi11Error.ChannelNotEstablished =>
            new VisaException(VisaStatus.ErrorConnectionLost, message),
        VisaCs.Lan.Vxi11Error.DeviceNotAccessible or VisaCs.Lan.Vxi11Error.InvalidAddress => new VisaException(VisaStatus.ErrorResourceNotFound, message),
        VisaCs.Lan.Vxi11Error.Parameter or VisaCs.Lan.Vxi11Error.Syntax => new VisaException(VisaStatus.ErrorInvalidParameter, message),
        VisaCs.Lan.Vxi11Error.OutOfResources => new VisaException(VisaStatus.ErrorAllocation, message),
        _ => new VisaException(VisaStatus.ErrorIO, message),
    };

    /// <summary>A secure connection (TLS or authentication) failed or the policy forbids it (VI_ERROR_NPERMISSION).</summary>
    public static VisaSecurityException Security(SecurityFailure failure, string message, Exception? innerException = null) =>
        new(failure, message, innerException);
}

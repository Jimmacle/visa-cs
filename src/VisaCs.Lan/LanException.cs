namespace VisaCs.Lan;

/// <summary>What went wrong in a <see cref="LanException"/>.</summary>
public enum LanError
{
    /// <summary>The connection is closed or broken, or the server no longer knows the link.</summary>
    ConnectionLost,
    /// <summary>The host name cannot be resolved, or the host cannot be reached.</summary>
    HostNotFound,
    /// <summary>The host refused the TCP connection (nothing listens on the port).</summary>
    ConnectionRefused,
    /// <summary>The host has no such device, HiSLIP sub-address, service or RPC program (version).</summary>
    DeviceNotFound,
    /// <summary>The peer broke the protocol: malformed or unexpected data.</summary>
    ProtocolError,
    /// <summary>The server or device reported an error (a HiSLIP Error message, a VXI-11 I/O error, an RPC system error).</summary>
    ServerError,
    /// <summary>The server or protocol version does not support the operation.</summary>
    NotSupported,
    /// <summary>The server rejected the request's arguments (syntax or parameter error, undecodable RPC arguments).</summary>
    InvalidRequest,
    /// <summary>The device is locked by another client.</summary>
    Locked,
    /// <summary>The client holds no lock to release.</summary>
    NotLocked,
    /// <summary>The server is busy (for example unread data) and refused the request.</summary>
    Busy,
    /// <summary>The device aborted the operation (VXI-11 <c>device_abort</c>).</summary>
    Aborted,
    /// <summary>The server is out of resources.</summary>
    OutOfResources,
    /// <summary>A secure connection (TLS or authentication) failed or the policy forbids it; see <see cref="LanSecurityException"/>.</summary>
    Security,
    /// <summary>An operating system call failed unexpectedly.</summary>
    SystemError,
}

/// <summary>
/// A LAN connection or protocol failure. <see cref="Error"/> tells what went wrong; subclasses add protocol detail
/// (<see cref="Vxi11Exception"/>, <see cref="Rpc.RpcException"/>, <see cref="LanSecurityException"/>). Timeouts throw
/// <see cref="LanTimeoutException"/> instead.
/// </summary>
public class LanException : IOException
{
    /// <summary>Creates the exception.</summary>
    public LanException(LanError error, string? message = null, Exception? innerException = null)
        : base(message ?? error.ToString(), innerException)
    {
        Error = error;
    }

    /// <summary>What went wrong.</summary>
    public LanError Error { get; }

    /// <summary>The operating system's error code (socket error or errno), when known.</summary>
    public int? NativeError { get; init; }
}

/// <summary>A LAN operation did not finish within its timeout.</summary>
public class LanTimeoutException : TimeoutException
{
    /// <summary>Creates the exception.</summary>
    public LanTimeoutException(string? message = null, Exception? innerException = null)
        : base(message ?? "The operation timed out.", innerException)
    {
    }

    /// <summary>The number of bytes transferred before the timeout (for a send: the bytes known to be sent).</summary>
    public int BytesTransferred { get; init; }
}

/// <summary>Why a HiSLIP secure connection failed (<see cref="LanSecurityException.Failure"/>).</summary>
public enum LanSecurityFailure
{
    /// <summary>TLS could not be negotiated (protocol versions, cipher suites, transport errors).</summary>
    TlsNegotiation,
    /// <summary>The server certificate was not trusted (untrusted issuer, expired, revoked or wrong name).</summary>
    ServerCertificate,
    /// <summary>The client could not be authenticated with any mutually supported SASL mechanism.</summary>
    Authentication,
    /// <summary>The server requires encryption but the client policy forbids it, or the reverse.</summary>
    Policy,
}

/// <summary>A HiSLIP secure connection (TLS or SASL authentication) failed; <see cref="LanException.Error"/> is <see cref="LanError.Security"/>.</summary>
public sealed class LanSecurityException : LanException
{
    /// <summary>Creates the exception.</summary>
    public LanSecurityException(LanSecurityFailure failure, string message, Exception? innerException = null)
        : base(LanError.Security, message, innerException)
    {
        Failure = failure;
    }

    /// <summary>What failed.</summary>
    public LanSecurityFailure Failure { get; }
}

/// <summary>
/// A VXI-11 call returned an error code (other than <see cref="Vxi11Error.IOTimeout"/>, which is a
/// <see cref="LanTimeoutException"/>); see <see cref="Vxi11Errors.ToException"/>.
/// </summary>
public sealed class Vxi11Exception : LanException
{
    /// <summary>Creates the exception; <see cref="LanException.Error"/> is derived from <paramref name="code"/>.</summary>
    public Vxi11Exception(Vxi11Error code, string? message = null, Exception? innerException = null)
        : base(ErrorFor(code), message ?? $"VXI-11 error: {Vxi11Errors.Describe(code)} ({(int)code}).", innerException)
    {
        Code = code;
    }

    /// <summary>The device's error code.</summary>
    public Vxi11Error Code { get; }

    /// <summary>The <see cref="LanError"/> that matches a VXI-11 error code.</summary>
    public static LanError ErrorFor(Vxi11Error code) => code switch
    {
        Vxi11Error.DeviceLocked => LanError.Locked,
        Vxi11Error.NoLockHeld => LanError.NotLocked,
        Vxi11Error.Abort => LanError.Aborted,
        Vxi11Error.OperationNotSupported => LanError.NotSupported,
        Vxi11Error.InvalidLinkIdentifier or Vxi11Error.ChannelNotEstablished => LanError.ConnectionLost,
        Vxi11Error.DeviceNotAccessible or Vxi11Error.InvalidAddress => LanError.DeviceNotFound,
        Vxi11Error.Parameter or Vxi11Error.Syntax => LanError.InvalidRequest,
        Vxi11Error.OutOfResources => LanError.OutOfResources,
        _ => LanError.ServerError,
    };
}

namespace VisaCs;

/// <summary>What part of establishing a secure connection failed.</summary>
public enum SecurityFailure
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

/// <summary>
/// A HiSLIP secure connection (TLS or SASL authentication) failed. The status is
/// <see cref="VisaStatus.ErrorNoPermission"/> (VI_ERROR_NPERMISSION).
/// </summary>
public class VisaSecurityException : VisaException
{
    /// <summary>Creates a security exception (VI_ERROR_NPERMISSION).</summary>
    public VisaSecurityException(SecurityFailure failure, string message, Exception? innerException = null)
        : base(VisaStatus.ErrorNoPermission, message, innerException)
    {
        Failure = failure;
    }

    /// <summary>Which step failed.</summary>
    public SecurityFailure Failure { get; }
}

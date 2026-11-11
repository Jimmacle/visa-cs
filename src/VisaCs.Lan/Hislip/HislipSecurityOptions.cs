using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace VisaCs.Lan;

/// <summary>When a HiSLIP session encrypts its connection (HiSLIP 2.0 secure connections).</summary>
public enum HislipEncryptionMode
{
    /// <summary>Encrypt only when the server requires it (mandatory or initial encryption). The default.</summary>
    Auto,
    /// <summary>Encrypt whenever the server supports secure connections.</summary>
    Preferred,
    /// <summary>Always encrypt; opening fails if the server does not support secure connections.</summary>
    Required,
    /// <summary>Never encrypt; opening fails if the server requires encryption.</summary>
    Disabled,
}

/// <summary>
/// Credentials and policy for HiSLIP 2.0 secure connections (TLS plus SASL client authentication, IVI-6.1 and IVI-6.5).
/// Pass it to <see cref="VisaCs.Lan.HislipClient.Connect"/>, or set it on <c>ResourceManagerOptions.HislipSecurity</c>.
/// </summary>
/// <remarks>
/// A client reads these options when it connects, and again when it starts encryption on an open connection
/// (<see cref="VisaCs.Lan.HislipClient.EstablishSecureConnection"/>); later changes affect only those later uses. The
/// object is not thread-safe: configure it before clients use it.
/// </remarks>
public sealed class HislipSecurityOptions
{
    /// <summary>When to establish a secure connection. Default <see cref="HislipEncryptionMode.Auto"/>.</summary>
    public HislipEncryptionMode Encryption { get; set; } = HislipEncryptionMode.Auto;

    /// <summary>
    /// User name and password for SCRAM-SHA-256(-PLUS) and PLAIN, used for hosts that <see cref="HostCredentials"/> and
    /// <see cref="CredentialProvider"/> supply nothing for.
    /// </summary>
    public NetworkCredential? Credentials { get; set; }

    /// <summary>
    /// Per-host credentials, keyed by the host as passed to the client (as written in the resource name) (case-insensitive). Consulted first.
    /// </summary>
    public IDictionary<string, NetworkCredential> HostCredentials { get; } =
        new Dictionary<string, NetworkCredential>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Supplies credentials for a host (as written in the resource name). Consulted when <see cref="HostCredentials"/>
    /// has no entry for the host, before <see cref="Credentials"/>; return null to fall back to <see cref="Credentials"/>.
    /// </summary>
    public Func<string, NetworkCredential?>? CredentialProvider { get; set; }

    /// <summary>
    /// Client certificates (with private keys) offered during the TLS handshake. The SASL EXTERNAL mechanism, which
    /// authenticates with the TLS client certificate, is only used when this is not empty.
    /// </summary>
    public X509CertificateCollection ClientCertificates { get; } = new();

    /// <summary>
    /// Extra trust anchors: CA certificates or the instruments' own (for example self-signed) certificates.
    /// A server certificate is accepted if it chains to one of these or is one of these (and is within its validity
    /// period). The host name is still checked unless <see cref="CheckCertificateName"/> is false.
    /// </summary>
    public X509Certificate2Collection TrustedCertificates { get; } = new();

    /// <summary>Check that the certificate matches the host name (or <see cref="TargetHostName"/>). Default true.</summary>
    public bool CheckCertificateName { get; set; } = true;

    /// <summary>Overrides the name checked against the certificate (and sent as TLS SNI). Default: the host in the resource name.</summary>
    public string? TargetHostName { get; set; }

    /// <summary>
    /// Custom server certificate validation. When set it replaces all built-in checks, including
    /// <see cref="AcceptAnyServerCertificate"/>, <see cref="TrustedCertificates"/> and <see cref="CheckCertificateName"/>.
    /// </summary>
    public RemoteCertificateValidationCallback? ServerCertificateValidation { get; set; }

    /// <summary>Accept any server certificate. For testing only: disables protection against impersonation.</summary>
    public bool AcceptAnyServerCertificate { get; set; }

    /// <summary>
    /// Whether the server certificate chain is checked for revocation. Default <see cref="X509RevocationMode.NoCheck"/>,
    /// because instruments are often on networks without access to the issuers' revocation lists. With
    /// <see cref="X509RevocationMode.Online"/> or <see cref="X509RevocationMode.Offline"/> a certificate whose status
    /// cannot be determined is rejected. Certificates pinned in <see cref="TrustedCertificates"/> are not checked.
    /// </summary>
    public X509RevocationMode RevocationMode { get; set; } = X509RevocationMode.NoCheck;

    /// <summary>
    /// Allowed SASL mechanisms in preference order (e.g. "SCRAM-SHA-256-PLUS", "PLAIN"). Empty (the default) means
    /// SCRAM-SHA-256-PLUS, SCRAM-SHA-256, EXTERNAL, then ANONYMOUS when no credentials are configured, as far as the
    /// server offers them. PLAIN, which sends the password itself (protected only by TLS), is used only when listed here.
    /// </summary>
    /// <remarks>
    /// A mechanism the server rejects outright is skipped. If the server rejects the credentials, authentication
    /// fails without trying further mechanisms, so an impostor cannot make a strong mechanism fail on purpose to get
    /// the credentials through a weaker one.
    /// </remarks>
    public IList<string> SaslMechanisms { get; } = new List<string>();

    /// <summary>Trace string sent with the ANONYMOUS mechanism. Default "anonymous@&lt;machine name&gt;".</summary>
    public string AnonymousTrace { get; set; } = "anonymous@" + Environment.MachineName;

    /// <summary>Authorization identity sent with PLAIN and EXTERNAL. Default empty, as IVI-6.5 recommends.</summary>
    public string? AuthorizationId { get; set; }

    /// <summary>Allowed TLS versions. Default TLS 1.2 and 1.3.</summary>
    public SslProtocols TlsProtocols { get; set; } = SslProtocols.Tls12 | SslProtocols.Tls13;

    internal NetworkCredential? CredentialsFor(string host)
    {
        if (HostCredentials.TryGetValue(host, out var c)) return c;
        return CredentialProvider?.Invoke(host) ?? Credentials;
    }

    /// <summary>The built-in server certificate check: system trust, then <see cref="TrustedCertificates"/>.</summary>
    internal bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (ServerCertificateValidation is not null) return ServerCertificateValidation(sender, certificate, chain, errors);
        if (AcceptAnyServerCertificate) return true;
        if (certificate is null) return false;
        if (!CheckCertificateName) errors &= ~SslPolicyErrors.RemoteCertificateNameMismatch;
        if (errors == SslPolicyErrors.None) return true;
        if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None) return false;
        if (TrustedCertificates.Count == 0) return false;

        var server = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
        foreach (var trusted in TrustedCertificates)
        {
            if (trusted.RawData.AsSpan().SequenceEqual(server.RawData) && server.NotAfter > DateTime.Now && server.NotBefore <= DateTime.Now)
                return true; // pinned certificate
        }
        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.RevocationMode = RevocationMode;
        custom.ChainPolicy.CustomTrustStore.AddRange(TrustedCertificates);
        if (chain is not null)
        {
            foreach (var element in chain.ChainElements) custom.ChainPolicy.ExtraStore.Add(element.Certificate);
        }
        return custom.Build(server);
    }
}

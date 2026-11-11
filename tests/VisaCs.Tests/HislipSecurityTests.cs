using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VisaCs.Lan;
using VisaCs.Simulation;

namespace VisaCs.Tests;

public sealed class HislipSecurityFixture
{
    public X509Certificate2 ServerCertificate { get; } = HislipInstrumentServer.CreateSelfSignedCertificate("localhost");

    /// <summary>The server certificate without its private key, as a client would know it.</summary>
    public X509Certificate2 PublicServerCertificate => X509CertificateLoader.LoadCertificate(ServerCertificate.RawData);
}

public sealed class HislipSecurityTests(HislipSecurityFixture fixture) : IClassFixture<HislipSecurityFixture>
{
    private sealed class Setup : IDisposable
    {
        public required SimulatedInstrument Instrument { get; init; }
        public required HislipInstrumentServer Server { get; init; }
        public required ResourceManager ResourceManager { get; init; }
        public string Resource => $"TCPIP::127.0.0.1::hislip0,{Server.Port}::INSTR";

        public void Dispose()
        {
            ResourceManager.Dispose();
            Server.Dispose();
        }
    }

    private Setup Create(Action<HislipInstrumentServer>? configureServer = null, Action<HislipSecurityOptions>? configureClient = null,
        bool secure = true)
    {
        var instrument = new SimulatedInstrument();
        var server = new HislipInstrumentServer(instrument);
        if (secure)
        {
            server.Certificate = fixture.ServerCertificate;
            server.Users["alice"] = "s3cret, with = signs";
        }
        configureServer?.Invoke(server);
        server.Start();
        var options = new ResourceManagerOptions { EnableLanDiscovery = false, EnableMdnsDiscovery = false };
        options.HislipSecurity.TrustedCertificates.Add(fixture.PublicServerCertificate);
        options.HislipSecurity.Credentials = new NetworkCredential("alice", "s3cret, with = signs");
        configureClient?.Invoke(options.HislipSecurity);
        return new Setup { Instrument = instrument, Server = server, ResourceManager = new ResourceManager(options) };
    }

    private static TcpipHislipSession Open(Setup setup)
    {
        var session = setup.ResourceManager.Open<TcpipHislipSession>(setup.Resource);
        session.TimeoutMilliseconds = 10_000;
        return session;
    }

    [Fact]
    public void NegotiatesProtocolVersions()
    {
        using (var v2 = Create(secure: false))
        using (var session = Open(v2))
        {
            Assert.Equal(0x0200, session.ProtocolVersion);
            Assert.False(session.SecureConnectionSupported);
            Assert.False(session.EncryptionEnabled);
            Assert.Equal(v2.Instrument.Identity, session.Query("*IDN?"));
        }
        using (var v1 = Create(s => s.ProtocolVersion = 0x0100))
        using (var session = Open(v1))
        {
            Assert.Equal(0x0100, session.ProtocolVersion);
            Assert.False(session.SecureConnectionSupported); // capabilities only exist in 2.0
            Assert.Equal(v1.Instrument.Identity, session.Query("*IDN?"));
            Assert.Equal(VisaStatus.ErrorOperationNotSupported, Assert.Throws<VisaException>(() => session.GetDescriptors()).Status);
        }
    }

    [Fact]
    public void EncryptsAndAuthenticatesWithScramPlus()
    {
        using var setup = Create(configureClient: c => c.Encryption = HislipEncryptionMode.Preferred);
        using var session = Open(setup);
        Assert.True(session.SecureConnectionSupported);
        Assert.True(session.EncryptionEnabled);
        Assert.Equal("SCRAM-SHA-256-PLUS", session.SaslMechanism);
        Assert.Equal(("alice", "SCRAM-SHA-256-PLUS"), setup.Server.LastAuthentication);
        Assert.NotNull(session.TlsCipherSuite);
        Assert.Contains("CN=localhost", session.ServerCertificate!.Subject);

        // Everything works over the encrypted channels.
        Assert.Equal(setup.Instrument.Identity, session.Query("*IDN?"));
        Assert.Equal(100_000, session.QueryBinaryBlock("DATA:BIN? 100000").Length);
        session.WriteLine("*SRE 1;SRQ");
        Assert.Equal("1", session.Query("*OPC?"));
        Assert.Equal(0x41, session.ReadStatusByte() & 0x41);
        session.EnableEvent(EventType.ServiceRequest, EventMechanism.Queue);
        setup.Instrument.RequestService();
        Assert.IsType<ServiceRequestEventArgs>(session.WaitOnEvent(EventType.ServiceRequest, 5000));
        session.LockExclusive(1000);
        Assert.True(session.GetLockInfo().ExclusiveLockGranted);
        session.Unlock();
        session.Clear();
        Assert.Equal(setup.Instrument.Identity, session.Query("*IDN?"));

        // VISA attributes
        Assert.True(session.GetAttribute<bool>(VisaAttribute.TcpipHislipEncryptionEnabled));
        Assert.Equal("SCRAM-SHA-256-PLUS", session.GetAttribute<string>(VisaAttribute.TcpipSaslMechanism));
        Assert.Contains("localhost", session.GetAttribute<string>(VisaAttribute.TcpipServerCertSubjectName));
        Assert.EndsWith("Z", session.GetAttribute<string>(VisaAttribute.TcpipServerCertExpirationDate));
        Assert.False(session.GetAttribute<bool>(VisaAttribute.TcpipServerCertIsPerpetual));
    }

    [Fact]
    public void TogglesOptionalEncryption()
    {
        using var setup = Create();
        using var session = Open(setup);
        Assert.False(session.EncryptionEnabled); // Auto: optional encryption is not switched on
        Assert.Equal(setup.Instrument.Identity, session.Query("*IDN?"));

        session.SetEncryptionEnabled(true);
        Assert.True(session.EncryptionEnabled);
        Assert.Equal("SCRAM-SHA-256-PLUS", session.SaslMechanism);
        Assert.NotNull(session.ServerCertificate);
        Assert.Equal("x", session.Query("ECHO? x"));

        session.SetAttribute(VisaAttribute.TcpipHislipEncryptionEnabled, false);
        Assert.False(session.EncryptionEnabled);
        Assert.Null(session.SaslMechanism);
        Assert.Null(session.ServerCertificate);
        Assert.Null(session.TlsCipherSuite);
        Assert.Equal("y", session.Query("ECHO? y"));
        session.EnableEvent(EventType.ServiceRequest, EventMechanism.Queue);
        setup.Instrument.RequestService();
        Assert.IsType<ServiceRequestEventArgs>(session.WaitOnEvent(EventType.ServiceRequest, 5000));

        session.SetEncryptionEnabled(true);
        Assert.Equal("z", session.Query("ECHO? z"));
    }

    [Fact]
    public void HonorsMandatoryEncryption()
    {
        using var setup = Create(s => s.EncryptionMandatory = true);
        using (var session = Open(setup))
        {
            Assert.True(session.EncryptionMandatory);
            Assert.True(session.EncryptionEnabled); // Auto encrypts because the server requires it
            Assert.Equal(setup.Instrument.Identity, session.Query("*IDN?"));
            var ex = Assert.Throws<VisaSecurityException>(() => session.SetEncryptionEnabled(false));
            Assert.Equal(SecurityFailure.Policy, ex.Failure);
        }

        setup.ResourceManager.Options.HislipSecurity.Encryption = HislipEncryptionMode.Disabled;
        var refused = Assert.Throws<VisaSecurityException>(() => Open(setup));
        Assert.Equal(SecurityFailure.Policy, refused.Failure);
        Assert.Equal(VisaStatus.ErrorNoPermission, refused.Status);
    }

    [Fact]
    public void PerformsRequiredInitialEncryption()
    {
        using var setup = Create(s => s.InitialEncryptionRequired = true);
        using var session = Open(setup);
        Assert.True(session.EncryptionEnabled);
        Assert.Equal(setup.Instrument.Identity, session.Query("*IDN?"));
        session.SetEncryptionEnabled(false); // allowed: encryption is optional after the initial secure connection
        Assert.Equal(setup.Instrument.Identity, session.Query("*IDN?"));
    }

    [Fact]
    public void RequiresSupportWhenAsked()
    {
        using var setup = Create(secure: false, configureClient: c => c.Encryption = HislipEncryptionMode.Required);
        var ex = Assert.Throws<VisaSecurityException>(() => Open(setup));
        Assert.Equal(SecurityFailure.Policy, ex.Failure);
    }

    [Fact]
    public void RejectsWrongPassword()
    {
        using var setup = Create(configureClient: c =>
        {
            c.Encryption = HislipEncryptionMode.Preferred;
            c.Credentials = new NetworkCredential("alice", "wrong");
        });
        var ex = Assert.Throws<VisaSecurityException>(() => Open(setup));
        Assert.Equal(SecurityFailure.Authentication, ex.Failure);
        Assert.Equal(VisaStatus.ErrorNoPermission, ex.Status);
        Assert.Equal(0, setup.Server.AuthenticationCount);
    }

    [Fact]
    public void RejectsUntrustedCertificates()
    {
        using var setup = Create(configureClient: c =>
        {
            c.Encryption = HislipEncryptionMode.Preferred;
            c.TrustedCertificates.Clear();
        });
        var ex = Assert.Throws<VisaSecurityException>(() => Open(setup));
        Assert.Equal(SecurityFailure.ServerCertificate, ex.Failure);

        setup.ResourceManager.Options.HislipSecurity.AcceptAnyServerCertificate = true;
        using var session = Open(setup);
        Assert.True(session.EncryptionEnabled);
    }

    [Fact]
    public void ChecksCertificateNames()
    {
        var other = HislipInstrumentServer.CreateSelfSignedCertificate("instrument.example");
        using var setup = Create(s => s.Certificate = other, c =>
        {
            c.Encryption = HislipEncryptionMode.Preferred;
            c.TrustedCertificates.Add(X509CertificateLoader.LoadCertificate(other.RawData));
        });
        // The certificate does name 127.0.0.1 (SAN), so this succeeds...
        using (var session = Open(setup)) Assert.True(session.EncryptionEnabled);
        // ...but a mismatching expected name fails.
        setup.ResourceManager.Options.HislipSecurity.TargetHostName = "someone-else.example";
        Assert.Equal(SecurityFailure.ServerCertificate, Assert.Throws<VisaSecurityException>(() => Open(setup)).Failure);
        setup.ResourceManager.Options.HislipSecurity.CheckCertificateName = false;
        using (var session = Open(setup)) Assert.True(session.EncryptionEnabled);
    }

    [Theory]
    [InlineData("SCRAM-SHA-256")]
    [InlineData("PLAIN")]
    public void AuthenticatesWithOtherMechanisms(string mechanism)
    {
        using var setup = Create(s =>
        {
            s.SaslMechanisms.Clear();
            s.SaslMechanisms.Add(mechanism);
        }, c =>
        {
            c.Encryption = HislipEncryptionMode.Preferred;
            if (mechanism == "PLAIN") c.SaslMechanisms.Add("PLAIN"); // PLAIN is opt-in
        });
        using var session = Open(setup);
        Assert.Equal(mechanism, session.SaslMechanism);
        Assert.Equal(("alice", mechanism), setup.Server.LastAuthentication);
        Assert.Equal(setup.Instrument.Identity, session.Query("*IDN?"));
    }

    [Fact]
    public void AuthenticatesAnonymously()
    {
        using var setup = Create(s =>
        {
            s.SaslMechanisms.Clear();
            s.SaslMechanisms.Add("ANONYMOUS");
        }, c =>
        {
            c.Encryption = HislipEncryptionMode.Preferred;
            c.Credentials = null;
            c.AnonymousTrace = "tester@example.com";
        });
        using var session = Open(setup);
        Assert.Equal("ANONYMOUS", session.SaslMechanism);
        Assert.Equal("anonymous:tester@example.com", setup.Server.LastAuthentication.User);
    }

    [Fact]
    public void AuthenticatesWithClientCertificates()
    {
        using var clientKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=visa-client", clientKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var clientCertificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);

        using var setup = Create(s =>
        {
            s.SaslMechanisms.Clear();
            s.SaslMechanisms.Add("EXTERNAL");
            s.TrustedClientCertificates.Add(X509CertificateLoader.LoadCertificate(clientCertificate.RawData));
        }, c =>
        {
            c.Encryption = HislipEncryptionMode.Preferred;
            c.Credentials = null;
            c.ClientCertificates.Add(clientCertificate);
        });
        using var session = Open(setup);
        Assert.Equal("EXTERNAL", session.SaslMechanism);
        Assert.Equal("CN=visa-client", setup.Server.LastAuthentication.User);
    }

    [Fact]
    public void FollowsClientMechanismPreferences()
    {
        using var setup = Create(configureClient: c =>
        {
            c.Encryption = HislipEncryptionMode.Preferred;
            c.SaslMechanisms.Add("EXTERNAL"); // no client certificate: skipped
            c.SaslMechanisms.Add("PLAIN");
        });
        using var session = Open(setup);
        Assert.Equal("PLAIN", session.SaslMechanism);
    }

    [Fact]
    public void ReportsMissingCredentials()
    {
        using var setup = Create(s =>
        {
            s.SaslMechanisms.Clear();
            s.SaslMechanisms.Add("SCRAM-SHA-256-PLUS");
        }, c =>
        {
            c.Encryption = HislipEncryptionMode.Preferred;
            c.Credentials = null;
        });
        var ex = Assert.Throws<VisaSecurityException>(() => Open(setup));
        Assert.Equal(SecurityFailure.Authentication, ex.Failure);
        Assert.Contains("no credentials", ex.Message);
    }

    [Fact]
    public void UsesPerHostCredentials()
    {
        using var setup = Create(configureClient: c =>
        {
            c.Encryption = HislipEncryptionMode.Preferred;
            c.Credentials = new NetworkCredential("alice", "wrong");
            c.HostCredentials["127.0.0.1"] = new NetworkCredential("alice", "s3cret, with = signs");
        });
        using var session = Open(setup);
        Assert.Equal("SCRAM-SHA-256-PLUS", session.SaslMechanism);
    }

    [Fact]
    public void ReadsDescriptors()
    {
        using var setup = Create();
        using var session = Open(setup);
        var descriptors = session.GetDescriptors();
        var versions = Assert.Single(descriptors, d => d.Type == HislipDescriptorType.SupportedTlsVersions);
        Assert.Equal(new byte[] { 0x03, 0x03, 0x03, 0x04 }, versions.Content);
        Assert.Equal(setup.Server.TlsInformation, Assert.Single(descriptors, d => d.Type == HislipDescriptorType.TlsInformation).Text);
    }
}

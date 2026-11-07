using System.Buffers.Binary;
using System.Net.Security;
using System.Text;
using VisaCs.Internal;

namespace VisaCs.Protocols.Hislip;

/// <summary>HiSLIP 2.0 secure connections: TLS on both channels (StartTLS/EndTLS) and SASL authentication.</summary>
internal sealed partial class HislipConnection
{
    /// <summary>
    /// Mechanisms tried when <see cref="HislipSecurityOptions.SaslMechanisms"/> is empty, strongest first. PLAIN is only
    /// used when listed explicitly; ANONYMOUS only when no credentials are configured.
    /// </summary>
    private static readonly string[] DefaultMechanismOrder = [Sasl.ScramSha256Plus, Sasl.ScramSha256, Sasl.External, Sasl.Anonymous];

    private async ValueTask ApplySecurityPolicyAsync(HislipSecurityOptions security, Deadline deadline, bool async)
    {
        bool required = EncryptionMandatory || InitialEncryptionRequired;
        switch (security.Encryption)
        {
            case HislipEncryptionMode.Disabled when required:
                throw new VisaSecurityException(SecurityFailure.Policy,
                    "The HiSLIP server requires an encrypted connection, but encryption is disabled in HislipSecurityOptions.");
            case HislipEncryptionMode.Required when !SecureConnectionSupported:
                throw new VisaSecurityException(SecurityFailure.Policy,
                    $"The HiSLIP server (protocol {NegotiatedProtocolVersion >> 8}.{NegotiatedProtocolVersion & 0xFF}) does not support secure connections.");
            case HislipEncryptionMode.Required:
            case HislipEncryptionMode.Preferred when SecureConnectionSupported:
            case HislipEncryptionMode.Auto when required:
                await EstablishSecureConnectionAsync(security, deadline, async).ConfigureAwait(false);
                break;
        }
    }

    private SslClientAuthenticationOptions TlsOptions(HislipSecurityOptions security) => new()
    {
        TargetHost = security.TargetHostName ?? _host.Trim('[', ']'),
        EnabledSslProtocols = security.TlsProtocols,
        ClientCertificates = security.ClientCertificates.Count > 0 ? security.ClientCertificates : null,
        RemoteCertificateValidationCallback = security.ValidateServerCertificate,
        CertificateRevocationCheckMode = security.RevocationMode,
    };

    private byte[] LastDeliveredPayload()
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, _lastDeliveredMessageId);
        return payload;
    }

    private async ValueTask<string> LastTlsErrorAsync(Deadline deadline, bool async)
    {
        try
        {
            var error = (await GetDescriptorsAsync(deadline, async).ConfigureAwait(false)).FirstOrDefault(d => d.Type == HislipDescriptorType.TlsLastError);
            return error.Content is { Length: > 0 } ? $" Server reports: {error.Text}" : "";
        }
        catch (VisaException)
        {
            return "";
        }
    }

    private static VisaException ServerBusy() =>
        new(VisaStatus.ErrorResourceBusy, "The HiSLIP server is busy (unread data); clear the device and try again.");

    /// <summary>The Establish Secure Connection transaction: TLS on both channels, then SASL authentication.</summary>
    public async ValueTask EstablishSecureConnectionAsync(HislipSecurityOptions security, Deadline deadline, bool async)
    {
        ThrowIfClosed();
        if (IsEncrypted) return;
        if (!SecureConnectionSupported)
            throw new VisaSecurityException(SecurityFailure.Policy, "The HiSLIP server does not support secure connections.");

        byte answer = await SwitchAsyncChannelAsync(HislipMessageType.AsyncStartTls, HislipMessageType.AsyncStartTlsResponse,
            async channel => await TlsHislipChannel.AuthenticateAsClientAsync(channel.Socket, TlsOptions(security), deadline, async).ConfigureAwait(false),
            "starting TLS failed", deadline, async).ConfigureAwait(false);
        if (answer == 0) throw ServerBusy();
        if (answer != 1)
            throw new VisaSecurityException(SecurityFailure.TlsNegotiation,
                "The HiSLIP server refused to start TLS." + await LastTlsErrorAsync(deadline, async).ConfigureAwait(false));

        await SwitchSyncChannelAsync(HislipMessageType.StartTls,
            async channel => await TlsHislipChannel.AuthenticateAsClientAsync(channel.Socket, TlsOptions(security), deadline, async).ConfigureAwait(false),
            "starting TLS failed", deadline, async).ConfigureAwait(false);
        var tls = (TlsHislipChannel)_sync;
        ServerCertificate = tls.RemoteCertificate;
        CipherSuite = tls.Stream.NegotiatedCipherSuite;
        TlsProtocol = tls.Stream.SslProtocol;
        try
        {
            await AuthenticateAsync(security, tls, deadline, async).ConfigureAwait(false);
        }
        catch
        {
            Fail("authentication failed");
            throw;
        }
    }

    /// <summary>The End Secure Connection transaction (only allowed when encryption is optional).</summary>
    public async ValueTask EndSecureConnectionAsync(Deadline deadline, bool async)
    {
        ThrowIfClosed();
        if (!IsEncrypted) return;
        if (EncryptionMandatory)
            throw new VisaSecurityException(SecurityFailure.Policy, "The HiSLIP server requires encryption; it cannot be switched off.");

        byte answer = await SwitchAsyncChannelAsync(HislipMessageType.AsyncEndTls, HislipMessageType.AsyncEndTlsResponse,
            async channel => await ((TlsHislipChannel)channel).EndTlsAsync(deadline, async).ConfigureAwait(false),
            "ending TLS failed", deadline, async).ConfigureAwait(false);
        if (answer == 0) throw ServerBusy();
        if (answer != 1)
            throw new VisaException(VisaStatus.ErrorIO, "The HiSLIP server refused to end TLS." + await LastTlsErrorAsync(deadline, async).ConfigureAwait(false));

        await SwitchSyncChannelAsync(HislipMessageType.EndTls,
            async channel => await ((TlsHislipChannel)channel).EndTlsAsync(deadline, async).ConfigureAwait(false),
            "ending TLS failed", deadline, async).ConfigureAwait(false);
        SaslMechanism = null;
        CipherSuite = null;
        TlsProtocol = null;
        ServerCertificate = null;
    }

    /// <summary>
    /// The asynchronous-channel half of StartTLS/EndTLS: sends <paramref name="request"/> and, when the server agrees
    /// (answer 1), switches the channel while the reader loop is paused. Holds the request gate throughout, so no other
    /// request can write into the handshake or close_notify. Returns the server's answer (0 busy, 1 agreed, else refused).
    /// </summary>
    private async ValueTask<byte> SwitchAsyncChannelAsync(HislipMessageType request, HislipMessageType response,
        Func<HislipChannel, ValueTask<HislipChannel>> switchChannel, string failure, Deadline deadline, bool async)
    {
        await EnterAsyncChannelAsync(deadline, async).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            DiscardStaleResponses();
            await SendAsync(_async, request, TakeRmt(), MostRecentMessageId, LastDeliveredPayload(), deadline, async).ConfigureAwait(false);
            HislipMessage reply;
            try
            {
                reply = await WaitForResponseAsync(request, 0, response, deadline, async).ConfigureAwait(false);
            }
            catch (VisaTimeoutException)
            {
                // The server may still agree and then expect a TLS handshake or close_notify: nothing can follow safely.
                Fail($"no {response} arrived in time");
                throw;
            }
            if (reply.Control != 1) return reply.Control;
            try
            {
                var switched = await switchChannel(_async).ConfigureAwait(false);
                _async = switched;
                if (Volatile.Read(ref _disposed)) switched.Dispose();
            }
            catch
            {
                Fail(failure);
                throw;
            }
            finally
            {
                ResumeReader();
            }
            return 1;
        }
        finally
        {
            _asyncRequestGate.Release();
        }
    }

    /// <summary>The synchronous-channel half of StartTLS/EndTLS: sends <paramref name="message"/>, then switches the channel.</summary>
    private async ValueTask SwitchSyncChannelAsync(HislipMessageType message, Func<HislipChannel, ValueTask<HislipChannel>> switchChannel,
        string failure, Deadline deadline, bool async)
    {
        try
        {
            await SendAsync(_sync, message, 0, 0, ReadOnlyMemory<byte>.Empty, deadline, async).ConfigureAwait(false);
            var switched = await switchChannel(_sync).ConfigureAwait(false);
            _sync = switched;
            if (Volatile.Read(ref _disposed)) switched.Dispose();
        }
        catch
        {
            // The asynchronous channel has switched already; the two channels no longer agree.
            Fail(failure);
            throw;
        }
    }

    private List<ISaslClientMechanism> CandidateMechanisms(IReadOnlyList<string> offered, HislipSecurityOptions security, TlsHislipChannel tls)
    {
        var credentials = security.CredentialsFor(_host);
        bool explicitList = security.SaslMechanisms.Count > 0;
        var order = (explicitList ? security.SaslMechanisms : DefaultMechanismOrder)
            .Where(m => offered.Contains(m, StringComparer.OrdinalIgnoreCase));
        bool plusOffered = offered.Contains(Sasl.ScramSha256Plus, StringComparer.OrdinalIgnoreCase);
        var result = new List<ISaslClientMechanism>();
        foreach (var name in order)
        {
            switch (name.ToUpperInvariant())
            {
                case Sasl.ScramSha256Plus when credentials is not null:
                    var binding = tls.ServerEndPointChannelBinding();
                    if (binding is not null)
                        result.Add(new ScramSha256ClientMechanism(credentials.UserName, credentials.Password, binding, channelBindingAvailable: true));
                    break;
                case Sasl.ScramSha256 when credentials is not null:
                    result.Add(new ScramSha256ClientMechanism(credentials.UserName, credentials.Password, null, channelBindingAvailable: !plusOffered));
                    break;
                case Sasl.Plain when credentials is not null:
                    result.Add(new PlainMechanism(credentials.UserName, credentials.Password, security.AuthorizationId));
                    break;
                case Sasl.External when security.ClientCertificates.Count > 0:
                    result.Add(new ExternalMechanism(security.AuthorizationId));
                    break;
                case Sasl.Anonymous when credentials is null || explicitList:
                    result.Add(new AnonymousMechanism(security.AnonymousTrace));
                    break;
            }
        }
        return result;
    }

    /// <summary>
    /// SASL authentication on the (encrypted) synchronous channel. A mechanism the server rejects outright is skipped;
    /// a failed authentication ends the attempt, because retrying the same credentials with a weaker mechanism could
    /// hand them to an impostor (one that makes channel binding fail on purpose).
    /// </summary>
    private async ValueTask AuthenticateAsync(HislipSecurityOptions security, TlsHislipChannel tls, Deadline deadline, bool async)
    {
        await SendAsync(_sync, HislipMessageType.GetSaslMechanismList, 0, 0, ReadOnlyMemory<byte>.Empty, deadline, async).ConfigureAwait(false);
        var list = await ReceiveSyncMessageAsync(deadline, async).ConfigureAwait(false);
        if (list.Type == HislipMessageType.FatalError) throw Fatal(list);
        if (list.Type != HislipMessageType.GetSaslMechanismListResponse)
            throw new VisaException(VisaStatus.ErrorIO, $"HiSLIP protocol error: expected GetSaslMechanismListResponse, got {list.Type}.");
        var offered = Encoding.ASCII.GetString(list.Payload).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var candidates = CandidateMechanisms(offered, security, tls);
        if (candidates.Count == 0)
            throw new VisaSecurityException(SecurityFailure.Authentication,
                $"No usable SASL mechanism: the server offers [{string.Join(", ", offered)}]" +
                (security.CredentialsFor(_host) is null ? " and no credentials are configured." : "."));

        var rejected = new List<string>();
        foreach (var mechanism in candidates)
        {
            await SendAsync(_sync, HislipMessageType.AuthenticationStart, 0, 0, Encoding.ASCII.GetBytes(mechanism.Name), deadline, async)
                .ConfigureAwait(false);
            await SendAsync(_sync, HislipMessageType.AuthenticationExchange, 0, 0, mechanism.InitialResponse(), deadline, async)
                .ConfigureAwait(false);
            while (true)
            {
                var message = await ReceiveSyncMessageAsync(deadline, async).ConfigureAwait(false);
                if (message.Type == HislipMessageType.AuthenticationExchange)
                {
                    await SendAsync(_sync, HislipMessageType.AuthenticationExchange, 0, 0, mechanism.Respond(message.Payload), deadline, async)
                        .ConfigureAwait(false);
                    continue;
                }
                if (message.Type == HislipMessageType.AuthenticationResult)
                {
                    if ((message.Control & 1) == 0)
                        throw new VisaSecurityException(SecurityFailure.Authentication,
                            $"Authentication with {mechanism.Name} failed: {Encoding.UTF8.GetString(message.Payload)} (code {message.Parameter}).");
                    if (!mechanism.VerifyOutcome(message.Payload))
                        throw new VisaSecurityException(SecurityFailure.Authentication,
                            $"The server's {mechanism.Name} signature is invalid: it may not know the credentials (possible impersonation).");
                    SaslMechanism = mechanism.Name;
                    return;
                }
                if (message.Type == HislipMessageType.Error)
                {
                    // The server does not take this mechanism after all: try the next one.
                    rejected.Add($"{mechanism.Name}: {Hislip.DescribeError(message.Control)} {message.PayloadText}".Trim());
                    break;
                }
                if (message.Type == HislipMessageType.FatalError) throw Fatal(message);
                throw new VisaException(VisaStatus.ErrorIO, $"HiSLIP protocol error: unexpected {message.Type} during authentication.");
            }
        }
        throw new VisaSecurityException(SecurityFailure.Authentication, "Authentication failed: the server rejected " + string.Join("; ", rejected));
    }
}

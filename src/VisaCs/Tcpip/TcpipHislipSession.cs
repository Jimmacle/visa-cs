using System.Globalization;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using VisaCs.Internal;
using VisaCs.Lan;

namespace VisaCs;

/// <summary>HiSLIP instrument session (<c>TCPIP::host::hislip0[,port]::INSTR</c>).</summary>
/// <remarks>
/// <para>
/// Data, END and triggers travel in order on the synchronous channel; status byte, locks, remote/local control and
/// the device clear handshake use the asynchronous channel. Locking, <see cref="GetLockInfo"/> and
/// <see cref="GetDescriptors"/> do not wait for a read or write in progress on the session.
/// </para>
/// <para>
/// A read that times out keeps its place: the next read continues the same response (in synchronized mode the next
/// write discards it). A late answer to a timed-out status, lock or clear request is discarded, and a lock the server
/// grants after the request was cancelled is released again. The connection closes, and later calls throw
/// VI_ERROR_CONN_LOST, when a write is cut off after part of a message was sent, when the server reports a fatal
/// error, and when changing the encryption fails; reopen the session then.
/// </para>
/// <para>The protocol itself is <see cref="HislipClient"/> from <c>VisaCs.Lan</c>.</para>
/// </remarks>
public sealed class TcpipHislipSession : TcpipInstrumentSession
{
    private HislipClient? _client;

    internal TcpipHislipSession(ResourceManager manager, TcpipInstrumentResourceName name)
        : base(manager, name, inputBufferSize: 256 * 1024)
    {
    }

    /// <inheritdoc/>
    public override bool IsHislip => true;

    /// <summary>TCP port of the HiSLIP server (VI_ATTR_TCPIP_PORT).</summary>
    public int Port { get; private set; }

    /// <summary>
    /// Negotiated HiSLIP protocol version, e.g. 0x0100 for 1.0 (VI_ATTR_TCPIP_HISLIP_VERSION): the lower of the
    /// server's and this client's (2.0).
    /// </summary>
    public int ProtocolVersion => Client.ProtocolVersion;

    /// <summary>Session ID assigned by the server.</summary>
    public int SessionId => Client.SessionId;

    /// <summary>Two-character vendor ID reported by the server.</summary>
    public string ServerVendorId => new([(char)(Client.ServerVendorId >> 8), (char)(Client.ServerVendorId & 0xFF)]);

    /// <summary>
    /// Largest message the server accepts, in bytes (from its AsyncMaximumMessageSizeResponse); <see cref="ulong.MaxValue"/>
    /// if the server did not answer. Writes are split into Data messages that fit (at most 64 MiB each).
    /// </summary>
    public ulong ServerMaximumMessageSize => Client.ServerMaximumMessageSize;

    /// <summary>
    /// Overlapped mode (VI_ATTR_TCPIP_HISLIP_OVERLAP_EN); false means synchronized mode. Change it with
    /// <see cref="SetOverlapEnabled"/>.
    /// </summary>
    public bool OverlapEnabled => Client.OverlapMode;

    /// <summary>
    /// Requests overlapped or synchronized mode with a device clear (which also discards unread input). The server
    /// decides: read <see cref="OverlapEnabled"/> afterwards.
    /// </summary>
    public void SetOverlapEnabled(bool enabled) => AsyncIO.Complete(SetOverlapEnabledAsync(enabled, CancellationToken.None, async: false));

    /// <summary>Requests overlapped or synchronized mode; see <see cref="SetOverlapEnabled"/>.</summary>
    public Task SetOverlapEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SetOverlapEnabledAsync(enabled, cancellationToken, async: true).AsTask();

    private async ValueTask SetOverlapEnabledAsync(bool enabled, CancellationToken cancellationToken, bool async) =>
        await ExecuteAsync(enabled, static async (s, enabled, ct, async) =>
        {
            var session = (TcpipHislipSession)s;
            session.DiscardInputBuffer();
            await session.DeviceClearAsync(enabled, session.NewDeadline(ct), async).ConfigureAwait(false);
            return true;
        }, cancellationToken, async).ConfigureAwait(false);

    /// <summary>
    /// Largest message, in KiB, this client announces to the server (VI_ATTR_TCPIP_HISLIP_MAX_MESSAGE_KB). Default 1024.
    /// The server should split larger responses; this client accepts messages of any size anyway. Change it with
    /// <see cref="SetMaximumMessageKB"/>.
    /// </summary>
    public int MaximumMessageKB { get; private set; } = 1024;

    /// <summary>Announces a new maximum message size to the server (AsyncMaximumMessageSize); see <see cref="MaximumMessageKB"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kilobytes"/> is less than 1.</exception>
    public void SetMaximumMessageKB(int kilobytes) =>
        AsyncIO.Complete(SetMaximumMessageKBAsync(kilobytes, CancellationToken.None, async: false));

    /// <summary>Announces a new maximum message size to the server; see <see cref="SetMaximumMessageKB"/>.</summary>
    public Task SetMaximumMessageKBAsync(int kilobytes, CancellationToken cancellationToken = default) =>
        SetMaximumMessageKBAsync(kilobytes, cancellationToken, async: true).AsTask();

    private async ValueTask SetMaximumMessageKBAsync(int kilobytes, CancellationToken cancellationToken, bool async)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(kilobytes, 1);
        await RunAsyncChannelAsync(kilobytes, static async (session, kilobytes, deadline, async) =>
        {
            ulong size = (ulong)kilobytes * 1024;
            if (async) await session.Client.SetMaximumMessageSizeAsync(size, deadline.RemainingMilliseconds, deadline.CancellationToken).ConfigureAwait(false);
            else session.Client.SetMaximumMessageSize(size, deadline.RemainingMilliseconds);
            session.MaximumMessageKB = kilobytes;
            return true;
        }, cancellationToken, async).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override bool KeepAlive
    {
        get => Client.KeepAlive;
        set => Client.KeepAlive = value;
    }

    private HislipClient Client => _client ?? throw new VisaException(VisaStatus.ErrorInvalidObject, "The session is not open.");

    private protected override async ValueTask OpenCoreAsync(int connectTimeoutMs, CancellationToken cancellationToken, bool async)
    {
        Port = Name.HislipPort ?? ResourceManager.Options.HislipPort;
        ulong maximumMessageSize = (ulong)MaximumMessageKB * 1024;
        var security = ResourceManager.Options.HislipSecurity;
        _client = async
            ? await HislipClient.ConnectAsync(Name.Host, Port, Name.HislipSubAddress, connectTimeoutMs, security, maximumMessageSize,
                cancellationToken).ConfigureAwait(false)
            : HislipClient.Connect(Name.Host, Port, Name.HislipSubAddress, connectTimeoutMs, security, maximumMessageSize);
        _client.ServiceRequest += stb => RaiseEvent(new ServiceRequestEventArgs(stb));
        Address = _client.RemoteAddress.ToString();
    }

    private protected override ValueTask CloseCoreAsync(bool async)
    {
        _client?.Dispose();
        return default;
    }

    private protected override ValueTask WriteCoreAsync(ReadOnlyMemory<byte> data, bool end, Deadline deadline, bool async)
    {
        // In synchronized mode, unread response data becomes stale once a new message is sent.
        if (!Client.OverlapMode) DiscardInputBuffer();
        if (async) return Client.WriteAsync(data, end, deadline.RemainingMilliseconds, deadline.CancellationToken);
        Client.Write(data, end, deadline.RemainingMilliseconds);
        return default;
    }

    private protected override async ValueTask<FillResult> FillCoreAsync(Memory<byte> target, Deadline deadline, bool async)
    {
        var (count, end) = async
            ? await Client.ReadAsync(target, deadline.RemainingMilliseconds, deadline.CancellationToken).ConfigureAwait(false)
            : Client.Read(target, deadline.RemainingMilliseconds);
        return new FillResult(count, end);
    }

    private protected override void OnReadCompleted(ReadStatus status)
    {
        if (status == ReadStatus.EndReceived) Client.MarkResponseDelivered();
    }

    private protected override ValueTask<byte> ReadStatusByteCoreAsync(Deadline deadline, bool async) =>
        async
            ? Client.ReadStatusByteAsync(deadline.RemainingMilliseconds, deadline.CancellationToken)
            : new(Client.ReadStatusByte(deadline.RemainingMilliseconds));

    private ValueTask DeviceClearAsync(bool? requestOverlap, Deadline deadline, bool async)
    {
        if (async) return Client.DeviceClearAsync(deadline.RemainingMilliseconds, requestOverlap, deadline.CancellationToken);
        Client.DeviceClear(deadline.RemainingMilliseconds, requestOverlap);
        return default;
    }

    private protected override ValueTask ClearCoreAsync(Deadline deadline, bool async) => DeviceClearAsync(null, deadline, async);

    private protected override ValueTask TriggerCoreAsync(Deadline deadline, bool async)
    {
        if (!Client.OverlapMode) DiscardInputBuffer();
        if (async) return Client.TriggerAsync(deadline.RemainingMilliseconds, deadline.CancellationToken);
        Client.Trigger(deadline.RemainingMilliseconds);
        return default;
    }

    private protected override ValueTask ControlRenCoreAsync(RemoteLocalMode mode, Deadline deadline, bool async)
    {
        // HiSLIP's remote/local control codes are VISA's VI_GPIB_REN_* values.
        var control = (HislipRemoteLocalMode)mode;
        if (async) return Client.RemoteLocalAsync(control, deadline.RemainingMilliseconds, deadline.CancellationToken);
        Client.RemoteLocal(control, deadline.RemainingMilliseconds);
        return default;
    }

    private protected override ValueTask AcquireDeviceLockAsync(LockType type, string? key, int timeoutMilliseconds, Deadline deadline, bool async)
    {
        // The server waits for the time this request has left: the caller may have waited for sessions in this process.
        string? sharedKey = type == LockType.Exclusive ? null : key ?? "";
        int timeout = timeoutMilliseconds < 0 ? -1 : deadline.RemainingMilliseconds;
        if (async) return Client.LockAsync(sharedKey, timeout, deadline.CancellationToken);
        Client.Lock(sharedKey, timeout);
        return default;
    }

    private protected override ValueTask ReleaseDeviceLockAsync(LockType type, CancellationToken cancellationToken, bool async)
    {
        // While closing, closing the connection releases the server's locks.
        if (_client is null || _client.IsClosed || IsDisposed) return default;
        if (async) return _client.UnlockAsync(5000, cancellationToken);
        _client.Unlock(5000);
        return default;
    }

    // ------------------------------------------------------------------ HiSLIP 2.0 security

    /// <summary>The server supports HiSLIP 2.0 secure connections (TLS + SASL).</summary>
    public bool SecureConnectionSupported => Client.SecureConnectionSupported;

    /// <summary>The server only accepts encrypted connections; <see cref="SetEncryptionEnabled"/>(false) then fails.</summary>
    public bool EncryptionMandatory => Client.EncryptionMandatory;

    /// <summary>
    /// Whether the connection is encrypted (VI_ATTR_TCPIP_HISLIP_ENCRYPTION_EN). <see cref="ResourceManagerOptions.HislipSecurity"/>
    /// decides at open time; <see cref="SetEncryptionEnabled"/> changes it on an open session.
    /// </summary>
    public bool EncryptionEnabled => Client.IsEncrypted;

    /// <summary>
    /// Starts or ends encryption on the open session. Starting performs the HiSLIP 2.0 Establish Secure Connection
    /// transaction (TLS on both channels, then SASL authentication) with the current
    /// <see cref="ResourceManagerOptions.HislipSecurity"/>, whatever its <see cref="HislipSecurityOptions.Encryption"/>
    /// mode; ending is allowed only when the server does not make encryption mandatory. Unread input is discarded first.
    /// </summary>
    /// <remarks>
    /// If the server reports unread data (VI_ERROR_RSRC_BUSY) nothing changes. Any other failure, including a timeout
    /// or cancellation during the switch, closes the connection: later calls throw VI_ERROR_CONN_LOST and the session
    /// must be reopened.
    /// </remarks>
    /// <exception cref="VisaSecurityException">TLS, certificate or authentication failure, or the policy forbids the change.</exception>
    public void SetEncryptionEnabled(bool enabled) => AsyncIO.Complete(SetEncryptionEnabledAsync(enabled, CancellationToken.None, async: false));

    /// <summary>Starts or ends encryption on the open session; see <see cref="SetEncryptionEnabled"/>.</summary>
    public Task SetEncryptionEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SetEncryptionEnabledAsync(enabled, cancellationToken, async: true).AsTask();

    private async ValueTask SetEncryptionEnabledAsync(bool enabled, CancellationToken cancellationToken, bool async) =>
        await ExecuteAsync(enabled, static async (s, enabled, ct, async) =>
        {
            var session = (TcpipHislipSession)s;
            var client = session.Client;
            if (enabled == client.IsEncrypted) return true;
            session.DiscardInputBuffer();
            int timeout = session.TimeoutMilliseconds;
            var security = session.ResourceManager.Options.HislipSecurity;
            switch (enabled, async)
            {
                case (true, true):
                    await client.EstablishSecureConnectionAsync(security, timeout, ct).ConfigureAwait(false);
                    break;
                case (true, false):
                    client.EstablishSecureConnection(security, timeout);
                    break;
                case (false, true):
                    await client.EndSecureConnectionAsync(timeout, ct).ConfigureAwait(false);
                    break;
                default:
                    client.EndSecureConnection(timeout);
                    break;
            }
            return true;
        }, cancellationToken, async).ConfigureAwait(false);

    /// <summary>The SASL mechanism used to authenticate, e.g. SCRAM-SHA-256-PLUS (VI_ATTR_TCPIP_SASL_MECHANISM); null when not encrypted.</summary>
    public string? SaslMechanism => Client.SaslMechanism;

    /// <summary>The negotiated TLS cipher suite (VI_ATTR_TCPIP_TLS_CIPHER_SUITE, which holds its IANA name); null when not encrypted.</summary>
    public TlsCipherSuite? TlsCipherSuite => Client.CipherSuite;

    /// <summary>The negotiated TLS protocol version; null when not encrypted.</summary>
    public SslProtocols? TlsProtocol => Client.TlsProtocol;

    /// <summary>The server's X.509 certificate; null when not encrypted. Owned by the session: do not dispose it.</summary>
    public X509Certificate2? ServerCertificate => Client.ServerCertificate;

    /// <summary>Reads the server's HiSLIP 2.0 descriptors (supported TLS versions, TLS information, last TLS error).</summary>
    /// <exception cref="VisaException">VI_ERROR_NSUP_OPER with a HiSLIP 1.0 server.</exception>
    public IReadOnlyList<HislipDescriptor> GetDescriptors() =>
        AsyncIO.Complete(GetDescriptorsAsync(CancellationToken.None, async: false));

    /// <summary>Reads the server's HiSLIP 2.0 descriptors; see <see cref="GetDescriptors"/>.</summary>
    public Task<IReadOnlyList<HislipDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken = default) =>
        GetDescriptorsAsync(cancellationToken, async: true).AsTask();

    private ValueTask<IReadOnlyList<HislipDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken, bool async) =>
        RunAsyncChannelAsync(0, static (session, _, deadline, async) =>
            async
                ? session.Client.GetDescriptorsAsync(deadline.RemainingMilliseconds, deadline.CancellationToken)
                : new(session.Client.GetDescriptors(deadline.RemainingMilliseconds)), cancellationToken, async);

    /// <summary>
    /// Queries the server's lock state (AsyncLockInfo): whether any client holds the exclusive lock, and how many
    /// clients hold locks (exclusive or shared).
    /// </summary>
    public (bool ExclusiveLockGranted, int ClientsHoldingLocks) GetLockInfo() =>
        AsyncIO.Complete(GetLockInfoAsync(CancellationToken.None, async: false));

    /// <summary>Queries the server's lock state (AsyncLockInfo); see <see cref="GetLockInfo"/>.</summary>
    public Task<(bool ExclusiveLockGranted, int ClientsHoldingLocks)> GetLockInfoAsync(CancellationToken cancellationToken = default) =>
        GetLockInfoAsync(cancellationToken, async: true).AsTask();

    private ValueTask<(bool ExclusiveLockGranted, int ClientsHoldingLocks)> GetLockInfoAsync(CancellationToken cancellationToken, bool async) =>
        RunAsyncChannelAsync(0, static async (session, _, deadline, async) =>
        {
            var info = async
                ? await session.Client.GetLockInfoAsync(deadline.RemainingMilliseconds, deadline.CancellationToken).ConfigureAwait(false)
                : session.Client.GetLockInfo(deadline.RemainingMilliseconds);
            return (info.ExclusiveLockGranted, info.ClientsHoldingLocks);
        }, cancellationToken, async);

    /// <summary>
    /// Runs an asynchronous-channel request outside the session's I/O gate (it may run while a read or write is in
    /// progress), with the session timeout and the usual error mapping.
    /// </summary>
    private ValueTask<TResult> RunAsyncChannelAsync<TState, TResult>(TState state,
        Func<TcpipHislipSession, TState, Deadline, bool, ValueTask<TResult>> operation, CancellationToken cancellationToken, bool async) =>
        RunAsync((operation, state), static (s, a, token, async) =>
            a.operation((TcpipHislipSession)s, a.state, ((TcpipHislipSession)s).NewDeadline(token), async), cancellationToken, async);

    private static bool IsPerpetual(DateTime notAfter) => notAfter.ToUniversalTime() >= new DateTime(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc);

    private protected override void RegisterAttributes(AttributeTable table)
    {
        base.RegisterAttributes(table);
        table.Add(VisaAttribute.TcpipPort, () => Port);
        table.Add(VisaAttribute.TcpipHislipVersion, () => ProtocolVersion);
        table.Add(VisaAttribute.TcpipHislipOverlapEnabled, () => OverlapEnabled, SetOverlapEnabled);
        table.Add(VisaAttribute.TcpipHislipMaxMessageKB, () => MaximumMessageKB, SetMaximumMessageKB);
        table.Add(VisaAttribute.TcpipHislipEncryptionEnabled, () => EncryptionEnabled, SetEncryptionEnabled);
        table.Add(VisaAttribute.TcpipSaslMechanism, () => SaslMechanism ?? "");
        table.Add(VisaAttribute.TcpipTlsCipherSuite, () => TlsCipherSuite?.ToString() ?? "");
        table.Add(VisaAttribute.TcpipServerCertIssuerName, () => ServerCertificate?.Issuer ?? "");
        table.Add(VisaAttribute.TcpipServerCertSubjectName, () => ServerCertificate?.Subject ?? "");
        table.Add(VisaAttribute.TcpipServerCertExpirationDate,
            () => ServerCertificate is { } c ? c.NotAfter.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) : "");
        table.Add(VisaAttribute.TcpipServerCertIsPerpetual, () => ServerCertificate is { } c && IsPerpetual(c.NotAfter));
    }
}

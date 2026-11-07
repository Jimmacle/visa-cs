using System.Globalization;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using VisaCs.Internal;
using VisaCs.Protocols.Hislip;

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
/// </remarks>
public sealed class TcpipHislipSession : TcpipInstrumentSession
{
    private HislipConnection? _connection;

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
    public int ProtocolVersion => Connection.NegotiatedProtocolVersion;

    /// <summary>Session ID assigned by the server.</summary>
    public int SessionId => Connection.SessionId;

    /// <summary>Two-character vendor ID reported by the server.</summary>
    public string ServerVendorId => new([(char)(Connection.ServerVendorId >> 8), (char)(Connection.ServerVendorId & 0xFF)]);

    /// <summary>
    /// Largest message the server accepts, in bytes (from its AsyncMaximumMessageSizeResponse); <see cref="ulong.MaxValue"/>
    /// if the server did not answer. Writes are split into Data messages that fit (at most 64 MiB each).
    /// </summary>
    public ulong ServerMaximumMessageSize => Connection.ServerMaxMessageSize;

    /// <summary>
    /// Overlapped mode (VI_ATTR_TCPIP_HISLIP_OVERLAP_EN); false means synchronized mode. Change it with
    /// <see cref="SetOverlapEnabled"/>.
    /// </summary>
    public bool OverlapEnabled => Connection.OverlapMode;

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
            await session.Connection.DeviceClearAsync(enabled, session.NewDeadline(ct), async).ConfigureAwait(false);
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
            await session.Connection.SetClientMaximumMessageSizeAsync((ulong)kilobytes * 1024, deadline, async).ConfigureAwait(false);
            session.MaximumMessageKB = kilobytes;
            return true;
        }, cancellationToken, async).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override bool KeepAlive
    {
        get => NetUtil.GetKeepAlive(Connection.SyncSocket);
        set
        {
            NetUtil.SetKeepAlive(Connection.SyncSocket, value);
            NetUtil.SetKeepAlive(Connection.AsyncSocket, value);
        }
    }

    private HislipConnection Connection => _connection ?? throw new VisaException(VisaStatus.ErrorInvalidObject, "The session is not open.");

    private protected override async ValueTask OpenCoreAsync(int connectTimeoutMs, CancellationToken cancellationToken, bool async)
    {
        Port = Name.HislipPort ?? ResourceManager.Options.HislipPort;
        _connection = await HislipConnection.ConnectAsync(Name.Host, Port, Name.HislipSubAddress,
            Deadline.FromMilliseconds(connectTimeoutMs).WithCancellation(cancellationToken), (ulong)MaximumMessageKB * 1024,
            ResourceManager.Options.HislipSecurity, async).ConfigureAwait(false);
        _connection.ServiceRequest += stb => RaiseEvent(new ServiceRequestEventArgs(stb));
        Address = NetUtil.RemoteAddressText(_connection.SyncSocket);
    }

    private protected override ValueTask CloseCoreAsync(bool async)
    {
        _connection?.Dispose();
        return default;
    }

    private protected override ValueTask WriteCoreAsync(ReadOnlyMemory<byte> data, bool end, Deadline deadline, bool async)
    {
        // In synchronized mode, unread response data becomes stale once a new message is sent.
        if (!Connection.OverlapMode) DiscardInputBuffer();
        return Connection.WriteAsync(data, end, deadline, async);
    }

    private protected override ValueTask<FillResult> FillCoreAsync(Memory<byte> target, Deadline deadline, bool async) =>
        Connection.FillAsync(target, deadline, async);

    private protected override void OnReadCompleted(ReadStatus status)
    {
        if (status == ReadStatus.EndReceived) Connection.NotifyRmtDelivered();
    }

    private protected override ValueTask<byte> ReadStatusByteCoreAsync(Deadline deadline, bool async) =>
        Connection.ReadStatusByteAsync(deadline, async);

    private protected override ValueTask ClearCoreAsync(Deadline deadline, bool async) => Connection.DeviceClearAsync(null, deadline, async);

    private protected override ValueTask TriggerCoreAsync(Deadline deadline, bool async)
    {
        if (!Connection.OverlapMode) DiscardInputBuffer();
        return Connection.TriggerAsync(deadline, async);
    }

    private protected override ValueTask ControlRenCoreAsync(RemoteLocalMode mode, Deadline deadline, bool async) =>
        Connection.RemoteLocalAsync(mode, deadline, async);

    private protected override ValueTask AcquireDeviceLockAsync(LockType type, string? key, int timeoutMilliseconds, Deadline deadline, bool async) =>
        Connection.LockAsync(type == LockType.Exclusive ? "" : key ?? "", timeoutMilliseconds, deadline, async);

    private protected override ValueTask ReleaseDeviceLockAsync(LockType type, CancellationToken cancellationToken, bool async)
    {
        // While closing, closing the connection releases the server's locks.
        if (_connection is null || _connection.IsClosed || IsDisposed) return default;
        return _connection.UnlockAsync(Deadline.FromMilliseconds(5000).WithCancellation(cancellationToken), async);
    }

    // ------------------------------------------------------------------ HiSLIP 2.0 security

    /// <summary>The server supports HiSLIP 2.0 secure connections (TLS + SASL).</summary>
    public bool SecureConnectionSupported => Connection.SecureConnectionSupported;

    /// <summary>The server only accepts encrypted connections; <see cref="SetEncryptionEnabled"/>(false) then fails.</summary>
    public bool EncryptionMandatory => Connection.EncryptionMandatory;

    /// <summary>
    /// Whether the connection is encrypted (VI_ATTR_TCPIP_HISLIP_ENCRYPTION_EN). <see cref="ResourceManagerOptions.HislipSecurity"/>
    /// decides at open time; <see cref="SetEncryptionEnabled"/> changes it on an open session.
    /// </summary>
    public bool EncryptionEnabled => Connection.IsEncrypted;

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
            if (enabled == session.Connection.IsEncrypted) return true;
            session.DiscardInputBuffer();
            if (enabled)
                await session.Connection.EstablishSecureConnectionAsync(session.ResourceManager.Options.HislipSecurity, session.NewDeadline(ct), async)
                    .ConfigureAwait(false);
            else
                await session.Connection.EndSecureConnectionAsync(session.NewDeadline(ct), async).ConfigureAwait(false);
            return true;
        }, cancellationToken, async).ConfigureAwait(false);

    /// <summary>The SASL mechanism used to authenticate, e.g. SCRAM-SHA-256-PLUS (VI_ATTR_TCPIP_SASL_MECHANISM); null when not encrypted.</summary>
    public string? SaslMechanism => Connection.SaslMechanism;

    /// <summary>The negotiated TLS cipher suite (VI_ATTR_TCPIP_TLS_CIPHER_SUITE, which holds its IANA name); null when not encrypted.</summary>
    public TlsCipherSuite? TlsCipherSuite => Connection.CipherSuite;

    /// <summary>The negotiated TLS protocol version; null when not encrypted.</summary>
    public SslProtocols? TlsProtocol => Connection.TlsProtocol;

    /// <summary>The server's X.509 certificate; null when not encrypted. Owned by the session: do not dispose it.</summary>
    public X509Certificate2? ServerCertificate => Connection.ServerCertificate;

    /// <summary>Reads the server's HiSLIP 2.0 descriptors (supported TLS versions, TLS information, last TLS error).</summary>
    /// <exception cref="VisaException">VI_ERROR_NSUP_OPER with a HiSLIP 1.0 server.</exception>
    public IReadOnlyList<HislipDescriptor> GetDescriptors() =>
        AsyncIO.Complete(GetDescriptorsAsync(CancellationToken.None, async: false));

    /// <summary>Reads the server's HiSLIP 2.0 descriptors; see <see cref="GetDescriptors"/>.</summary>
    public Task<IReadOnlyList<HislipDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken = default) =>
        GetDescriptorsAsync(cancellationToken, async: true).AsTask();

    private ValueTask<IReadOnlyList<HislipDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken, bool async) =>
        RunAsyncChannelAsync(0, static (session, _, deadline, async) => session.Connection.GetDescriptorsAsync(deadline, async), cancellationToken, async);

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
            var (exclusive, count) = await session.Connection.LockInfoAsync(deadline, async).ConfigureAwait(false);
            return (exclusive, (int)count);
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

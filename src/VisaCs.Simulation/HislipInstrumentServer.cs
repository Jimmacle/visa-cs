using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VisaCs.Internal;
using VisaCs.Lan;
using VisaCs.Lan.Protocols;

namespace VisaCs.Simulation;

/// <summary>
/// A HiSLIP 2.0 server for a <see cref="SimulatedInstrument"/>, including the optional secure connection capability
/// (TLS on both channels and SASL authentication with SCRAM-SHA-256(-PLUS), PLAIN, EXTERNAL and ANONYMOUS).
/// </summary>
public sealed class HislipInstrumentServer : IDisposable
{
    private readonly SimulatedInstrument _device;
    private readonly TcpListener _listener;
    private readonly object _sync = new();
    private readonly Dictionary<ushort, Session> _sessions = new();
    private ushort _nextSessionId = 1;
    private Session? _exclusiveOwner;
    private string? _sharedKey;
    private readonly HashSet<Session> _sharedHolders = new();
    private volatile bool _disposed;
    private int _authentications;
    private int _failedAuthentications;

    private sealed class Session
    {
        public required ushort Id { get; init; }
        public required HislipChannel Sync { get; set; }
        public HislipChannel? Async { get; set; }
        public ushort Version { get; init; }
        public bool Overlap { get; set; }
        public ulong ClientMaxMessageSize { get; set; } = ulong.MaxValue;
        public List<byte> Input { get; } = new();
        public Lock SyncSend { get; } = new();
        public Lock AsyncSend { get; } = new();
        public int RmtDeliveredCount;
        public bool Authenticated { get; set; }
        public bool EverAuthenticated { get; set; }
        /// <summary>Incremented by AsyncDeviceClear: output of operations in progress at that time is abandoned.</summary>
        public int ClearGeneration;
        public string? Mechanism { get; set; }
        public ScramSha256ServerExchange? Scram { get; set; }
        public int ExchangeStep { get; set; }
        public bool Encrypted => Sync.IsEncrypted;
    }

    /// <summary>Creates a HiSLIP server for an instrument. Port 0 picks a free port.</summary>
    public HislipInstrumentServer(SimulatedInstrument device, IPAddress? bindAddress = null, int port = 0)
    {
        _device = device;
        _listener = new TcpListener(bindAddress ?? IPAddress.Loopback, port);
        _device.ServiceRequested += OnServiceRequested;
    }

    /// <summary>The TCP port the server listens on (valid after Start).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Whether the server starts sessions in (and prefers) overlapped mode.</summary>
    public bool PreferOverlap { get; set; }

    /// <summary>Largest message the server accepts (reported in AsyncMaximumMessageSizeResponse).</summary>
    public ulong MaximumMessageSize { get; set; } = 1024 * 1024;

    /// <summary>Highest protocol version the server implements. Default 2.0.</summary>
    public ushort ProtocolVersion { get; set; } = 0x0200;

    /// <summary>Server certificate. Setting it enables the HiSLIP 2.0 secure connection capability.</summary>
    public X509Certificate2? Certificate { get; set; }

    /// <summary>Only encrypted connections are accepted.</summary>
    public bool EncryptionMandatory { get; set; }

    /// <summary>Clients must establish a secure connection right after initialization (encryption may be ended later).</summary>
    public bool InitialEncryptionRequired { get; set; }

    /// <summary>Clients must authenticate before exchanging data when the connection is encrypted. Default true.</summary>
    public bool RequireAuthentication { get; set; } = true;

    /// <summary>Offered SASL mechanisms, in preference order.</summary>
    public IList<string> SaslMechanisms { get; } = new List<string> { "SCRAM-SHA-256-PLUS", "SCRAM-SHA-256", "PLAIN", "ANONYMOUS" };

    /// <summary>Accepted user names and passwords.</summary>
    public IDictionary<string, string> Users { get; } = new Dictionary<string, string>();

    /// <summary>Client certificates accepted by EXTERNAL. Empty means any client certificate is accepted.</summary>
    public X509Certificate2Collection TrustedClientCertificates { get; } = new();

    /// <summary>Text of the TLS information descriptor.</summary>
    public string TlsInformation { get; set; } = "TLS 1.2, TLS 1.3; VisaCs simulator";

    /// <summary>Number of successful authentications.</summary>
    public int AuthenticationCount => Volatile.Read(ref _authentications);

    /// <summary>Number of authentication attempts that ended with a failed AuthenticationResult.</summary>
    public int FailedAuthenticationCount => Volatile.Read(ref _failedAuthentications);

    /// <summary>Delay before answering AsyncStartTLS, for tests of a client that gives up waiting. Default 0.</summary>
    public int StartTlsResponseDelayMilliseconds { get; set; }

    /// <summary>The user and mechanism of the most recent successful authentication.</summary>
    public (string? User, string? Mechanism) LastAuthentication { get; private set; }

    /// <summary>Number of HiSLIP sessions opened so far.</summary>
    public int SessionCount
    {
        get
        {
            lock (_sync) return _sessions.Count;
        }
    }

    /// <summary>Number of messages received with the RMT-delivered bit set.</summary>
    public int RmtDeliveredCount
    {
        get
        {
            lock (_sync) return _sessions.Values.Sum(s => s.RmtDeliveredCount);
        }
    }

    /// <summary>Creates a self-signed RSA certificate for <paramref name="hostName"/> (also valid for 127.0.0.1 and ::1).</summary>
    public static X509Certificate2 CreateSelfSignedCertificate(string hostName = "localhost", DateTimeOffset? notAfter = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={hostName}, O=VisaCs Simulator", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(hostName);
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), notAfter ?? DateTimeOffset.UtcNow.AddYears(1));
        // Round-trip through PFX so the private key is usable by SChannel on Windows.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>Starts listening.</summary>
    public void Start()
    {
        _listener.Start();
        new Thread(AcceptLoop) { IsBackground = true, Name = "HiSLIP sim accept" }.Start();
    }

    private void AcceptLoop()
    {
        while (!_disposed)
        {
            Socket socket;
            try
            {
                socket = _listener.AcceptSocket();
            }
            catch
            {
                if (_disposed) return;
                continue;
            }
            socket.NoDelay = true;
            new Thread(() => Serve(socket)) { IsBackground = true, Name = "HiSLIP sim connection" }.Start();
        }
    }

    private static void Send(HislipChannel channel, Lock gate, HislipMessageType type, byte control, uint parameter, ReadOnlySpan<byte> payload)
    {
        lock (gate) SyncProtocolIO.SendHislip(channel, type, control, parameter, payload);
    }

    private static Deadline HandshakeDeadline => Deadline.FromMilliseconds(15000);

    private SslServerAuthenticationOptions TlsOptions() => new()
    {
        ServerCertificate = Certificate,
        ClientCertificateRequired = SaslMechanisms.Contains("EXTERNAL", StringComparer.OrdinalIgnoreCase),
        RemoteCertificateValidationCallback = (_, _, _, _) => true, // client certificates are checked by EXTERNAL
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
    };

    private void Serve(Socket socket)
    {
        Session? session = null;
        HislipChannel channel = new PlainHislipChannel(socket);
        bool isSync = false;
        try
        {
            var first = SyncProtocolIO.ReceiveHislip(channel);
            if (first.Type == HislipMessageType.Initialize)
            {
                string subAddress = first.PayloadText;
                if (subAddress.Length > 0 && !subAddress.StartsWith("hislip", StringComparison.OrdinalIgnoreCase))
                {
                    SyncProtocolIO.SendHislip(channel, HislipMessageType.FatalError, 3, 0, Encoding.ASCII.GetBytes("Unknown sub-address"));
                    return;
                }
                ushort version = Math.Min((ushort)(first.Parameter >> 16), ProtocolVersion);
                lock (_sync)
                {
                    session = new Session { Id = _nextSessionId++, Sync = channel, Overlap = PreferOverlap, Version = version };
                    _sessions[session.Id] = session;
                }
                isSync = true;
                byte control = (byte)(PreferOverlap ? 1 : 0);
                if (version >= 0x0200 && Certificate is not null)
                {
                    if (EncryptionMandatory) control |= 2 | 4;
                    if (InitialEncryptionRequired) control |= 4;
                }
                SyncProtocolIO.SendHislip(channel, HislipMessageType.InitializeResponse, control, (uint)(version << 16 | session.Id),
                    ReadOnlySpan<byte>.Empty);
                SyncLoop(session);
            }
            else if (first.Type == HislipMessageType.AsyncInitialize)
            {
                lock (_sync) _sessions.TryGetValue((ushort)(first.Parameter & 0xFFFF), out session);
                if (session is null || session.Async is not null)
                {
                    SyncProtocolIO.SendHislip(channel, HislipMessageType.FatalError, 3, 0, Encoding.ASCII.GetBytes("Invalid session"));
                    return;
                }
                session.Async = channel;
                byte capabilities = (byte)(session.Version >= 0x0200 && Certificate is not null ? 1 : 0);
                SyncProtocolIO.SendHislip(channel, HislipMessageType.AsyncInitializeResponse, capabilities, 0x5349 /* "SI" */, ReadOnlySpan<byte>.Empty);
                AsyncLoop(session);
            }
            else
            {
                SyncProtocolIO.SendHislip(channel, HislipMessageType.FatalError, 3, 0, Encoding.ASCII.GetBytes("Expected Initialize"));
            }
        }
        catch
        {
            // Connection closed.
        }
        finally
        {
            if (session is not null && isSync) EndSession(session);
            (isSync && session is not null ? session.Sync : channel).Dispose();
        }
    }

    private void EndSession(Session session)
    {
        lock (_sync)
        {
            _sessions.Remove(session.Id);
            if (_exclusiveOwner == session) _exclusiveOwner = null;
            if (_sharedHolders.Remove(session) && _sharedHolders.Count == 0) _sharedKey = null;
            Monitor.PulseAll(_sync);
        }
        session.Async?.Dispose();
    }

    /// <summary>True if this session must not exchange data yet (encryption or authentication outstanding).</summary>
    private bool SecurityBlocks(Session session)
    {
        if (session.Version < 0x0200 || Certificate is null) return false;
        if (EncryptionMandatory) return !session.Encrypted || !session.Authenticated;
        // Initial encryption: authenticate once over TLS; the client may then switch encryption off.
        if (InitialEncryptionRequired && !session.EverAuthenticated) return true;
        return session.Encrypted && RequireAuthentication && !session.Authenticated;
    }

    private void Fatal(Session session, string message)
    {
        var text = Encoding.ASCII.GetBytes(message);
        try { Send(session.Sync, session.SyncSend, HislipMessageType.FatalError, 5, 0, text); } catch { /* closing */ }
        if (session.Async is { } a)
        {
            try { Send(a, session.AsyncSend, HislipMessageType.FatalError, 5, 0, text); } catch { /* closing */ }
        }
        throw new IOException(message);
    }

    private byte[] Descriptors() => SyncProtocolIO.EncodeDescriptors(
    [
        new HislipDescriptor(HislipDescriptorType.SupportedTlsVersions, [0x03, 0x03, 0x03, 0x04]),
        new HislipDescriptor(HislipDescriptorType.TlsInformation, Encoding.ASCII.GetBytes(TlsInformation)),
        new HislipDescriptor(HislipDescriptorType.TlsLastError, Encoding.ASCII.GetBytes("")),
    ]);

    private void SyncLoop(Session session)
    {
        Span<byte> header = stackalloc byte[Hislip.HeaderSize];
        while (!_disposed)
        {
            session.Sync.ReceiveExact(header);
            var h = Hislip.ParseHeader(header);
            if (h.PayloadLength > MaximumMessageSize)
            {
                session.Sync.Skip((long)h.PayloadLength);
                Send(session.Sync, session.SyncSend, HislipMessageType.Error, 4, 0, Encoding.ASCII.GetBytes("Message too large"));
                continue;
            }
            var payload = new byte[(int)h.PayloadLength];
            session.Sync.ReceiveExact(payload);
            if ((h.Type is HislipMessageType.Data or HislipMessageType.DataEnd or HislipMessageType.Trigger) && (h.Control & 1) != 0)
                Interlocked.Increment(ref session.RmtDeliveredCount);

            switch (h.Type)
            {
                case HislipMessageType.Data:
                case HislipMessageType.DataEnd:
                case HislipMessageType.Trigger:
                    if (SecurityBlocks(session)) Fatal(session, "Secure connection and authentication required");
                    if (h.Type == HislipMessageType.Trigger)
                    {
                        _device.Trigger();
                        break;
                    }
                    session.Input.AddRange(payload);
                    if (h.Type == HislipMessageType.DataEnd)
                    {
                        var message = session.Input.ToArray();
                        session.Input.Clear();
                        int generation = Volatile.Read(ref session.ClearGeneration);
                        var responses = _device.Process(message);
                        // A device clear that arrived while the command executed abandons its output (IVI-6.1 6.12).
                        if (generation != Volatile.Read(ref session.ClearGeneration)) break;
                        foreach (var response in responses)
                            SendResponse(session, [.. response, (byte)'\n'], h.Parameter);
                    }
                    break;
                case HislipMessageType.DeviceClearComplete:
                    session.Input.Clear();
                    session.Overlap = (h.Control & 1) != 0;
                    byte features = (byte)(session.Overlap ? 1 : 0);
                    if (session.Version >= 0x0200 && Certificate is not null)
                    {
                        if (EncryptionMandatory) features |= 2 | 4;
                        if (InitialEncryptionRequired) features |= 4;
                    }
                    Send(session.Sync, session.SyncSend, HislipMessageType.DeviceClearAcknowledge, features, 0, ReadOnlySpan<byte>.Empty);
                    break;
                case HislipMessageType.StartTls when Certificate is not null && !session.Encrypted:
                    lock (session.SyncSend) session.Sync = SyncProtocolIO.AuthenticateAsServer(session.Sync.Socket, TlsOptions(), HandshakeDeadline);
                    session.Authenticated = false;
                    break;
                case HislipMessageType.EndTls when session.Sync is TlsHislipChannel tls:
                    lock (session.SyncSend) session.Sync = tls.EndTls(HandshakeDeadline);
                    session.Authenticated = false;
                    break;
                case HislipMessageType.GetDescriptors:
                    Send(session.Sync, session.SyncSend, HislipMessageType.GetDescriptorsResponse, 0, 0, Descriptors());
                    break;
                case HislipMessageType.GetSaslMechanismList:
                    Send(session.Sync, session.SyncSend, HislipMessageType.GetSaslMechanismListResponse, 0, 0,
                        Encoding.ASCII.GetBytes(string.Join(' ', SaslMechanisms)));
                    break;
                case HislipMessageType.AuthenticationStart:
                {
                    string mechanism = Encoding.ASCII.GetString(payload);
                    if (!session.Encrypted || !SaslMechanisms.Contains(mechanism, StringComparer.OrdinalIgnoreCase))
                    {
                        session.Mechanism = null;
                        Send(session.Sync, session.SyncSend, HislipMessageType.Error, 5, 0, Encoding.ASCII.GetBytes($"Unsupported mechanism {mechanism}"));
                        break;
                    }
                    session.Mechanism = mechanism.ToUpperInvariant();
                    session.ExchangeStep = 0;
                    session.Scram = null;
                    break;
                }
                case HislipMessageType.AuthenticationExchange:
                    if (session.Mechanism is null) break; // the start was rejected; ignore its exchange
                    Authenticate(session, payload);
                    break;
                default:
                    Send(session.Sync, session.SyncSend, HislipMessageType.Error, 1, 0, Encoding.ASCII.GetBytes("Unrecognized message type"));
                    break;
            }
        }
    }

    private void AuthenticationResult(Session session, bool success, string? user, byte[]? data = null, string? error = null)
    {
        if (success)
        {
            session.Authenticated = true;
            session.EverAuthenticated = true;
            Interlocked.Increment(ref _authentications);
            LastAuthentication = (user, session.Mechanism);
            Send(session.Sync, session.SyncSend, HislipMessageType.AuthenticationResult, 1, 0, data ?? []);
        }
        else
        {
            session.Authenticated = false;
            Interlocked.Increment(ref _failedAuthentications);
            Send(session.Sync, session.SyncSend, HislipMessageType.AuthenticationResult, 0, 1, Encoding.UTF8.GetBytes(error ?? "Authentication failed"));
        }
        session.Mechanism = null;
        session.Scram = null;
    }

    private void Authenticate(Session session, byte[] data)
    {
        var tls = (TlsHislipChannel)session.Sync;
        switch (session.Mechanism)
        {
            case "ANONYMOUS":
                AuthenticationResult(session, true, "anonymous:" + Encoding.UTF8.GetString(data));
                return;
            case "PLAIN":
            {
                var parts = Encoding.UTF8.GetString(data).Split('\0');
                bool ok = parts.Length == 3 && Users.TryGetValue(parts[1], out var password) && password == parts[2];
                AuthenticationResult(session, ok, ok ? parts[1] : null, error: "Invalid user name or password");
                return;
            }
            case "EXTERNAL":
            {
                var certificate = tls.RemoteCertificate;
                bool ok = certificate is not null &&
                          (TrustedClientCertificates.Count == 0 || TrustedClientCertificates.Any(c => c.RawData.AsSpan().SequenceEqual(certificate.RawData)));
                AuthenticationResult(session, ok, certificate?.Subject, error: "No acceptable client certificate");
                return;
            }
            case "SCRAM-SHA-256":
            case "SCRAM-SHA-256-PLUS":
            {
                if (session.ExchangeStep++ == 0)
                {
                    bool plus = session.Mechanism.EndsWith("-PLUS", StringComparison.Ordinal);
                    session.Scram = new ScramSha256ServerExchange(user => Users.TryGetValue(user, out var p) ? p : null,
                        TlsHislipChannel.ComputeServerEndPointBinding(Certificate!), plus,
                        SaslMechanisms.Contains("SCRAM-SHA-256-PLUS", StringComparer.OrdinalIgnoreCase));
                    var serverFirst = session.Scram.First(data);
                    if (serverFirst is null)
                    {
                        AuthenticationResult(session, false, null, error: "SCRAM negotiation failed");
                        return;
                    }
                    Send(session.Sync, session.SyncSend, HislipMessageType.AuthenticationExchange, 0, 0, serverFirst);
                    return;
                }
                var serverFinal = session.Scram!.Final(data);
                AuthenticationResult(session, serverFinal is not null, session.Scram.User, serverFinal, "Invalid SCRAM proof or channel binding");
                return;
            }
            default:
                AuthenticationResult(session, false, null, error: "Unsupported mechanism");
                return;
        }
    }

    private static void SendResponse(Session session, byte[] response, uint messageId)
    {
        ulong max = session.ClientMaxMessageSize;
        int chunk = max <= Hislip.HeaderSize ? response.Length : (int)Math.Min(max - Hislip.HeaderSize, int.MaxValue);
        chunk = Math.Max(1, chunk);
        int offset = 0;
        do
        {
            int n = Math.Min(chunk, response.Length - offset);
            bool last = offset + n >= response.Length;
            Send(session.Sync, session.SyncSend, last ? HislipMessageType.DataEnd : HislipMessageType.Data, 0, messageId,
                response.AsSpan(offset, n));
            offset += n;
        } while (offset < response.Length);
    }

    private void AsyncLoop(Session session)
    {
        while (!_disposed)
        {
            var message = SyncProtocolIO.ReceiveHislip(session.Async!);
            var socket = session.Async!;
            switch (message.Type)
            {
                case HislipMessageType.AsyncMaximumMessageSize:
                {
                    if (message.Payload.Length >= 8) session.ClientMaxMessageSize = BinaryPrimitives.ReadUInt64BigEndian(message.Payload);
                    Span<byte> size = stackalloc byte[8];
                    BinaryPrimitives.WriteUInt64BigEndian(size, MaximumMessageSize);
                    Send(socket, session.AsyncSend, HislipMessageType.AsyncMaximumMessageSizeResponse, 0, 0, size);
                    break;
                }
                case HislipMessageType.AsyncStartTls:
                {
                    bool ok = Certificate is not null && session.Version >= 0x0200 && !session.Async!.IsEncrypted;
                    if (StartTlsResponseDelayMilliseconds > 0) Thread.Sleep(StartTlsResponseDelayMilliseconds);
                    lock (session.AsyncSend)
                    {
                        SyncProtocolIO.SendHislip(socket, HislipMessageType.AsyncStartTlsResponse, (byte)(ok ? 1 : 3), 0, ReadOnlySpan<byte>.Empty);
                        if (ok) session.Async = SyncProtocolIO.AuthenticateAsServer(socket.Socket, TlsOptions(), HandshakeDeadline);
                    }
                    break;
                }
                case HislipMessageType.AsyncEndTls:
                {
                    bool ok = socket is TlsHislipChannel && !EncryptionMandatory;
                    lock (session.AsyncSend)
                    {
                        SyncProtocolIO.SendHislip(socket, HislipMessageType.AsyncEndTlsResponse, (byte)(ok ? 1 : 3), 0, ReadOnlySpan<byte>.Empty);
                        if (ok) session.Async = ((TlsHislipChannel)socket).EndTls(HandshakeDeadline);
                    }
                    break;
                }
                case HislipMessageType.GetDescriptors:
                    Send(socket, session.AsyncSend, HislipMessageType.GetDescriptorsResponse, 0, 0, Descriptors());
                    break;
                case HislipMessageType.AsyncStatusQuery:
                    if (SecurityBlocks(session)) Fatal(session, "Secure connection and authentication required");
                    if ((message.Control & 1) != 0) Interlocked.Increment(ref session.RmtDeliveredCount);
                    Send(socket, session.AsyncSend, HislipMessageType.AsyncStatusResponse, _device.GetStatusByte(false), 0, ReadOnlySpan<byte>.Empty);
                    break;
                case HislipMessageType.AsyncDeviceClear:
                    Interlocked.Increment(ref session.ClearGeneration);
                    _device.DeviceClear();
                    Send(socket, session.AsyncSend, HislipMessageType.AsyncDeviceClearAcknowledge, (byte)(PreferOverlap ? 1 : 0), 0, ReadOnlySpan<byte>.Empty);
                    break;
                case HislipMessageType.AsyncLock:
                {
                    if (SecurityBlocks(session)) Fatal(session, "Secure connection and authentication required");
                    byte result = message.Control == 1
                        ? (byte)(AcquireLock(session, message.PayloadText, message.Parameter) ? 1 : 0)
                        : ReleaseLock(session);
                    Send(socket, session.AsyncSend, HislipMessageType.AsyncLockResponse, result, 0, ReadOnlySpan<byte>.Empty);
                    break;
                }
                case HislipMessageType.AsyncLockInfo:
                {
                    byte exclusive;
                    uint count;
                    lock (_sync)
                    {
                        exclusive = (byte)(_exclusiveOwner is not null ? 1 : 0);
                        count = (uint)(_sharedHolders.Count + exclusive);
                    }
                    Send(socket, session.AsyncSend, HislipMessageType.AsyncLockInfoResponse, exclusive, count, ReadOnlySpan<byte>.Empty);
                    break;
                }
                case HislipMessageType.AsyncRemoteLocalControl:
                    _device.IsRemote = message.Control is 1 or 3 or 4 or 5;
                    _device.LocalLockout = message.Control is 4 or 5;
                    Send(socket, session.AsyncSend, HislipMessageType.AsyncRemoteLocalResponse, 0, 0, ReadOnlySpan<byte>.Empty);
                    break;
                default:
                    Send(socket, session.AsyncSend, HislipMessageType.Error, 1, 0, Encoding.ASCII.GetBytes("Unrecognized message type"));
                    break;
            }
        }
    }

    private bool AcquireLock(Session session, string key, uint timeoutMs)
    {
        lock (_sync)
        {
            var deadline = Deadline.FromMilliseconds(timeoutMs >= int.MaxValue ? -1 : (int)timeoutMs);
            while (true)
            {
                // A session that closed while waiting must not end up owning the lock.
                if (_disposed || !_sessions.ContainsKey(session.Id)) return false;
                if (key.Length == 0)
                {
                    if (_exclusiveOwner == session) return true;
                    if (_exclusiveOwner is null && _sharedHolders.All(s => s == session))
                    {
                        _exclusiveOwner = session;
                        return true;
                    }
                }
                else
                {
                    if (_exclusiveOwner is null || _exclusiveOwner == session)
                    {
                        if (_sharedKey is null || _sharedKey == key)
                        {
                            _sharedKey = key;
                            _sharedHolders.Add(session);
                            return true;
                        }
                    }
                }
                int remaining = deadline.RemainingMilliseconds;
                if (remaining == 0) return false;
                Monitor.Wait(_sync, remaining);
            }
        }
    }

    private byte ReleaseLock(Session session)
    {
        lock (_sync)
        {
            try
            {
                if (_exclusiveOwner == session)
                {
                    _exclusiveOwner = null;
                    return 1;
                }
                if (_sharedHolders.Remove(session))
                {
                    if (_sharedHolders.Count == 0) _sharedKey = null;
                    return 2;
                }
                return 3;
            }
            finally
            {
                Monitor.PulseAll(_sync);
            }
        }
    }

    private void OnServiceRequested(byte stb)
    {
        List<Session> sessions;
        lock (_sync) sessions = _sessions.Values.Where(s => s.Async is not null).ToList();
        foreach (var s in sessions)
        {
            try { Send(s.Async!, s.AsyncSend, HislipMessageType.AsyncServiceRequest, stb, 0, ReadOnlySpan<byte>.Empty); }
            catch { /* session closing */ }
        }
    }

    /// <summary>Stops the server and closes its connections.</summary>
    public void Dispose()
    {
        _disposed = true;
        _device.ServiceRequested -= OnServiceRequested;
        try { _listener.Stop(); } catch { /* ignore */ }
        List<Session> sessions;
        lock (_sync)
        {
            sessions = _sessions.Values.ToList();
            Monitor.PulseAll(_sync);
        }
        foreach (var s in sessions)
        {
            s.Sync.Dispose();
            s.Async?.Dispose();
        }
    }
}

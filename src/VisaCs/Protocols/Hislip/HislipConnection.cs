using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VisaCs.Internal;

namespace VisaCs.Protocols.Hislip;

/// <summary>
/// One HiSLIP session: a synchronous channel carrying data, and an asynchronous channel carrying service requests,
/// status queries, locks, device clear, remote/local control and (HiSLIP 2.0) the TLS switch-over. Every operation
/// takes a <c>bool async</c>: false blocks the calling thread, true holds none while waiting.
/// </summary>
/// <remarks>
/// <para>
/// Synchronous-channel I/O is serialized by the owning session. Requests on the asynchronous channel are serialized
/// by a gate that a request waits for no longer than its own deadline. A background read loop (asynchronous, holding
/// no thread while idle) receives everything the server sends on the asynchronous channel; it pauses while
/// StartTLS/EndTLS switch that channel, and the switch holds the gate so no other request can write into it.
/// </para>
/// <para>
/// Responses carry no request ID, so they are matched by order: when a request times out, its late response is
/// dropped when it arrives, and a lock that the server grants after the caller gave up is released again.
/// </para>
/// <para>
/// The connection closes itself when the protocol state becomes unknown: a message only partly sent, a fatal error,
/// a failed or timed-out TLS switch, or failed authentication. Later calls then throw VI_ERROR_CONN_LOST.
/// </para>
/// </remarks>
internal sealed partial class HislipConnection : IDisposable
{
    /// <summary>Vendor ID this client sends in Initialize ("CS").</summary>
    private const ushort VendorId = 0x4353;

    private const int LockReplySlackMs = 2000;

    private volatile HislipChannel _sync;
    private volatile HislipChannel _async;
    private readonly SemaphoreSlim _asyncRequestGate = new(1, 1);
    private readonly Lock _responseSync = new();
    private readonly Queue<HislipMessage> _responses = new();
    private readonly Queue<AbandonedRequest> _abandoned = new();
    private HislipMessageType? _awaitedResponse;
    private readonly ChangeSignal _responseSignal = new();
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource? _readerResume;
    private readonly byte[] _header = new byte[Hislip.HeaderSize];
    private int _headerFilled;
    private volatile bool _closed;
    private volatile string? _closeReason;
    private bool _disposed;
    private readonly string _host;

    private uint _messageId = Hislip.InitialMessageId;
    private uint _mostRecentMessageId = Hislip.InitialMessageId - 2;
    private uint _lastDeliveredMessageId = Hislip.InitialMessageId - 2;
    private bool _anySent;
    private volatile bool _rmtDelivered;

    // State of the Data/DataEND message currently being received on the sync channel.
    private long _payloadRemaining;
    private bool _payloadIsEnd;
    private bool _payloadDiscard;
    private uint _payloadMessageId;

    /// <summary>A request whose caller stopped waiting; its response is dropped when it arrives.</summary>
    private readonly record struct AbandonedRequest(HislipMessageType Response, bool LockRequest);

    private HislipConnection(HislipChannel sync, HislipChannel async, string host)
    {
        _sync = sync;
        _async = async;
        _host = host;
    }

    public ushort SessionId { get; private set; }

    /// <summary>The protocol version both sides use: the lower of the server's and <see cref="Hislip.ProtocolVersion"/>.</summary>
    public ushort NegotiatedProtocolVersion { get; private set; }

    public ushort ServerVendorId { get; private set; }
    public bool OverlapMode { get; private set; }
    public ulong ServerMaxMessageSize { get; private set; } = ulong.MaxValue;
    public Socket SyncSocket => _sync.Socket;
    public Socket AsyncSocket => _async.Socket;
    public bool IsClosed => _closed;

    /// <summary>HiSLIP 2.0: the server supports secure connections (AsyncInitializeResponse capability bit 0).</summary>
    public bool SecureConnectionSupported { get; private set; }

    /// <summary>HiSLIP 2.0: the server only accepts encrypted connections.</summary>
    public bool EncryptionMandatory { get; private set; }

    /// <summary>
    /// HiSLIP 2.0: the client must establish a secure connection right after initialization (set when the server
    /// requests initial encryption or makes encryption mandatory).
    /// </summary>
    public bool InitialEncryptionRequired { get; private set; }

    public bool IsEncrypted => _sync.IsEncrypted;

    /// <summary>The SASL mechanism the client authenticated with, if any.</summary>
    public string? SaslMechanism { get; private set; }

    public X509Certificate2? ServerCertificate { get; private set; }

    public TlsCipherSuite? CipherSuite { get; private set; }

    public SslProtocols? TlsProtocol { get; private set; }

    /// <summary>Raised by the async-channel read loop when the server sends AsyncServiceRequest. Argument is the status byte.</summary>
    public event Action<byte>? ServiceRequest;

    /// <summary>Set after the RMT (response message terminator) of a response was delivered to the application.</summary>
    public void NotifyRmtDelivered() => _rmtDelivered = true;

    /// <summary>
    /// Opens both channels, applies <paramref name="security"/> and negotiates the maximum message size.
    /// <paramref name="deadline"/> covers all of it, including the TLS handshakes and authentication.
    /// </summary>
    public static async ValueTask<HislipConnection> ConnectAsync(string host, int port, string subAddress, Deadline deadline,
        ulong clientMaxMessageSize, HislipSecurityOptions? security, bool async)
    {
        security ??= new HislipSecurityOptions();
        HislipChannel? sync = null, asyncChannel = null;
        try
        {
            sync = new PlainHislipChannel(await NetUtil.ConnectAsync(host, port, deadline, async).ConfigureAwait(false));
            await Hislip.SendAsync(sync, HislipMessageType.Initialize, 0, (uint)(Hislip.ProtocolVersion << 16 | VendorId),
                Encoding.ASCII.GetBytes(subAddress), deadline, async).ConfigureAwait(false);
            var response = await Hislip.ReceiveAsync(sync, deadline, async).ConfigureAwait(false);
            if (response.Type == HislipMessageType.FatalError)
                throw new VisaException(VisaStatus.ErrorResourceNotFound,
                    $"HiSLIP server refused the connection: {Hislip.DescribeFatalError(response.Control)} {response.PayloadText}".Trim());
            if (response.Type != HislipMessageType.InitializeResponse)
                throw new VisaException(VisaStatus.ErrorIO, $"HiSLIP protocol error: expected InitializeResponse, got {response.Type}.");

            ushort negotiated = (ushort)(response.Parameter >> 16);
            ushort sessionId = (ushort)(response.Parameter & 0xFFFF);
            bool v2 = negotiated >= Hislip.ProtocolVersion20;
            // The second channel goes to the address the first one reached, not to another address of the host name.
            asyncChannel = new PlainHislipChannel(await NetUtil.ConnectAsync((IPEndPoint)sync.Socket.RemoteEndPoint!, deadline, async,
                hostForMessages: host).ConfigureAwait(false));
            await Hislip.SendAsync(asyncChannel, HislipMessageType.AsyncInitialize, 0, sessionId, ReadOnlyMemory<byte>.Empty, deadline, async)
                .ConfigureAwait(false);
            var asyncResponse = await Hislip.ReceiveAsync(asyncChannel, deadline, async).ConfigureAwait(false);
            if (asyncResponse.Type == HislipMessageType.FatalError)
                throw new VisaException(VisaStatus.ErrorResourceNotFound,
                    $"HiSLIP server refused the async channel: {Hislip.DescribeFatalError(asyncResponse.Control)} {asyncResponse.PayloadText}".Trim());
            if (asyncResponse.Type != HislipMessageType.AsyncInitializeResponse)
                throw new VisaException(VisaStatus.ErrorIO, $"HiSLIP protocol error: expected AsyncInitializeResponse, got {asyncResponse.Type}.");

            var connection = new HislipConnection(sync, asyncChannel, host)
            {
                SessionId = sessionId,
                OverlapMode = (response.Control & 1) != 0,
                NegotiatedProtocolVersion = negotiated,
                ServerVendorId = (ushort)(asyncResponse.Parameter & 0xFFFF),
                EncryptionMandatory = v2 && (response.Control & 2) != 0,
                InitialEncryptionRequired = v2 && (response.Control & 6) != 0,
                SecureConnectionSupported = v2 && (asyncResponse.Control & 1) != 0,
            };
            sync = asyncChannel = null; // owned by the connection now
            connection.StartReader();
            try
            {
                // Security first: a server that requires initial encryption expects StartTLS before anything else.
                await connection.ApplySecurityPolicyAsync(security, deadline, async).ConfigureAwait(false);
                await connection.NegotiateMaximumMessageSizeAsync(clientMaxMessageSize, deadline, async).ConfigureAwait(false);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
            return connection;
        }
        catch
        {
            sync?.Dispose();
            asyncChannel?.Dispose();
            throw;
        }
    }

    private async ValueTask NegotiateMaximumMessageSizeAsync(ulong size, Deadline deadline, bool async)
    {
        try
        {
            await SetClientMaximumMessageSizeAsync(size, deadline, async).ConfigureAwait(false);
        }
        catch (VisaException ex) when (!_closed && !deadline.IsCanceled && (ex.Status == VisaStatus.ErrorIO || ex is VisaTimeoutException))
        {
            // Some servers do not implement AsyncMaximumMessageSize (they answer with an Error, or not at all):
            // keep the unlimited default.
        }
    }

    /// <summary>Announces the largest message this client accepts and records the server's limit from the reply.</summary>
    public async ValueTask SetClientMaximumMessageSizeAsync(ulong size, Deadline deadline, bool async)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(payload, size);
        var reply = await AsyncRequestAsync(HislipMessageType.AsyncMaximumMessageSize, 0, 0, payload,
            HislipMessageType.AsyncMaximumMessageSizeResponse, deadline, async).ConfigureAwait(false);
        if (reply.Payload.Length >= 8) ServerMaxMessageSize = BinaryPrimitives.ReadUInt64BigEndian(reply.Payload);
    }

    // ------------------------------------------------------------------ closing

    private void ThrowIfClosed()
    {
        if (_closed)
            throw new VisaException(VisaStatus.ErrorConnectionLost,
                _closeReason is { } reason ? $"The HiSLIP connection was closed: {reason}." : "The HiSLIP connection is closed.");
    }

    /// <summary>Closes the connection because its protocol state is unknown; later calls report <paramref name="reason"/>.</summary>
    private void Fail(string reason)
    {
        _closeReason ??= reason;
        Dispose();
    }

    /// <summary>The exception for a FatalError message; the connection is closed (the server closes it too).</summary>
    private VisaException Fatal(HislipMessage message)
    {
        var exception = Hislip.FatalErrorException(message);
        Fail("the server sent a fatal error");
        return exception;
    }

    /// <summary>
    /// Sends a message on <paramref name="channel"/>. If a failure leaves the server inside the message (part of it was
    /// sent, or the TLS session broke), the connection is closed: the server would read whatever follows as the rest.
    /// </summary>
    private async ValueTask SendAsync(HislipChannel channel, HislipMessageType type, byte control, uint parameter, ReadOnlyMemory<byte> payload,
        Deadline deadline, bool async)
    {
        try
        {
            await Hislip.SendAsync(channel, type, control, parameter, payload, deadline, async).ConfigureAwait(false);
        }
        catch (Exception ex) when (NetUtil.MayHaveSentPart(ex) || channel is TlsHislipChannel { IsBroken: true })
        {
            Fail($"a {type} message was only partly sent");
            if (ex is VisaTimeoutException timeout)
                throw new VisaTimeoutException("The send timed out after part of a HiSLIP message was sent; the connection was closed.", ex)
                    { BytesTransferred = timeout.BytesTransferred };
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, true)) return;
        _closed = true;
        _stop.Cancel();
        _sync.Dispose();
        _async.Dispose();
        lock (_responseSync) _responseSignal.Pulse();
    }

    // ------------------------------------------------------------------ async channel

    private void StartReader() => _ = Task.Run(ReadLoopAsync);

    private async Task ReadLoopAsync()
    {
        var header = new byte[Hislip.HeaderSize];
        var untilClosed = Deadline.Infinite.WithCancellation(_stop.Token);
        try
        {
            while (!_closed)
            {
                var message = await Hislip.ReceiveAsync(_async, untilClosed, async: true, header).ConfigureAwait(false);
                switch (message.Type)
                {
                    case HislipMessageType.AsyncServiceRequest:
                        try { ServiceRequest?.Invoke(message.Control); } catch { /* handler errors are not fatal */ }
                        break;
                    case HislipMessageType.AsyncInterrupted:
                        // Synchronized mode: the server discarded a response because a new message arrived.
                        break;
                    case HislipMessageType.AsyncStartTlsResponse or HislipMessageType.AsyncEndTlsResponse when message.Control == 1:
                        // The bytes that follow belong to the TLS handshake or close_notify: stop reading until the
                        // requester has switched the channel (it calls ResumeReader).
                        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        Volatile.Write(ref _readerResume, resume);
                        PostResponse(message);
                        await resume.Task.WaitAsync(_stop.Token).ConfigureAwait(false);
                        break;
                    default:
                        PostResponse(message);
                        if (message.Type == HislipMessageType.FatalError) return;
                        break;
                }
            }
        }
        catch
        {
            if (!_closed)
                PostResponse(new HislipMessage(new HislipHeader(HislipMessageType.FatalError, 0, 0, 0), "async channel closed"u8.ToArray()));
        }
    }

    private void ResumeReader() => Interlocked.Exchange(ref _readerResume, null)?.TrySetResult();

    private void PostResponse(HislipMessage message)
    {
        bool releaseLock = false;
        lock (_responseSync)
        {
            if (message.Type != HislipMessageType.FatalError && _abandoned.TryPeek(out var head) &&
                (message.Type == head.Response || message.Type == HislipMessageType.Error))
            {
                // The late answer to a request whose caller stopped waiting.
                _abandoned.Dequeue();
                releaseLock = head.LockRequest && message.Type == HislipMessageType.AsyncLockResponse && message.Control == 1;
            }
            else
            {
                // A response to the request now waiting means the server will not answer the abandoned ones.
                if (message.Type == _awaitedResponse) _abandoned.Clear();
                _responses.Enqueue(message);
                _responseSignal.Pulse();
            }
        }
        if (releaseLock) _ = ReleaseAbandonedLockAsync();
    }

    /// <summary>Releases a lock the server granted after its requester had given up.</summary>
    private async Task ReleaseAbandonedLockAsync()
    {
        try
        {
            // Wait as long as it takes (or until the connection closes, which releases the lock too).
            if (!await _asyncRequestGate.EnterAsync(Deadline.Infinite.WithCancellation(_stop.Token), async: true).ConfigureAwait(false)) return;
            try
            {
                if (_closed) return;
                lock (_responseSync) _abandoned.Enqueue(new AbandonedRequest(HislipMessageType.AsyncLockResponse, LockRequest: false));
                await SendAsync(_async, HislipMessageType.AsyncLock, 0, MostRecentMessageId, ReadOnlyMemory<byte>.Empty,
                    Deadline.FromMilliseconds(5000), async: true).ConfigureAwait(false);
            }
            finally
            {
                _asyncRequestGate.Release();
            }
        }
        catch (Exception)
        {
            // Nothing was sent, or the connection is gone; either way no answer will need to be dropped.
            Fail("releasing a lock granted after its request was abandoned failed");
        }
    }

    /// <summary>Sends a request on the async channel and waits for the matching response type.</summary>
    public async ValueTask<HislipMessage> AsyncRequestAsync(HislipMessageType type, byte control, uint parameter, ReadOnlyMemory<byte> payload,
        HislipMessageType expected, Deadline deadline, bool async)
    {
        await EnterAsyncChannelAsync(deadline, async).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            DiscardStaleResponses();
            await SendAsync(_async, type, control, parameter, payload, deadline, async).ConfigureAwait(false);
            return await WaitForResponseAsync(type, control, expected, deadline, async).ConfigureAwait(false);
        }
        finally
        {
            _asyncRequestGate.Release();
        }
    }

    private ValueTask EnterAsyncChannelAsync(Deadline deadline, bool async) =>
        _asyncRequestGate.EnterOrThrowAsync(deadline, async, "the HiSLIP asynchronous-channel request in progress");

    /// <summary>Drops responses nobody waited for (the next request has not been sent yet), except a fatal error.</summary>
    private void DiscardStaleResponses()
    {
        HislipMessage? fatal = null;
        lock (_responseSync)
        {
            while (fatal is null && _responses.TryDequeue(out var stale))
            {
                if (stale.Type == HislipMessageType.FatalError) fatal = stale;
            }
        }
        if (fatal is not null) throw Fatal(fatal);
    }

    private async ValueTask<HislipMessage> WaitForResponseAsync(HislipMessageType request, byte control, HislipMessageType expected,
        Deadline deadline, bool async)
    {
        lock (_responseSync) _awaitedResponse = expected;
        try
        {
            while (true)
            {
                HislipMessage? message;
                Task next;
                lock (_responseSync)
                {
                    _responses.TryDequeue(out message);
                    next = _responseSignal.Next;
                }
                if (message is null)
                {
                    ThrowIfClosed();
                    if (await ChangeSignal.WaitAsync(next, deadline, async).ConfigureAwait(false)) continue;
                    lock (_responseSync)
                    {
                        if (_responses.Count > 0) continue; // it arrived just now
                        _abandoned.Enqueue(new AbandonedRequest(expected, LockRequest: request == HislipMessageType.AsyncLock && control == 1));
                    }
                    ThrowIfClosed();
                    throw new VisaTimeoutException($"No HiSLIP {expected} received.");
                }
                if (message.Type == expected) return message;
                if (message.Type == HislipMessageType.Error) throw Hislip.ErrorException(message, $"in response to {request}");
                if (message.Type == HislipMessageType.FatalError) throw Fatal(message);
                // Anything else is unexpected; ignore it.
            }
        }
        finally
        {
            lock (_responseSync) _awaitedResponse = null;
        }
    }

    // ------------------------------------------------------------------ message IDs and RMT

    private byte TakeRmt()
    {
        byte rmt = _rmtDelivered ? (byte)1 : (byte)0;
        _rmtDelivered = false;
        return rmt;
    }

    private uint MostRecentMessageId => _anySent ? _mostRecentMessageId : Hislip.InitialMessageId - 2;

    /// <summary>AsyncStatusQuery message ID: last sent in synchronized mode, last fully delivered in overlapped mode.</summary>
    private uint StatusQueryMessageId => OverlapMode ? _lastDeliveredMessageId : MostRecentMessageId;

    private async ValueTask SendSyncDataAsync(HislipMessageType type, ReadOnlyMemory<byte> payload, Deadline deadline, bool async)
    {
        await SendAsync(_sync, type, TakeRmt(), _messageId, payload, deadline, async).ConfigureAwait(false);
        _mostRecentMessageId = _messageId;
        _anySent = true;
        _messageId = unchecked(_messageId + 2);
    }

    private void ResetMessageIds()
    {
        _messageId = Hislip.InitialMessageId;
        _mostRecentMessageId = Hislip.InitialMessageId - 2;
        _lastDeliveredMessageId = Hislip.InitialMessageId - 2;
        _anySent = false;
        _rmtDelivered = false;
    }

    // ------------------------------------------------------------------ data transfer

    /// <summary>Writes data as Data/DataEND messages, honoring the server's maximum message size.</summary>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, bool end, Deadline deadline, bool async)
    {
        ThrowIfClosed();
        if (data.IsEmpty && !end) return;

        // In synchronized mode a new message makes any unread response stale.
        if (!OverlapMode && _payloadRemaining > 0) _payloadDiscard = true;

        ulong maxMessage = ServerMaxMessageSize;
        long maxPayload = maxMessage <= Hislip.HeaderSize ? 1024 : (long)Math.Min(maxMessage - Hislip.HeaderSize, 64UL * 1024 * 1024);
        int offset = 0;
        do
        {
            int n = (int)Math.Min(data.Length - offset, maxPayload);
            bool last = offset + n >= data.Length;
            var type = last && end ? HislipMessageType.DataEnd : HislipMessageType.Data;
            await SendSyncDataAsync(type, data.Slice(offset, n), deadline, async).ConfigureAwait(false);
            offset += n;
        } while (offset < data.Length);
    }

    public ValueTask TriggerAsync(Deadline deadline, bool async)
    {
        ThrowIfClosed();
        if (!OverlapMode && _payloadRemaining > 0) _payloadDiscard = true;
        return SendSyncDataAsync(HislipMessageType.Trigger, ReadOnlyMemory<byte>.Empty, deadline, async);
    }

    private bool IsStale(uint messageId)
    {
        if (!_anySent || messageId == Hislip.UnknownMessageId) return false;
        return unchecked((int)(_mostRecentMessageId - messageId)) > 0;
    }

    private async ValueTask<HislipHeader> ReceiveSyncHeaderAsync(Deadline deadline, bool async)
    {
        // Resumable so a timeout in the middle of a header does not desynchronize the stream.
        while (_headerFilled < Hislip.HeaderSize)
            _headerFilled += await _sync.ReceiveSomeAsync(_header.AsMemory(_headerFilled), deadline, async).ConfigureAwait(false);
        _headerFilled = 0;
        return Hislip.ParseHeader(_header);
    }

    /// <summary>Reads one complete message other than Data/DataEND from the sync channel (used during authentication).</summary>
    private async ValueTask<HislipMessage> ReceiveSyncMessageAsync(Deadline deadline, bool async)
    {
        var h = await ReceiveSyncHeaderAsync(deadline, async).ConfigureAwait(false);
        return new HislipMessage(h, await Hislip.ReceivePayloadAsync(_sync, h, deadline, async).ConfigureAwait(false));
    }

    /// <summary>Input source for <see cref="InputBuffer"/>: returns payload bytes of Data/DataEND messages.</summary>
    public async ValueTask<FillResult> FillAsync(Memory<byte> target, Deadline deadline, bool async)
    {
        while (true)
        {
            ThrowIfClosed();
            if (_payloadRemaining == 0)
            {
                var h = await ReceiveSyncHeaderAsync(deadline, async).ConfigureAwait(false);
                switch (h.Type)
                {
                    case HislipMessageType.Data:
                    case HislipMessageType.DataEnd:
                        if (h.PayloadLength > long.MaxValue)
                            throw new VisaException(VisaStatus.ErrorIO, "HiSLIP protocol error: invalid payload length.");
                        _payloadRemaining = (long)h.PayloadLength;
                        _payloadIsEnd = h.Type == HislipMessageType.DataEnd;
                        _payloadMessageId = h.Parameter;
                        _payloadDiscard = !OverlapMode && IsStale(h.Parameter);
                        if (_payloadRemaining == 0)
                        {
                            if (_payloadDiscard) continue;
                            MessageDelivered();
                            if (_payloadIsEnd) return new FillResult(0, true);
                            continue;
                        }
                        break;
                    case HislipMessageType.Error:
                    {
                        var payload = await Hislip.ReceivePayloadAsync(_sync, h, deadline, async, skipIfTooLarge: true).ConfigureAwait(false);
                        throw Hislip.ErrorException(new HislipMessage(h, payload));
                    }
                    case HislipMessageType.FatalError:
                    {
                        var payload = await Hislip.ReceivePayloadAsync(_sync, h, deadline, async, skipIfTooLarge: true).ConfigureAwait(false);
                        throw Fatal(new HislipMessage(h, payload));
                    }
                    default:
                        // Interrupted (overlap mode) and anything unexpected: skip the payload.
                        await _sync.SkipAsync((long)Math.Min(h.PayloadLength, long.MaxValue), deadline, async).ConfigureAwait(false);
                        continue;
                }
            }

            if (_payloadDiscard)
            {
                while (_payloadRemaining > 0)
                {
                    int skip = (int)Math.Min(_payloadRemaining, target.Length);
                    int n = await _sync.ReceiveSomeAsync(target[..skip], deadline, async).ConfigureAwait(false);
                    _payloadRemaining -= n;
                }
                continue;
            }

            int want = (int)Math.Min(target.Length, _payloadRemaining);
            int received = await _sync.ReceiveSomeAsync(target[..want], deadline, async).ConfigureAwait(false);
            _payloadRemaining -= received;
            bool end = _payloadRemaining == 0 && _payloadIsEnd;
            if (_payloadRemaining == 0) MessageDelivered();
            return new FillResult(received, end);
        }
    }

    private void MessageDelivered() => _lastDeliveredMessageId = _payloadMessageId;

    // ------------------------------------------------------------------ device operations

    public async ValueTask<byte> ReadStatusByteAsync(Deadline deadline, bool async)
    {
        var reply = await AsyncRequestAsync(HislipMessageType.AsyncStatusQuery, TakeRmt(), StatusQueryMessageId, ReadOnlyMemory<byte>.Empty,
            HislipMessageType.AsyncStatusResponse, deadline, async).ConfigureAwait(false);
        return reply.Control;
    }

    /// <summary>Performs the HiSLIP device clear transaction. Optionally requests a new overlap mode.</summary>
    public async ValueTask DeviceClearAsync(bool? requestOverlap, Deadline deadline, bool async)
    {
        var ack = await AsyncRequestAsync(HislipMessageType.AsyncDeviceClear, 0, 0, ReadOnlyMemory<byte>.Empty,
            HislipMessageType.AsyncDeviceClearAcknowledge, deadline, async).ConfigureAwait(false);
        byte feature = (byte)(ack.Control & ~1);
        feature |= requestOverlap is { } overlap ? (byte)(overlap ? 1 : 0) : (byte)(ack.Control & 1);

        // Discard the remainder of any partially received message.
        if (_payloadRemaining > 0)
        {
            await _sync.SkipAsync(_payloadRemaining, deadline, async).ConfigureAwait(false);
            _payloadRemaining = 0;
        }

        await SendAsync(_sync, HislipMessageType.DeviceClearComplete, feature, 0, ReadOnlyMemory<byte>.Empty, deadline, async).ConfigureAwait(false);
        while (true)
        {
            var h = await ReceiveSyncHeaderAsync(deadline, async).ConfigureAwait(false);
            if (h.Type == HislipMessageType.FatalError)
            {
                var payload = await Hislip.ReceivePayloadAsync(_sync, h, deadline, async, skipIfTooLarge: true).ConfigureAwait(false);
                throw Fatal(new HislipMessage(h, payload));
            }
            await _sync.SkipAsync((long)Math.Min(h.PayloadLength, long.MaxValue), deadline, async).ConfigureAwait(false);
            if (h.Type == HislipMessageType.DeviceClearAcknowledge)
            {
                OverlapMode = (h.Control & 1) != 0;
                if (NegotiatedProtocolVersion >= Hislip.ProtocolVersion20)
                {
                    EncryptionMandatory = (h.Control & 2) != 0;
                    InitialEncryptionRequired = (h.Control & 6) != 0;
                }
                break;
            }
        }

        ResetMessageIds();
        _payloadDiscard = false;
        _payloadIsEnd = false;
    }

    public async ValueTask RemoteLocalAsync(RemoteLocalMode mode, Deadline deadline, bool async)
    {
        await AsyncRequestAsync(HislipMessageType.AsyncRemoteLocalControl, (byte)mode, MostRecentMessageId, ReadOnlyMemory<byte>.Empty,
            HislipMessageType.AsyncRemoteLocalResponse, deadline, async).ConfigureAwait(false);
    }

    /// <summary>
    /// Requests a lock. Empty <paramref name="lockString"/> means exclusive, otherwise shared with that key.
    /// <paramref name="timeoutMs"/> negative waits without limit; otherwise the server waits for the time
    /// <paramref name="deadline"/> has left (the caller may have spent part of its timeout already).
    /// </summary>
    public async ValueTask LockAsync(string lockString, int timeoutMs, Deadline deadline, bool async)
    {
        uint serverTimeout = timeoutMs < 0 ? uint.MaxValue : (uint)deadline.RemainingMilliseconds;
        var reply = await AsyncRequestAsync(HislipMessageType.AsyncLock, 1, serverTimeout, Encoding.ASCII.GetBytes(lockString),
            HislipMessageType.AsyncLockResponse, deadline.Extend(LockReplySlackMs), async).ConfigureAwait(false);
        switch (reply.Control)
        {
            case 1:
                return;
            case 0:
                throw new VisaTimeoutException("The HiSLIP lock could not be acquired before the timeout (resource is locked).");
            default:
                throw new VisaException(VisaStatus.ErrorResourceLocked, "The HiSLIP server rejected the lock request.");
        }
    }

    public async ValueTask UnlockAsync(Deadline deadline, bool async)
    {
        var reply = await AsyncRequestAsync(HislipMessageType.AsyncLock, 0, MostRecentMessageId, ReadOnlyMemory<byte>.Empty,
            HislipMessageType.AsyncLockResponse, deadline, async).ConfigureAwait(false);
        if (reply.Control == 3)
            throw new VisaException(VisaStatus.ErrorSessionNotLocked, "The HiSLIP server reports no lock held by this session.");
    }

    public async ValueTask<(bool ExclusiveGranted, uint LockCount)> LockInfoAsync(Deadline deadline, bool async)
    {
        var reply = await AsyncRequestAsync(HislipMessageType.AsyncLockInfo, 0, 0, ReadOnlyMemory<byte>.Empty,
            HislipMessageType.AsyncLockInfoResponse, deadline, async).ConfigureAwait(false);
        return (reply.Control == 1, reply.Parameter);
    }

    public async ValueTask<IReadOnlyList<HislipDescriptor>> GetDescriptorsAsync(Deadline deadline, bool async)
    {
        if (NegotiatedProtocolVersion < Hislip.ProtocolVersion20)
            throw VisaException.NotSupported("GetDescriptors (needs HiSLIP 2.0)");
        var reply = await AsyncRequestAsync(HislipMessageType.GetDescriptors, 0, 0, ReadOnlyMemory<byte>.Empty,
            HislipMessageType.GetDescriptorsResponse, deadline, async).ConfigureAwait(false);
        return Hislip.ParseDescriptors(reply.Payload);
    }
}

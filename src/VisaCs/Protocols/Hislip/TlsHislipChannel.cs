using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VisaCs.Internal;

namespace VisaCs.Protocols.Hislip;

/// <summary>
/// TLS channel. A reader loop decrypts into a buffer (at most 4 MiB ahead of the consumer), so that reads can honor
/// deadlines and cancellation without cancelling an SslStream read in the middle of a record (which would corrupt the
/// session). The loop is asynchronous and holds no thread while the peer is quiet.
/// </summary>
/// <remarks>
/// Writes cannot stop in the middle of a record either: a write that fails or times out after it started marks the
/// channel as broken (<see cref="IsBroken"/>), and later reads and writes fail. The socket is in blocking mode while
/// the channel exists, because <see cref="NetworkStream"/> requires it; all I/O still goes through asynchronous calls.
/// </remarks>
internal sealed class TlsHislipChannel : HislipChannel
{
    private readonly SslStream _ssl;
    private readonly Lock _sync = new();
    private readonly ChangeSignal _changed = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader;
    private const int MaxBuffered = 4 * 1024 * 1024;
    private readonly Queue<byte[]> _chunks = new();
    private byte[]? _current;
    private int _currentOffset;
    private int _buffered;
    private bool _eof;
    private Exception? _fault;
    private bool _disposed;
    private bool _shutdownSent;
    private bool _handedOver;
    private readonly TlsRecordBoundaryStream _records;

    private TlsHislipChannel(Socket socket, SslStream ssl, TlsRecordBoundaryStream records) : base(socket)
    {
        _ssl = ssl;
        _records = records;
        _reader = Task.Run(ReadLoopAsync);
    }

    public override bool IsEncrypted => true;

    /// <summary>True after a write failed part-way: the TLS session cannot be used any more.</summary>
    public bool IsBroken
    {
        get
        {
            lock (_sync) return _fault is not null;
        }
    }

    public SslStream Stream => _ssl;

    public X509Certificate2? RemoteCertificate =>
        _ssl.RemoteCertificate is null ? null : _ssl.RemoteCertificate as X509Certificate2 ?? new X509Certificate2(_ssl.RemoteCertificate);

    /// <summary>
    /// Performs the client handshake on <paramref name="socket"/>. Failures throw <see cref="VisaSecurityException"/>
    /// (<see cref="SecurityFailure.ServerCertificate"/> when the certificate check rejected the server, else
    /// <see cref="SecurityFailure.TlsNegotiation"/>); a timeout throws <see cref="VisaTimeoutException"/>.
    /// </summary>
    public static async ValueTask<TlsHislipChannel> AuthenticateAsClientAsync(Socket socket, SslClientAuthenticationOptions options,
        Deadline deadline, bool async)
    {
        // Record whether certificate validation rejected the server, to report it as such (exception texts vary by OS and language).
        var validate = options.RemoteCertificateValidationCallback;
        bool rejected = false;
        options.RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
        {
            bool accepted = validate is null ? errors == SslPolicyErrors.None : validate(sender, certificate, chain, errors);
            if (!accepted) rejected = true;
            return accepted;
        };
        try
        {
            return await AuthenticateAsync(socket, (ssl, token) => ssl.AuthenticateAsClientAsync(options, token), deadline, async,
                () => rejected).ConfigureAwait(false);
        }
        finally
        {
            options.RemoteCertificateValidationCallback = validate;
        }
    }

    /// <summary>Performs the server handshake (used by the simulator).</summary>
    public static ValueTask<TlsHislipChannel> AuthenticateAsServerAsync(Socket socket, SslServerAuthenticationOptions options, Deadline deadline,
        bool async) =>
        AuthenticateAsync(socket, (ssl, token) => ssl.AuthenticateAsServerAsync(options, token), deadline, async, static () => false);

    private static async ValueTask<TlsHislipChannel> AuthenticateAsync(Socket socket, Func<SslStream, CancellationToken, Task> handshake,
        Deadline deadline, bool async, Func<bool> serverCertificateRejected)
    {
        socket.Blocking = true;
        var records = new TlsRecordBoundaryStream(new NetworkStream(socket, ownsSocket: false));
        var ssl = new SslStream(records, leaveInnerStreamOpen: true);
        using var cts = deadline.CreateTokenSource() ?? new CancellationTokenSource();
        Task? task = null;
        try
        {
            task = handshake(ssl, cts.Token);
            await NetUtil.AwaitBoundedAsync(task, deadline, cts, async).ConfigureAwait(false);
            return new TlsHislipChannel(socket, ssl, records);
        }
        catch (Exception ex) when (ex is not VisaException)
        {
            // A synchronous caller may have stopped waiting while the handshake still runs: let it see the cancellation
            // before the stream goes away.
            if (task is { IsCompleted: false })
            {
                cts.Cancel();
                try { task.Wait(1000); } catch (AggregateException) { /* reported below */ }
            }
            ssl.Dispose();
            if (ex is OperationCanceledException)
                throw new VisaTimeoutException("The TLS handshake timed out.", ex);
            if (serverCertificateRejected())
                throw new VisaSecurityException(SecurityFailure.ServerCertificate, $"The server certificate was not trusted: {ex.Message}", ex);
            throw new VisaSecurityException(SecurityFailure.TlsNegotiation, $"TLS handshake failed: {ex.Message}", ex);
        }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int n = await _ssl.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                Task? space = null;
                lock (_sync)
                {
                    if (n == 0)
                    {
                        _eof = true;
                        _changed.Pulse();
                        return;
                    }
                    _chunks.Enqueue(buffer.AsSpan(0, n).ToArray());
                    _buffered += n;
                    _changed.Pulse();
                    if (_buffered >= MaxBuffered) space = _changed.Next;
                }
                // Back pressure: wait until readers have consumed some of the buffered data.
                if (space is not null) _records.ReleaseReader();
                while (space is not null)
                {
                    await space.WaitAsync(_stop.Token).ConfigureAwait(false);
                    lock (_sync) space = _buffered >= MaxBuffered && !_disposed ? _changed.Next : null;
                }
            }
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                // After we have sent close_notify, Windows SChannel reports the peer's close_notify as a decryption
                // failure ("context expired") instead of end of stream. The record has been consumed either way.
                if (_shutdownSent || _stop.IsCancellationRequested) _eof = true;
                else _fault ??= ex;
                _changed.Pulse();
            }
        }
        finally
        {
            _records.ReleaseReader();
        }
    }

    public override async ValueTask<int> ReceiveSomeAsync(Memory<byte> buffer, Deadline deadline, bool async)
    {
        if (buffer.IsEmpty) return 0;
        while (true)
        {
            Task next;
            lock (_sync)
            {
                if (_current is null && _chunks.Count > 0)
                {
                    _current = _chunks.Dequeue();
                    _currentOffset = 0;
                }
                if (_current is not null)
                {
                    int n = Math.Min(buffer.Length, _current.Length - _currentOffset);
                    _current.AsSpan(_currentOffset, n).CopyTo(buffer.Span);
                    _currentOffset += n;
                    _buffered -= n;
                    if (_currentOffset >= _current.Length) _current = null;
                    _changed.Pulse();
                    return n;
                }
                if (_disposed) throw new VisaException(VisaStatus.ErrorConnectionLost, "The TLS connection was closed.");
                if (_fault is not null)
                    throw new VisaException(VisaStatus.ErrorConnectionLost, $"The TLS connection failed: {_fault.Message}", _fault);
                if (_eof) throw new VisaException(VisaStatus.ErrorConnectionLost, "The TLS session was closed by the peer.");
                next = _changed.Next;
            }
            if (!await ChangeSignal.WaitAsync(next, deadline, async).ConfigureAwait(false))
                throw new VisaTimeoutException();
        }
    }

    public override async ValueTask SendAsync(ReadOnlyMemory<byte> data, Deadline deadline, bool async)
    {
        await _writeGate.EnterOrThrowAsync(deadline, async, "the TLS write in progress").ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_fault is not null)
                    throw new VisaException(VisaStatus.ErrorConnectionLost, $"The TLS connection failed: {_fault.Message}", _fault);
            }
            if (deadline.HasExpired) throw new VisaTimeoutException("TLS write timed out.");
            using var cts = deadline.CreateTokenSource();
            Task? write = null;
            try
            {
                write = _ssl.WriteAsync(data, cts?.Token ?? default).AsTask();
                await NetUtil.AwaitBoundedAsync(write, deadline, cts, async).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not VisaException)
            {
                // The caller may reuse the buffer once this returns: let a cancelled write finish first.
                if (write is { IsCompleted: false })
                {
                    try { write.Wait(1000); } catch (AggregateException) { /* reported below */ }
                }
                // A write cut off in the middle of a record leaves the TLS session unusable.
                lock (_sync)
                {
                    _fault ??= ex;
                    _changed.Pulse();
                }
                if (ex is OperationCanceledException)
                    throw new VisaTimeoutException("TLS write timed out; the connection can no longer be used.", ex);
                throw new VisaException(VisaStatus.ErrorConnectionLost, $"TLS write failed: {ex.Message}", ex);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Ends TLS on this connection (close_notify in both directions) and returns the socket as a plain channel. This
    /// channel must not be used afterwards. Fails with VI_ERROR_IO if decrypted data was still unread.
    /// </summary>
    public async ValueTask<PlainHislipChannel> EndTlsAsync(Deadline deadline, bool async)
    {
        await _writeGate.EnterOrThrowAsync(deadline, async, "the TLS write in progress").ConfigureAwait(false);
        try
        {
            // The peer's close_notify may be arriving right now: keep the reader from decrypting it during the shutdown.
            if (!await _records.EnterSessionAsync(deadline, async).ConfigureAwait(false))
                throw new VisaTimeoutException("Sending TLS close_notify timed out.");
            bool done;
            try
            {
                lock (_sync) _shutdownSent = true;
                var shutdown = _ssl.ShutdownAsync();
                if (async)
                {
                    using var cts = deadline.CreateTokenSource();
                    try
                    {
                        await shutdown.WaitAsync(cts?.Token ?? default).ConfigureAwait(false);
                        done = true;
                    }
                    catch (OperationCanceledException)
                    {
                        done = false;
                    }
                }
                else
                {
                    done = shutdown.Wait(deadline.RemainingMilliseconds);
                }
            }
            catch (Exception ex) when (ex is not VisaException)
            {
                var inner = (ex as AggregateException)?.InnerException ?? ex;
                throw new VisaException(VisaStatus.ErrorConnectionLost, $"Ending TLS failed: {inner.Message}", inner);
            }
            finally
            {
                _records.ExitSession();
            }
            if (!done) throw new VisaTimeoutException("Sending TLS close_notify timed out.");
        }
        finally
        {
            _writeGate.Release();
        }

        while (true)
        {
            Task next;
            lock (_sync)
            {
                if (_eof)
                {
                    if (_buffered > 0) throw new VisaException(VisaStatus.ErrorIO, "Data was pending when TLS ended.");
                    break;
                }
                if (_fault is not null)
                    throw new VisaException(VisaStatus.ErrorConnectionLost, $"The TLS connection failed while ending encryption: {_fault.Message}", _fault);
                next = _changed.Next;
            }
            if (!await ChangeSignal.WaitAsync(next, deadline, async).ConfigureAwait(false))
                throw new VisaTimeoutException("The peer did not answer close_notify.");
        }

        if (async) await _reader.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        else _reader.Wait(1000);
        lock (_sync)
        {
            _disposed = true;
            _handedOver = true;
        }
        _ssl.Dispose(); // leaves the socket open
        return new PlainHislipChannel(Socket);
    }

    /// <summary>
    /// The tls-server-end-point channel binding (RFC 5929) of this client connection, with the IVI-6.5
    /// "tls-server-end-point:" prefix. Null if the server sent no certificate.
    /// </summary>
    public byte[]? ServerEndPointChannelBinding() => RemoteCertificate is { } certificate ? ComputeServerEndPointBinding(certificate) : null;

    /// <summary>
    /// The tls-server-end-point binding for a server certificate: its hash, using SHA-384/512 when the certificate is
    /// signed with those, else SHA-256.
    /// </summary>
    internal static byte[] ComputeServerEndPointBinding(X509Certificate2 certificate)
    {
        string oid = certificate.SignatureAlgorithm.Value ?? "";
        byte[] hash = oid switch
        {
            "1.2.840.113549.1.1.13" or "1.2.840.10045.4.3.4" => SHA512.HashData(certificate.RawData), // sha512RSA, sha512ECDSA
            "1.2.840.113549.1.1.12" or "1.2.840.10045.4.3.3" => SHA384.HashData(certificate.RawData), // sha384RSA, sha384ECDSA
            _ => SHA256.HashData(certificate.RawData),
        };
        return [.. "tls-server-end-point:"u8, .. hash];
    }

    public override void Dispose()
    {
        bool first;
        lock (_sync)
        {
            if (_handedOver) return; // the socket belongs to the plain channel EndTlsAsync returned
            first = !_disposed;
            _disposed = true;
            _changed.Pulse();
        }
        NetUtil.SafeClose(Socket);
        if (!first) return;
        _stop.Cancel();
        // Let the reader leave SslStream before the TLS session is released (it ends promptly once the socket is closed).
        try { _reader.Wait(1000); } catch (AggregateException) { /* the reader reports through _fault */ }
        try { _ssl.Dispose(); } catch { /* ignore */ }
    }
}

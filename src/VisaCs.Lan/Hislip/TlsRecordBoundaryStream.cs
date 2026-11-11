using System.Net.Security;
using VisaCs.Internal;

namespace VisaCs.Lan.Protocols;

/// <summary>
/// Hands TLS records to <see cref="SslStream"/> without ever reading past the end of the current record.
/// HiSLIP's EndTLS continues in plain text on the same socket right after the close_notify alerts; SslStream reads
/// ahead in large chunks, and plain-text bytes it took in with the alert would be lost when it is disposed.
/// </summary>
/// <remarks>
/// It also keeps the reading side's use of the TLS session apart from <see cref="SslStream.ShutdownAsync"/>: the
/// reading side holds a lock from the moment bytes arrive until it asks for more, which covers SslStream decrypting
/// them. SslStream does not serialize a shutdown with a read in progress, and OpenSSL fails one (or worse) when the
/// two run at once, which HiSLIP's EndTLS invites: both sides send close_notify at about the same time.
/// </remarks>
internal sealed class TlsRecordBoundaryStream(Stream inner) : Stream
{
    private const int HeaderSize = 5; // content type, version (2), length (2)
    private readonly byte[] _header = new byte[HeaderSize];
    private int _headerFilled;
    private int _headerDelivered;
    private int _bodyRemaining;
    private readonly SemaphoreSlim _session = new(1, 1);
    private bool _readerHolds; // touched only by the (single) reading side

    /// <summary>Waits until no read is using the TLS session and keeps reads out until <see cref="ExitSession"/>.</summary>
    public async ValueTask<bool> EnterSessionAsync(Deadline deadline, bool async)
    {
        if (!async) return _session.Wait(deadline.RemainingMilliseconds);
        using var cts = deadline.CreateTokenSource();
        try
        {
            await _session.WaitAsync(cts?.Token ?? default).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void ExitSession() => _session.Release();

    /// <summary>Called by the reading side when it stops using the TLS session (between reads, or for good).</summary>
    public void ReleaseReader()
    {
        if (!_readerHolds) return;
        _readerHolds = false;
        _session.Release();
    }

    private int InnerRead(Span<byte> buffer)
    {
        ReleaseReader();
        int n = inner.Read(buffer);
        _session.Wait();
        _readerHolds = true;
        return n;
    }

    private async ValueTask<int> InnerReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ReleaseReader();
        int n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        // No cancellation here: the bytes have been taken off the socket and must reach SslStream.
        await _session.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _readerHolds = true;
        return n;
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        if (_headerDelivered == HeaderSize && _bodyRemaining > 0)
            return Consumed(InnerRead(buffer[..Math.Min(buffer.Length, _bodyRemaining)]));
        while (_headerFilled < HeaderSize)
        {
            int n = InnerRead(_header.AsSpan(_headerFilled));
            if (n == 0) return 0;
            _headerFilled += n;
        }
        return DeliverHeader(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return 0;
        if (_headerDelivered == HeaderSize && _bodyRemaining > 0)
            return Consumed(await InnerReadAsync(buffer[..Math.Min(buffer.Length, _bodyRemaining)], cancellationToken).ConfigureAwait(false));
        while (_headerFilled < HeaderSize)
        {
            int n = await InnerReadAsync(_header.AsMemory(_headerFilled), cancellationToken).ConfigureAwait(false);
            if (n == 0) return 0;
            _headerFilled += n;
        }
        return DeliverHeader(buffer.Span);
    }

    /// <summary>Passes on the (complete) record header; the record's body follows in later reads.</summary>
    private int DeliverHeader(Span<byte> buffer)
    {
        if (_headerDelivered == HeaderSize)
        {
            // A new record: the previous one (header and body) has been passed on completely.
            _headerDelivered = 0;
        }
        int n = Math.Min(buffer.Length, HeaderSize - _headerDelivered);
        _header.AsSpan(_headerDelivered, n).CopyTo(buffer);
        _headerDelivered += n;
        if (_headerDelivered == HeaderSize)
        {
            _bodyRemaining = (_header[3] << 8) | _header[4];
            _headerFilled = 0;
            if (_bodyRemaining == 0) _headerDelivered = HeaderSize;
        }
        return n;
    }

    private int Consumed(int n)
    {
        _bodyRemaining -= n;
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.WriteAsync(buffer, offset, count, cancellationToken);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.WriteAsync(buffer, cancellationToken);
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

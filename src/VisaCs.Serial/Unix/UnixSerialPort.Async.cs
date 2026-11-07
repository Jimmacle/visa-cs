using VisaCs.Internal;

namespace VisaCs.Serial;

// The asynchronous transfers live in a part of the class that is not an unsafe context, where await is allowed.
internal sealed partial class UnixSerialPort
{
    private protected override async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, Deadline deadline)
    {
        int fd = Acquire();
        CancellationToken abort = AbortToken;
        CancellationTokenSource? wait = null;
        try
        {
            short revents = 0;
            while (true)
            {
                // Only read() consumes data, and a cancelled wait does not read: nothing received is lost.
                int n = TryRead(fd, buffer.Span);
                if (n > 0) return n;
                ThrowIfHungUp(revents);
                wait ??= deadline.CreateTokenSource(abort);
                revents = await WaitAsync(fd, Libc.POLLIN, deadline, abort, wait.Token, reading: true, 0).ConfigureAwait(false);
            }
        }
        finally
        {
            wait?.Dispose();
            Release();
        }
    }

    private protected override async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> data, Deadline deadline)
    {
        int fd = Acquire();
        CancellationToken abort = AbortToken;
        CancellationTokenSource? wait = null;
        try
        {
            short revents = 0;
            int offset = 0;
            while (offset < data.Length)
            {
                int n = TryWrite(fd, data.Span[offset..]);
                offset += n;
                if (n > 0) continue;
                ThrowIfHungUp(revents);
                wait ??= deadline.CreateTokenSource(abort);
                revents = await WaitAsync(fd, Libc.POLLOUT, deadline, abort, wait.Token, reading: false, offset).ConfigureAwait(false);
            }
        }
        finally
        {
            wait?.Dispose();
            Release();
        }
    }

    /// <summary>
    /// Waits for <paramref name="events"/> on the shared poll thread. <paramref name="wait"/> fires at the deadline,
    /// on the caller's cancellation and on Abort or Dispose (<paramref name="abort"/>); the exception tells which.
    /// </summary>
    private async ValueTask<short> WaitAsync(int fd, short events, Deadline deadline, CancellationToken abort, CancellationToken wait,
        bool reading, int bytesTransferred)
    {
        if (!deadline.HasExpired)
        {
            try
            {
                return await UnixPoller.Shared.WaitAsync(fd, events, wait).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        throw IsDisposed ? Closed() : abort.IsCancellationRequested ? Aborted() : TimedOut(deadline, reading, bytesTransferred);
    }
}

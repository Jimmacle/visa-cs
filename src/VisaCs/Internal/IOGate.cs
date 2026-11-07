namespace VisaCs.Internal;

/// <summary>
/// Mutual exclusion for a session's I/O that synchronous and asynchronous callers share. Not reentrant: public
/// operations enter once and then call unguarded core methods. Waiters are not served in a guaranteed order.
/// </summary>
internal sealed class IOGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>Waits for the gate; dispose the result to leave. With <paramref name="async"/> false this blocks.</summary>
    public async ValueTask<Releaser> EnterAsync(bool async, CancellationToken cancellationToken)
    {
        if (async) await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        else _semaphore.Wait(cancellationToken);
        return new Releaser(_semaphore);
    }

    /// <summary>Waits for the gate, blocking the calling thread.</summary>
    public Releaser Enter() => AsyncIO.Complete(EnterAsync(async: false, CancellationToken.None));

    public readonly struct Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}

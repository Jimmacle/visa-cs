namespace VisaCs.Internal;

/// <summary>
/// "State changed" notification for code that keeps its state under a lock. A waiter checks the state under the
/// lock; if it must wait, it takes <see cref="Next"/> while still holding the lock, releases the lock and waits on
/// it with <see cref="WaitAsync"/>, then checks again. Whoever changes the state calls <see cref="Pulse"/> while
/// holding the lock. Works for synchronous waiters (which block) and asynchronous ones (which hold no thread).
/// </summary>
internal sealed class ChangeSignal
{
    private TaskCompletionSource _current = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes at the next <see cref="Pulse"/>. Read it under the lock that protects the state.</summary>
    public Task Next => _current.Task;

    /// <summary>Wakes all waiters. Call under the lock that protects the state, after changing it.</summary>
    public void Pulse()
    {
        var previous = _current;
        _current = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    /// <summary>
    /// Waits for <paramref name="next"/> (from <see cref="Next"/>) until the deadline passes or its token is
    /// cancelled. Returns false if that happened first.
    /// </summary>
    public static async ValueTask<bool> WaitAsync(Task next, Deadline deadline, bool async)
    {
        if (next.IsCompleted) return true;
        if (deadline.HasExpired) return false;
        if (!async)
        {
            // Synchronous waiters may still carry a token (an async caller further up); slice the wait so it is seen.
            while (true)
            {
                int wait = deadline.NextWaitMilliseconds;
                if (wait == 0) return false;
                if (next.Wait(wait)) return true;
                if (deadline.HasExpired) return false;
            }
        }
        using var cts = deadline.CreateTokenSource();
        try
        {
            await next.WaitAsync(cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

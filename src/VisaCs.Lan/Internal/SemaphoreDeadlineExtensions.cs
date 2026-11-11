
namespace VisaCs.Internal;

/// <summary>Waiting for a <see cref="SemaphoreSlim"/> used as a request gate, bounded by the caller's deadline.</summary>
internal static class SemaphoreDeadlineExtensions
{
    /// <summary>
    /// Enters the gate. Returns false when the deadline passes (or its token is cancelled) first, so a request queued
    /// behind a slow one ends at its own timeout. With <paramref name="async"/> false this blocks.
    /// </summary>
    public static async ValueTask<bool> EnterAsync(this SemaphoreSlim gate, Deadline deadline, bool async)
    {
        if (gate.Wait(0)) return true;
        if (!async)
        {
            while (true)
            {
                int wait = deadline.NextWaitMilliseconds;
                if (wait == 0) return false;
                if (gate.Wait(wait)) return true;
                if (deadline.HasExpired) return false;
            }
        }
        using var cts = deadline.CreateTokenSource();
        try
        {
            await gate.WaitAsync(Timeout.Infinite, cts?.Token ?? default).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Enters the gate or throws <see cref="VisaTimeoutException"/> with <paramref name="what"/> in the message.</summary>
    public static async ValueTask EnterOrThrowAsync(this SemaphoreSlim gate, Deadline deadline, bool async, string what)
    {
        if (!await gate.EnterAsync(deadline, async).ConfigureAwait(false))
            throw LanErrors.Timeout($"Timed out waiting for {what} to finish.");
    }
}

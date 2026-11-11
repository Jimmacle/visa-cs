namespace VisaCs.Internal;

/// <summary>Turns the library's internal <see cref="Deadline"/>-based calls into the public timeout and token forms.</summary>
internal static class LanCall
{
    /// <summary>The deadline of a public call: <paramref name="timeoutMilliseconds"/> (negative: no limit), ended early by the token.</summary>
    public static Deadline DeadlineFor(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        Deadline.FromMilliseconds(timeoutMilliseconds).WithCancellation(cancellationToken);

    /// <summary>
    /// Awaits an asynchronous call. Inside the library cancellation ends an operation like a timeout; once the caller's
    /// token is cancelled, the error becomes <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async ValueTask RunAsync(ValueTask task, CancellationToken cancellationToken)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && (LanErrors.IsLanError(ex) || ex is OperationCanceledException))
        {
            throw AsyncIO.Canceled(ex, cancellationToken);
        }
    }

    /// <inheritdoc cref="RunAsync(ValueTask, CancellationToken)"/>
    public static async ValueTask<T> RunAsync<T>(ValueTask<T> task, CancellationToken cancellationToken)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && (LanErrors.IsLanError(ex) || ex is OperationCanceledException))
        {
            throw AsyncIO.Canceled(ex, cancellationToken);
        }
    }
}

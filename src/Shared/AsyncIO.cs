using System.Diagnostics;

namespace VisaCs.Internal;

/// <summary>
/// Helpers for I/O code written once for synchronous and asynchronous callers. Such code takes a
/// <c>bool async</c> parameter. When it is false, every OS call blocks and every await completes synchronously,
/// so <see cref="Complete{T}(ValueTask{T})"/> unwraps the result. A few synchronous paths (HiSLIP's asynchronous-channel
/// reader, TLS I/O, connects and handshakes) block inside the operation on work that completes on the thread pool;
/// the ValueTask is still complete when Complete sees it.
/// </summary>
/// <remarks>
/// Inside the library an operation cancelled by the caller ends exactly like one that timed out (a
/// <see cref="VisaTimeoutException"/>), so every protocol's timeout recovery also runs after cancellation. Public
/// async methods turn that into <see cref="OperationCanceledException"/> with <see cref="Canceled"/>.
/// </remarks>
internal static class AsyncIO
{
    /// <summary>Returns the result of a call made with <c>async: false</c>.</summary>
    public static T Complete<T>(ValueTask<T> task)
    {
        CheckCompleted(task.IsCompleted);
        return task.GetAwaiter().GetResult();
    }

    /// <summary>Completes a call made with <c>async: false</c>.</summary>
    public static void Complete(ValueTask task)
    {
        CheckCompleted(task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    [Conditional("DEBUG")]
    private static void CheckCompleted(bool completed)
    {
        if (!completed)
            throw new InvalidOperationException("A synchronous call went asynchronous: some code path ignores the async flag.");
    }

    /// <summary>The exception a public async method throws when the caller's token ended the operation.</summary>
    public static OperationCanceledException Canceled(Exception error, CancellationToken cancellationToken) =>
        error as OperationCanceledException ?? new OperationCanceledException("The operation was canceled.", error, cancellationToken);
}

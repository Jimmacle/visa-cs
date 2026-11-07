using System.Diagnostics;

namespace VisaCs.Internal;

/// <summary>
/// An absolute point in time used to bound a whole operation, including retries and loops. It can also carry a
/// cancellation token: once cancellation is requested the deadline behaves as expired, so cancelling an
/// operation follows the same recovery path as a timeout.
/// </summary>
/// <remarks><c>default(Deadline)</c> has already expired; use <see cref="Infinite"/> for "no limit".</remarks>
internal readonly struct Deadline
{
    /// <summary>Longest single wait while cancellation is possible, so that blocked waits notice it promptly.</summary>
    public const int CancellationPollMs = 50;

    private readonly long _expires; // Stopwatch timestamp; long.MaxValue means "never"
    private readonly CancellationToken _cancellation;

    private Deadline(long expires, CancellationToken cancellation = default)
    {
        _expires = expires;
        _cancellation = cancellation;
    }

    /// <summary>A deadline that never passes (and cannot be cancelled until <see cref="WithCancellation"/>).</summary>
    public static Deadline Infinite => new(long.MaxValue);

    /// <summary>Creates a deadline <paramref name="milliseconds"/> from now. Negative means infinite.</summary>
    public static Deadline FromMilliseconds(int milliseconds) =>
        milliseconds < 0
            ? Infinite
            : new Deadline(Stopwatch.GetTimestamp() + (long)milliseconds * Stopwatch.Frequency / 1000);

    /// <summary>Returns this deadline, also expiring when <paramref name="cancellation"/> is cancelled.</summary>
    public Deadline WithCancellation(CancellationToken cancellation) => new(_expires, cancellation);

    public bool IsInfinite => _expires == long.MaxValue;

    public bool CanBeCanceled => _cancellation.CanBeCanceled;

    public bool IsCanceled => _cancellation.IsCancellationRequested;

    public bool HasExpired => IsCanceled || (!IsInfinite && Stopwatch.GetTimestamp() >= _expires);

    /// <summary>The caller's cancellation token carried by this deadline.</summary>
    public CancellationToken CancellationToken => _cancellation;

    /// <summary>
    /// For an asynchronous OS call: a source whose token is cancelled when this deadline passes or the caller's
    /// token is cancelled. Null when neither can happen. Dispose it when the call has finished.
    /// </summary>
    public CancellationTokenSource? CreateTokenSource()
    {
        if (IsInfinite && !CanBeCanceled) return null;
        var source = CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(_cancellation) : new CancellationTokenSource();
        if (!IsInfinite) source.CancelAfter(RemainingMilliseconds);
        return source;
    }

    /// <summary>
    /// Like <see cref="CreateTokenSource()"/>, and also cancelled by <paramref name="other"/> (for example the
    /// object's own abort token). Never null. Dispose it when the call has finished.
    /// </summary>
    public CancellationTokenSource CreateTokenSource(CancellationToken other)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(other, _cancellation);
        if (!IsInfinite) source.CancelAfter(RemainingMilliseconds);
        return source;
    }

    /// <summary>Remaining time in milliseconds: -1 when infinite, 0 when expired.</summary>
    public int RemainingMilliseconds
    {
        get
        {
            if (IsCanceled) return 0;
            if (IsInfinite) return -1;
            long remaining = _expires - Stopwatch.GetTimestamp();
            if (remaining <= 0) return 0;
            long ms = (remaining * 1000 + Stopwatch.Frequency - 1) / Stopwatch.Frequency;
            return ms > int.MaxValue ? int.MaxValue : (int)ms;
        }
    }

    /// <summary>Remaining milliseconds, with infinite mapped to <paramref name="infiniteValue"/>.</summary>
    public int RemainingOr(int infiniteValue) => IsInfinite ? infiniteValue : RemainingMilliseconds;

    /// <summary>Remaining milliseconds clamped to a slice length (0 when expired), for polling loops.</summary>
    public int Slice(int maxSliceMs)
    {
        int r = RemainingMilliseconds;
        return r < 0 || r > maxSliceMs ? maxSliceMs : r;
    }

    /// <summary>
    /// How long one blocking wait may last: the remaining time (-1 when infinite, 0 when expired), limited to
    /// <see cref="CancellationPollMs"/> while cancellation is possible.
    /// </summary>
    public int NextWaitMilliseconds
    {
        get
        {
            int remaining = RemainingMilliseconds;
            if (!CanBeCanceled || remaining == 0) return remaining;
            return remaining < 0 || remaining > CancellationPollMs ? CancellationPollMs : remaining;
        }
    }

    /// <summary>Returns a deadline later than this one by <paramref name="milliseconds"/> (no-op when infinite).</summary>
    public Deadline Extend(int milliseconds) =>
        IsInfinite ? this : new Deadline(_expires + (long)milliseconds * Stopwatch.Frequency / 1000, _cancellation);

    /// <summary>Throws <see cref="VisaTimeoutException"/> ("<paramref name="operation"/> timed out.") once expired or cancelled.</summary>
    public void ThrowIfExpired(string? operation = null)
    {
        if (HasExpired)
            throw new VisaTimeoutException(operation is null ? null : $"{operation} timed out.");
    }
}

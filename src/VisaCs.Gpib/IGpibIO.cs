namespace VisaCs.Gpib;

/// <summary>Result of a GPIB read.</summary>
/// <param name="Count">Number of bytes read.</param>
/// <param name="End">
/// True if the last byte carried EOI or matched the termination character. False with a full buffer means more data
/// may follow; false with a partly filled buffer means the timeout passed after some bytes had arrived.
/// </param>
public readonly record struct GpibReadResult(int Count, bool End);

/// <summary>Byte transfers on a GPIB device or board descriptor.</summary>
/// <remarks>
/// <para>
/// Timeouts are per call. <see cref="Timeout.Infinite"/> (or any negative value) waits without limit; 0 does not
/// wait: the call times out unless it can complete at once. <see cref="Ni488Driver"/> sets the NI-488.2 timeout
/// (ibtmo) for each call: TNONE for a negative value, T10us (the shortest, NI-488.2 has no zero timeout) for 0, and
/// otherwise the next step up (10 ms, 30 ms, 100 ms, 300 ms, 1 s, 3 s, 10 s, 30 s, 100 s, 300 s, 1000 s), capping
/// longer timeouts at 1000 s.
/// </para>
/// <para>
/// Driver and bus errors throw <see cref="GpibException"/>, whose <see cref="GpibException.Error"/> names the
/// failure (for example <see cref="GpibError.NoListeners"/>, NI-488.2 ENOL, when no device listens) and whose
/// <see cref="GpibException.NativeError"/> holds the driver's <c>iberr</c>. A timeout throws
/// <see cref="GpibTimeoutException"/> with the number of bytes already sent, except that reads return the bytes that
/// arrived before the timeout (with <see cref="GpibReadResult.End"/> false) and throw only when nothing arrived.
/// </para>
/// <para>
/// Cancelling the token of an asynchronous method throws <see cref="OperationCanceledException"/>.
/// <see cref="Ni488Driver"/> stops a transfer in progress; a call it runs on its board worker thread (see its remarks)
/// is removed while queued and otherwise runs until the driver returns.
/// </para>
/// <para>
/// An object is not safe for concurrent calls from several threads: run one operation at a time on it. Different
/// devices may be used from different threads. <see cref="Ni488Driver"/> rejects a second asynchronous transfer on the
/// same descriptor with <see cref="GpibError.AsyncInProgress"/>; all <see cref="IGpibBoard"/> objects of one board
/// share that board's descriptor.
/// </para>
/// <para>
/// <see cref="IDisposable.Dispose"/> ends queued and in-progress asynchronous operations with
/// <see cref="ObjectDisposedException"/> (a transfer in progress is stopped first); later calls throw
/// <see cref="ObjectDisposedException"/>. Do not dispose while a synchronous call on the same object runs on another
/// thread.
/// </para>
/// </remarks>
public interface IGpibIO : IDisposable
{
    /// <summary>Writes <paramref name="data"/>; asserts EOI with the last byte when <paramref name="end"/> is true.</summary>
    void Write(ReadOnlySpan<byte> data, bool end, int timeoutMilliseconds);

    /// <summary>Writes <paramref name="data"/>; asserts EOI with the last byte when <paramref name="end"/> is true.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, bool end, int timeoutMilliseconds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads up to <c>buffer.Length</c> bytes. The read ends with a byte that carries EOI, with the termination
    /// character <paramref name="termChar"/> (EOS, compared in all 8 bits) unless it is null, or when the buffer is full.
    /// </summary>
    GpibReadResult Read(Span<byte> buffer, byte? termChar, int timeoutMilliseconds);

    /// <summary>Reads up to <c>buffer.Length</c> bytes; see <see cref="Read"/>.</summary>
    ValueTask<GpibReadResult> ReadAsync(Memory<byte> buffer, byte? termChar, int timeoutMilliseconds,
        CancellationToken cancellationToken = default);
}

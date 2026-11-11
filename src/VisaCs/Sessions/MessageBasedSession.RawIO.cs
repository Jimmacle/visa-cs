using System.Buffers;
using System.Text;
using VisaCs.Formatting;
using VisaCs.Internal;

namespace VisaCs;

// Reading, writing, queries and device operations (viRead, viWrite, viReadSTB, viClear, viAssertTrigger, viFlush).
public abstract partial class MessageBasedSession
{
    // ------------------------------------------------------------------ reading

    /// <summary>
    /// Reads into <paramref name="buffer"/> (viRead). Completes on END, on the termination character when enabled,
    /// or when the buffer is full; the result tells how many bytes arrived and which of these ended the read.
    /// </summary>
    public ReadResult Read(Span<byte> buffer)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            var result = AsyncIO.Complete(ReadAsync(rented.AsMemory(0, buffer.Length), CancellationToken.None, async: false));
            rented.AsSpan(0, result.Count).CopyTo(buffer);
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Reads into <paramref name="buffer"/> (viRead) without holding a thread while it waits; see <see cref="Read"/>.</summary>
    public ValueTask<ReadResult> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ReadAsync(buffer, cancellationToken, async: true);

    private ValueTask<ReadResult> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken, bool async) =>
        ExecuteAsync(buffer, static (s, buffer, ct, async) => s.ReadCoreAsync(buffer, s.NewDeadline(ct), async), cancellationToken, async);

    /// <summary>Reads up to <paramref name="count"/> bytes, stopping early on END or the termination character.</summary>
    public byte[] ReadBytes(int count) => AsyncIO.Complete(ReadBytesAsync(count, CancellationToken.None, async: false));

    /// <summary>Reads up to <paramref name="count"/> bytes without holding a thread; see <see cref="ReadBytes"/>.</summary>
    public Task<byte[]> ReadBytesAsync(int count, CancellationToken cancellationToken = default) =>
        ReadBytesAsync(count, cancellationToken, async: true).AsTask();

    private ValueTask<byte[]> ReadBytesAsync(int count, CancellationToken cancellationToken, bool async)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return ExecuteAsync(count, static async (s, count, ct, async) =>
        {
            var buffer = new byte[count];
            var result = await s.ReadCoreAsync(buffer, s.NewDeadline(ct), async).ConfigureAwait(false);
            return result.Count == count ? buffer : buffer.AsSpan(0, result.Count).ToArray();
        }, cancellationToken, async);
    }

    /// <summary>
    /// Reads a complete response: repeats reads of <see cref="ReadChunkSize"/> bytes until END or the termination
    /// character. The timeout applies to each chunk. On a timeout the data read so far is lost; the exception's
    /// <see cref="VisaTimeoutException.BytesTransferred"/> tells how much there was.
    /// </summary>
    public byte[] ReadToEnd() => AsyncIO.Complete(ReadToEndAsync(CancellationToken.None, async: false));

    /// <summary>Reads a complete response without holding a thread; see <see cref="ReadToEnd"/>.</summary>
    public Task<byte[]> ReadToEndAsync(CancellationToken cancellationToken = default) => ReadToEndAsync(cancellationToken, async: true).AsTask();

    private ValueTask<byte[]> ReadToEndAsync(CancellationToken cancellationToken, bool async) =>
        ExecuteAsync(0, static (s, _, ct, async) => s.ReadToEndUnguardedAsync(ct, async), cancellationToken, async);

    private protected async ValueTask<byte[]> ReadToEndUnguardedAsync(CancellationToken cancellationToken, bool async)
    {
        var output = new ArrayBufferWriter<byte>(256);
        int chunk = ReadChunkSize;
        while (true)
        {
            var memory = output.GetMemory(chunk)[..chunk];
            ReadResult result;
            try
            {
                result = await ReadCoreAsync(memory, NewDeadline(cancellationToken), async).ConfigureAwait(false);
            }
            catch (VisaTimeoutException ex) when (output.WrittenCount > 0 || ex.BytesTransferred > 0)
            {
                throw new VisaTimeoutException(ex.Message, ex) { BytesTransferred = output.WrittenCount + ex.BytesTransferred };
            }
            output.Advance(result.Count);
            if (result.Status != ReadStatus.MaximumCountReached) break;
        }
        return output.WrittenSpan.ToArray();
    }

    /// <summary>Reads a complete response (see <see cref="ReadToEnd"/>) and decodes it with <see cref="Encoding"/>.</summary>
    public string ReadString() => Encoding.GetString(ReadToEnd());

    /// <summary>Reads a complete response and decodes it without holding a thread; see <see cref="ReadString"/>.</summary>
    public async Task<string> ReadStringAsync(CancellationToken cancellationToken = default) =>
        Encoding.GetString(await ReadToEndAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Reads a complete response (see <see cref="ReadString"/>) and removes trailing CR, LF and, when enabled, the
    /// termination character.
    /// </summary>
    public string ReadLine() => TrimTermination(ReadString());

    /// <summary>Reads a response line without holding a thread; see <see cref="ReadLine"/>.</summary>
    public async Task<string> ReadLineAsync(CancellationToken cancellationToken = default) =>
        TrimTermination(await ReadStringAsync(cancellationToken).ConfigureAwait(false));

    private async ValueTask<string> ReadLineUnguardedAsync(CancellationToken cancellationToken, bool async) =>
        TrimTermination(Encoding.GetString(await ReadToEndUnguardedAsync(cancellationToken, async).ConfigureAwait(false)));

    private string TrimTermination(string s)
    {
        int end = s.Length;
        while (end > 0)
        {
            char c = s[end - 1];
            if (c == '\n' || c == '\r' || (TerminationCharacterEnabled && c == (char)TerminationCharacter)) end--;
            else break;
        }
        return end == s.Length ? s : s[..end];
    }

    // ------------------------------------------------------------------ writing

    /// <summary>Writes bytes (viWrite). END is sent with the last byte if <see cref="SendEndEnabled"/>.</summary>
    public void Write(ReadOnlySpan<byte> data) => AsyncIO.Complete(WriteAsync(data.ToArray(), CancellationToken.None, async: false));

    /// <summary>Writes bytes (viWrite). END is sent with the last byte if <see cref="SendEndEnabled"/>.</summary>
    public void Write(byte[] data) => AsyncIO.Complete(WriteAsync(data, CancellationToken.None, async: false));

    /// <summary>Writes bytes (viWrite) without holding a thread. END is sent with the last byte if <see cref="SendEndEnabled"/>.</summary>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        WriteAsync(data, cancellationToken, async: true);

    private ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken, bool async) =>
        ExecuteAsync(data, static (s, data, ct, async) => s.WriteUnguardedAsync(data, ct, async), cancellationToken, async);

    private protected ValueTask WriteUnguardedAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken, bool async) =>
        WriteCoreAsync(data, SendEndEnabled, NewDeadline(cancellationToken), async);

    /// <summary>Writes a string encoded with <see cref="Encoding"/>, as is (no termination is appended).</summary>
    public void Write(string text) => Write(Encoding.GetBytes(text));

    /// <summary>Writes a string as is without holding a thread; see <see cref="Write(string)"/>.</summary>
    public Task WriteAsync(string text, CancellationToken cancellationToken = default) =>
        WriteAsync(Encoding.GetBytes(text), cancellationToken).AsTask();

    /// <summary>Writes a string followed by <see cref="WriteTermination"/>.</summary>
    public void WriteLine(string text) => Write(Encoding.GetBytes(text + WriteTermination));

    /// <summary>Writes a string followed by <see cref="WriteTermination"/> without holding a thread.</summary>
    public Task WriteLineAsync(string text, CancellationToken cancellationToken = default) =>
        WriteAsync(Encoding.GetBytes(text + WriteTermination), cancellationToken).AsTask();

    private ValueTask WriteLineUnguardedAsync(string text, CancellationToken cancellationToken, bool async) =>
        WriteUnguardedAsync(Encoding.GetBytes(text + WriteTermination), cancellationToken, async);

    // ------------------------------------------------------------------ queries

    /// <summary>
    /// Writes <paramref name="command"/> followed by <see cref="WriteTermination"/>, waits <see cref="QueryDelay"/>, then
    /// reads the response and removes trailing CR, LF and the termination character (like <see cref="ReadLine"/>).
    /// No other operation of the session runs in between.
    /// </summary>
    public string Query(string command) => AsyncIO.Complete(QueryAsync(command, CancellationToken.None, async: false));

    /// <summary>Writes a command and reads the response line without holding a thread; see <see cref="Query(string)"/>.</summary>
    public Task<string> QueryAsync(string command, CancellationToken cancellationToken = default) =>
        QueryAsync(command, cancellationToken, async: true).AsTask();

    private ValueTask<string> QueryAsync(string command, CancellationToken cancellationToken, bool async) =>
        ExecuteAsync(command, static async (s, command, ct, async) =>
        {
            await s.SendQueryUnguardedAsync(command, ct, async).ConfigureAwait(false);
            return await s.ReadLineUnguardedAsync(ct, async).ConfigureAwait(false);
        }, cancellationToken, async);

    /// <summary>
    /// Writes a command and parses the response as one <typeparamref name="T"/>, e.g. <c>Query&lt;double&gt;("MEAS:VOLT?")</c>.
    /// Numbers use the invariant culture; integers also accept NR2/NR3 forms such as <c>1.000E+00</c> (rounded),
    /// decimals accept exponents, and <see cref="bool"/> accepts <c>1</c>/<c>0</c> and <c>ON</c>/<c>OFF</c>.
    /// A response that does not parse throws VI_ERROR_INV_FMT.
    /// </summary>
    public T Query<T>(string command) where T : ISpanParsable<T> => Ieee4882.ParseValue<T>(Query(command));

    /// <summary>Writes a command and parses the response without holding a thread; see <see cref="Query{T}(string)"/>.</summary>
    public async Task<T> QueryAsync<T>(string command, CancellationToken cancellationToken = default) where T : ISpanParsable<T> =>
        Ieee4882.ParseValue<T>(await QueryAsync(command, cancellationToken).ConfigureAwait(false));

    /// <summary>Writes a query command with <see cref="WriteTermination"/> and waits <see cref="QueryDelay"/>.</summary>
    private async ValueTask SendQueryUnguardedAsync(string command, CancellationToken cancellationToken, bool async)
    {
        await WriteLineUnguardedAsync(command, cancellationToken, async).ConfigureAwait(false);
        await QueryDelayAsync(cancellationToken, async).ConfigureAwait(false);
    }

    private async ValueTask QueryDelayAsync(CancellationToken cancellationToken, bool async)
    {
        var delay = QueryDelay;
        if (delay <= TimeSpan.Zero) return;
        if (async) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        else Thread.Sleep(delay);
    }

    // ------------------------------------------------------------------ device operations

    /// <summary>Performs a device clear (viClear) and discards buffered input.</summary>
    public void Clear() => AsyncIO.Complete(ClearAsync(CancellationToken.None, async: false));

    /// <summary>Performs a device clear (viClear) without holding a thread; see <see cref="Clear"/>.</summary>
    public Task ClearAsync(CancellationToken cancellationToken = default) => ClearAsync(cancellationToken, async: true).AsTask();

    private ValueTask ClearAsync(CancellationToken cancellationToken, bool async) =>
        ExecuteAsync(0, static async (s, _, ct, async) =>
        {
            s._input.Clear();
            await s.ClearCoreAsync(s.NewDeadline(ct), async).ConfigureAwait(false);
            s._input.Clear();
        }, cancellationToken, async);

    /// <summary>
    /// Reads the IEEE 488.2 status byte (viReadSTB) with the protocol's own request (a serial poll on GPIB), or with
    /// <c>*STB?</c> on serial and socket sessions. Sessions without either throw VI_ERROR_NSUP_OPER.
    /// </summary>
    public byte ReadStatusByte() => AsyncIO.Complete(ReadStatusByteAsync(CancellationToken.None, async: false));

    /// <summary>Reads the status byte (viReadSTB) without holding a thread; see <see cref="ReadStatusByte"/>.</summary>
    public Task<byte> ReadStatusByteAsync(CancellationToken cancellationToken = default) =>
        ReadStatusByteAsync(cancellationToken, async: true).AsTask();

    private ValueTask<byte> ReadStatusByteAsync(CancellationToken cancellationToken, bool async) =>
        ExecuteAsync(0, static (s, _, ct, async) => s.ReadStatusByteCoreAsync(s.NewDeadline(ct), async), cancellationToken, async);

    /// <summary>Sends a software trigger (viAssertTrigger): GPIB GET, the protocol's trigger message, or <c>*TRG</c>.</summary>
    public void AssertTrigger() => AsyncIO.Complete(AssertTriggerAsync(CancellationToken.None, async: false));

    /// <summary>Sends a software trigger without holding a thread; see <see cref="AssertTrigger"/>.</summary>
    public Task AssertTriggerAsync(CancellationToken cancellationToken = default) => AssertTriggerAsync(cancellationToken, async: true).AsTask();

    private ValueTask AssertTriggerAsync(CancellationToken cancellationToken, bool async) =>
        ExecuteAsync(0, static (s, _, ct, async) => s.TriggerCoreAsync(s.NewDeadline(ct), async), cancellationToken, async);

    /// <summary>
    /// Controls the remote/local state of the device (viGpibControlREN) where the protocol supports it (GPIB, USB488,
    /// VXI-11, HiSLIP, VICP); other sessions throw VI_ERROR_NSUP_OPER.
    /// </summary>
    public void ControlRen(RemoteLocalMode mode) => AsyncIO.Complete(ControlRenAsync(mode, CancellationToken.None, async: false));

    /// <summary>Controls the remote/local state without holding a thread; see <see cref="ControlRen"/>.</summary>
    public Task ControlRenAsync(RemoteLocalMode mode, CancellationToken cancellationToken = default) =>
        ControlRenAsync(mode, cancellationToken, async: true).AsTask();

    private ValueTask ControlRenAsync(RemoteLocalMode mode, CancellationToken cancellationToken, bool async) =>
        ExecuteAsync(mode, static (s, mode, ct, async) => s.ControlRenCoreAsync(mode, s.NewDeadline(ct), async), cancellationToken, async);

    /// <summary>
    /// Flushes or discards buffers (viFlush). The read flags (<see cref="BufferTypes.ReadBuffer"/>,
    /// <see cref="BufferTypes.ReadBufferDiscard"/>, <see cref="BufferTypes.InBuffer"/>,
    /// <see cref="BufferTypes.InBufferDiscard"/>) discard input that has arrived but not been read.
    /// <see cref="BufferTypes.WriteBuffer"/> and <see cref="BufferTypes.OutBuffer"/> wait until written data has
    /// left the transport (serial ports). <see cref="BufferTypes.OutBufferDiscard"/> drops data still waiting to be
    /// sent where the transport buffers it (serial ports). <see cref="BufferTypes.WriteBufferDiscard"/> does nothing:
    /// writes are not buffered.
    /// </summary>
    public void Flush(BufferTypes mask) => AsyncIO.Complete(FlushAsync(mask, CancellationToken.None, async: false));

    /// <summary>Flushes or discards buffers (viFlush) without holding a thread; see <see cref="Flush"/>.</summary>
    public Task FlushAsync(BufferTypes mask, CancellationToken cancellationToken = default) =>
        FlushAsync(mask, cancellationToken, async: true).AsTask();

    private ValueTask FlushAsync(BufferTypes mask, CancellationToken cancellationToken, bool async) =>
        ExecuteAsync(mask, static async (s, mask, ct, async) =>
        {
            if ((mask & (BufferTypes.ReadBuffer | BufferTypes.ReadBufferDiscard | BufferTypes.InBuffer | BufferTypes.InBufferDiscard)) != 0)
            {
                s._input.Clear();
                s.FlushTransportInput();
            }
            if ((mask & BufferTypes.OutBufferDiscard) != 0)
                await s.DiscardTransportOutputAsync(async).ConfigureAwait(false);
            if ((mask & (BufferTypes.WriteBuffer | BufferTypes.OutBuffer)) != 0)
                await s.FlushTransportOutputAsync(s.NewDeadline(ct), async).ConfigureAwait(false);
        }, cancellationToken, async);

    /// <summary>Emulates viReadSTB with "*STB?" for protocols without a native status byte.</summary>
    private protected async ValueTask<byte> QueryStatusByteWithStringsAsync(Deadline deadline, bool async)
    {
        await WriteCoreAsync("*STB?\n"u8.ToArray(), true, deadline, async).ConfigureAwait(false);
        int saved = _termOverride;
        _termOverride = '\n';
        try
        {
            int total = 0;
            while (total < _scratch.Length)
            {
                var result = await ReadCoreAsync(_scratch.AsMemory(total), deadline, async).ConfigureAwait(false);
                total += result.Count;
                if (result.Status != ReadStatus.MaximumCountReached) break;
            }
            var text = Encoding.ASCII.GetString(_scratch, 0, total);
            if (!Ieee4882.TryParseValue<int>(text.Trim(), out int stb) || stb is < 0 or > 255)
                throw new VisaException(VisaStatus.ErrorIO, $"Invalid response to *STB?: '{text.Trim()}'.");
            return (byte)stb;
        }
        finally
        {
            _termOverride = saved;
        }
    }
}

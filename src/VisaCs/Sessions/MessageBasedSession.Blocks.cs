using VisaCs.Internal;

namespace VisaCs;

// IEEE 488.2 binary blocks and ASCII arrays.
public abstract partial class MessageBasedSession
{
    /// <summary>Largest buffer allocated up front for a block; larger blocks grow as their data arrives.</summary>
    private const int InitialBlockAllocation = 1 << 20;

    // ------------------------------------------------------------------ binary blocks

    /// <summary>
    /// Reads an IEEE 488.2 arbitrary block (<c>#&lt;n&gt;&lt;length&gt;&lt;data&gt;</c> or indefinite <c>#0&lt;data&gt;</c>)
    /// and returns its data. Any text before '#' is skipped, the termination character is ignored inside the data, and
    /// the rest of the message (usually a newline) is consumed. A malformed header throws VI_ERROR_INV_FMT; a message
    /// that ends before the announced length throws VI_ERROR_IO.
    /// </summary>
    public byte[] ReadBinaryBlock() => AsyncIO.Complete(ReadBinaryBlockAsync(CancellationToken.None, async: false));

    /// <summary>Reads an IEEE 488.2 arbitrary block without holding a thread; see <see cref="ReadBinaryBlock"/>.</summary>
    public Task<byte[]> ReadBinaryBlockAsync(CancellationToken cancellationToken = default) =>
        ReadBinaryBlockAsync(cancellationToken, async: true).AsTask();

    private ValueTask<byte[]> ReadBinaryBlockAsync(CancellationToken cancellationToken, bool async) =>
        ExecuteAsync(0, static (s, _, ct, async) => s.ReadBinaryBlockUnguardedAsync(ct, async), cancellationToken, async);

    private async ValueTask<byte[]> ReadBinaryBlockUnguardedAsync(CancellationToken cancellationToken, bool async)
    {
        bool saved = _suppressTermChar;
        _suppressTermChar = true;
        try
        {
            var one = _scratch.AsMemory(0, 1);
            long skipped = 0;
            while (true)
            {
                var result = await ReadCoreAsync(one, NewDeadline(cancellationToken), async).ConfigureAwait(false);
                if (result.Count == 1 && _scratch[0] == (byte)'#') break;
                if (result.Status == ReadStatus.EndReceived)
                    throw new VisaException(VisaStatus.ErrorInvalidFormat, "No IEEE 488.2 block header ('#') in the response.");
                if (++skipped > 1 << 20)
                    throw new VisaException(VisaStatus.ErrorInvalidFormat, "No IEEE 488.2 block header found.");
            }

            var digit = await ReadCoreAsync(one, NewDeadline(cancellationToken), async).ConfigureAwait(false);
            if (digit.Count != 1 || _scratch[0] < '0' || _scratch[0] > '9')
                throw new VisaException(VisaStatus.ErrorInvalidFormat, "Invalid IEEE 488.2 block header.");
            int digits = _scratch[0] - '0';

            if (digits == 0)
            {
                // Indefinite-length block: data runs until END (NL^END).
                _suppressTermChar = !HasEndIndicator ? saved : true;
                var rest = await ReadToEndUnguardedAsync(cancellationToken, async).ConfigureAwait(false);
                int len = rest.Length;
                if (len > 0 && rest[len - 1] == '\n') len--;
                return len == rest.Length ? rest : rest.AsSpan(0, len).ToArray();
            }

            var lengthText = new byte[digits];
            await ReadExactlyAsync(lengthText, null, cancellationToken, async).ConfigureAwait(false);
            if (!Ieee4882.TryParseBlockLength(lengthText, out int length))
                throw new VisaException(VisaStatus.ErrorInvalidFormat, "Invalid IEEE 488.2 block length.");

            var (block, endSeen) = await ReadBlockDataAsync(length, cancellationToken, async).ConfigureAwait(false);
            _suppressTermChar = saved;
            if (!endSeen && (HasEndIndicator || TermCharActive))
                await ReadToEndUnguardedAsync(cancellationToken, async).ConfigureAwait(false); // trailing terminator
            return block;
        }
        finally
        {
            _suppressTermChar = saved;
        }
    }

    /// <summary>
    /// Reads the <paramref name="length"/> data bytes of a definite-length block. The buffer grows as data arrives,
    /// so a corrupt header cannot make the session allocate the announced size up front.
    /// </summary>
    private async ValueTask<(byte[] Data, bool EndSeen)> ReadBlockDataAsync(int length, CancellationToken cancellationToken, bool async)
    {
        var data = new byte[Math.Min(length, InitialBlockAllocation)];
        int filled = 0;
        bool endSeen = false;
        while (filled < length)
        {
            if (filled == data.Length) Array.Resize(ref data, (int)Math.Min(length, (long)data.Length * 2));
            endSeen = await ReadExactlyAsync(data.AsMemory(filled), null, cancellationToken, async).ConfigureAwait(false);
            filled = data.Length;
            if (endSeen && filled < length)
                throw new VisaException(VisaStatus.ErrorIO, $"The message ended after {filled} bytes of a {length} byte block.");
        }
        return (data, endSeen);
    }

    /// <summary>
    /// Reads exactly buffer.Length bytes. Fails if END arrives early. Returns true if END came with the last byte.
    /// Each read gets its own timeout unless <paramref name="deadline"/> is given.
    /// </summary>
    private async ValueTask<bool> ReadExactlyAsync(Memory<byte> buffer, Deadline? deadline, CancellationToken cancellationToken, bool async)
    {
        int offset = 0;
        bool endSeen = false;
        while (offset < buffer.Length)
        {
            var result = await ReadCoreAsync(buffer[offset..], deadline ?? NewDeadline(cancellationToken), async).ConfigureAwait(false);
            offset += result.Count;
            endSeen = result.Status == ReadStatus.EndReceived;
            if (endSeen && offset < buffer.Length)
                throw new VisaException(VisaStatus.ErrorIO, $"The message ended after {offset} bytes of a {buffer.Length} byte block.");
        }
        return endSeen;
    }

    /// <summary>
    /// Reads a binary block (see <see cref="ReadBinaryBlock"/>) and converts it to an array of <typeparamref name="T"/>
    /// in <see cref="BinaryByteOrder"/>. Throws VI_ERROR_INV_FMT if the block length is not a multiple of the element size.
    /// </summary>
    public T[] ReadBinaryValues<T>() where T : unmanaged => Ieee4882.BytesToValues<T>(ReadBinaryBlock(), BinaryByteOrder);

    /// <summary>Reads a binary block as an array of <typeparamref name="T"/> without holding a thread; see <see cref="ReadBinaryValues{T}"/>.</summary>
    public async Task<T[]> ReadBinaryValuesAsync<T>(CancellationToken cancellationToken = default) where T : unmanaged =>
        Ieee4882.BytesToValues<T>(await ReadBinaryBlockAsync(cancellationToken).ConfigureAwait(false), BinaryByteOrder);

    /// <summary>
    /// Writes <paramref name="command"/> with <see cref="WriteTermination"/>, waits <see cref="QueryDelay"/> and reads a
    /// binary block response; see <see cref="ReadBinaryBlock"/>.
    /// </summary>
    public byte[] QueryBinaryBlock(string command) => AsyncIO.Complete(QueryBinaryBlockAsync(command, CancellationToken.None, async: false));

    /// <summary>Sends a command and reads a binary block response without holding a thread; see <see cref="QueryBinaryBlock"/>.</summary>
    public Task<byte[]> QueryBinaryBlockAsync(string command, CancellationToken cancellationToken = default) =>
        QueryBinaryBlockAsync(command, cancellationToken, async: true).AsTask();

    private ValueTask<byte[]> QueryBinaryBlockAsync(string command, CancellationToken cancellationToken, bool async) =>
        ExecuteAsync(command, static async (s, command, ct, async) =>
        {
            await s.SendQueryUnguardedAsync(command, ct, async).ConfigureAwait(false);
            return await s.ReadBinaryBlockUnguardedAsync(ct, async).ConfigureAwait(false);
        }, cancellationToken, async);

    /// <summary>
    /// Sends a command and reads a binary block response as an array of <typeparamref name="T"/> in
    /// <see cref="BinaryByteOrder"/>, e.g. <c>QueryBinaryValues&lt;float&gt;("CURV?")</c>.
    /// </summary>
    public T[] QueryBinaryValues<T>(string command) where T : unmanaged => Ieee4882.BytesToValues<T>(QueryBinaryBlock(command), BinaryByteOrder);

    /// <summary>Sends a command and reads binary values without holding a thread; see <see cref="QueryBinaryValues{T}"/>.</summary>
    public async Task<T[]> QueryBinaryValuesAsync<T>(string command, CancellationToken cancellationToken = default) where T : unmanaged =>
        Ieee4882.BytesToValues<T>(await QueryBinaryBlockAsync(command, cancellationToken).ConfigureAwait(false), BinaryByteOrder);

    /// <summary>
    /// Writes <paramref name="prefix"/>, <paramref name="data"/> as a definite-length block
    /// (<c>#&lt;n&gt;&lt;length&gt;&lt;data&gt;</c>) and <see cref="WriteTermination"/>, in one message.
    /// </summary>
    public void WriteBinaryBlock(string prefix, ReadOnlySpan<byte> data) => Write(BuildBlockMessage(prefix, data));

    /// <summary>Writes a definite-length block without holding a thread; see <see cref="WriteBinaryBlock"/>.</summary>
    public Task WriteBinaryBlockAsync(string prefix, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        WriteAsync(BuildBlockMessage(prefix, data.Span), cancellationToken).AsTask();

    private byte[] BuildBlockMessage(string prefix, ReadOnlySpan<byte> data)
    {
        var header = Ieee4882.BuildBlockHeader(data.Length);
        var prefixBytes = Encoding.GetBytes(prefix);
        var term = Encoding.GetBytes(WriteTermination);
        var message = new byte[prefixBytes.Length + header.Length + data.Length + term.Length];
        prefixBytes.CopyTo(message, 0);
        header.CopyTo(message, prefixBytes.Length);
        data.CopyTo(message.AsSpan(prefixBytes.Length + header.Length));
        term.CopyTo(message, message.Length - term.Length);
        return message;
    }

    /// <summary>
    /// Writes <paramref name="prefix"/> and <paramref name="values"/> as a definite-length block in
    /// <see cref="BinaryByteOrder"/>, e.g. <c>WriteBinaryValues("SOUR:ARB:DATA ", samples)</c>.
    /// </summary>
    public void WriteBinaryValues<T>(string prefix, ReadOnlySpan<T> values) where T : unmanaged =>
        WriteBinaryBlock(prefix, Ieee4882.ValuesToBytes(values, BinaryByteOrder));

    /// <summary>Writes values as a binary block without holding a thread; see <see cref="WriteBinaryValues{T}"/>.</summary>
    public Task WriteBinaryValuesAsync<T>(string prefix, ReadOnlyMemory<T> values, CancellationToken cancellationToken = default)
        where T : unmanaged =>
        WriteBinaryBlockAsync(prefix, Ieee4882.ValuesToBytes(values.Span, BinaryByteOrder), cancellationToken);

    // ------------------------------------------------------------------ ASCII arrays

    /// <summary>
    /// Reads a response line (see <see cref="ReadLine"/>) of numbers separated by <see cref="AsciiSeparator"/>, e.g.
    /// <c>1.0,2.5,-3E-2</c>. A field that does not parse throws VI_ERROR_INV_FMT.
    /// </summary>
    public double[] ReadAsciiValues() => ReadAsciiValues<double>();

    /// <summary>Reads separated numbers without holding a thread; see <see cref="ReadAsciiValues()"/>.</summary>
    public Task<double[]> ReadAsciiValuesAsync(CancellationToken cancellationToken = default) =>
        ReadAsciiValuesAsync<double>(cancellationToken);

    /// <summary>
    /// Reads a response line of values separated by <see cref="AsciiSeparator"/>, parsed as <typeparamref name="T"/>
    /// with the rules of <see cref="Query{T}(string)"/>.
    /// </summary>
    public T[] ReadAsciiValues<T>() where T : ISpanParsable<T> => Ieee4882.ParseValues<T>(ReadLine(), AsciiSeparator);

    /// <summary>Reads separated values without holding a thread; see <see cref="ReadAsciiValues{T}()"/>.</summary>
    public async Task<T[]> ReadAsciiValuesAsync<T>(CancellationToken cancellationToken = default) where T : ISpanParsable<T> =>
        Ieee4882.ParseValues<T>(await ReadLineAsync(cancellationToken).ConfigureAwait(false), AsciiSeparator);

    /// <summary>
    /// Sends a command and parses the response as numbers separated by <see cref="AsciiSeparator"/>, e.g.
    /// <c>QueryAsciiValues("TRAC:DATA?")</c>.
    /// </summary>
    public double[] QueryAsciiValues(string command) => QueryAsciiValues<double>(command);

    /// <summary>Sends a command and parses separated numbers without holding a thread; see <see cref="QueryAsciiValues(string)"/>.</summary>
    public Task<double[]> QueryAsciiValuesAsync(string command, CancellationToken cancellationToken = default) =>
        QueryAsciiValuesAsync<double>(command, cancellationToken);

    /// <summary>
    /// Sends a command and parses the response as values separated by <see cref="AsciiSeparator"/>, parsed as
    /// <typeparamref name="T"/> with the rules of <see cref="Query{T}(string)"/>.
    /// </summary>
    public T[] QueryAsciiValues<T>(string command) where T : ISpanParsable<T> =>
        Ieee4882.ParseValues<T>(Query(command), AsciiSeparator);

    /// <summary>Sends a command and parses separated values without holding a thread; see <see cref="QueryAsciiValues{T}(string)"/>.</summary>
    public async Task<T[]> QueryAsciiValuesAsync<T>(string command, CancellationToken cancellationToken = default)
        where T : ISpanParsable<T> =>
        Ieee4882.ParseValues<T>(await QueryAsync(command, cancellationToken).ConfigureAwait(false), AsciiSeparator);

    /// <summary>
    /// Writes <paramref name="prefix"/>, then <paramref name="values"/> formatted with the invariant culture and
    /// separated by <see cref="AsciiSeparator"/>, then <see cref="WriteTermination"/>.
    /// </summary>
    public void WriteAsciiValues<T>(string prefix, IEnumerable<T> values) where T : IFormattable =>
        WriteLine(prefix + Ieee4882.FormatValues(values, AsciiSeparator));

    /// <summary>Writes separated values without holding a thread; see <see cref="WriteAsciiValues{T}"/>.</summary>
    public Task WriteAsciiValuesAsync<T>(string prefix, IEnumerable<T> values, CancellationToken cancellationToken = default)
        where T : IFormattable =>
        WriteLineAsync(prefix + Ieee4882.FormatValues(values, AsciiSeparator), cancellationToken);
}

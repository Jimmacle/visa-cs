using System.Buffers;
using System.Globalization;
using VisaCs.Formatting;
using VisaCs.Internal;

namespace VisaCs;

// Formatted I/O (viPrintf, viScanf, viQueryf).
//
// The asynchronous methods take their CancellationToken before the params arguments (C# requires params last). For
// convenience a CancellationToken passed as the *last* format argument is used as the token too, so that
// PrintfAsync("VOLT %f\n", v, token) cancels as expected instead of silently ignoring the token.
public abstract partial class MessageBasedSession
{
    /// <summary>
    /// Formats and writes a message (viPrintf). See <see cref="VisaFormatter"/> for the format syntax. Nothing is
    /// appended: put the terminator in the format. Errors in the format or arguments throw VI_ERROR_INV_FMT or
    /// VI_ERROR_NSUP_FMT before anything is sent.
    /// </summary>
    /// <example><code>session.Printf("VOLT %.3f;:CURR %d\n", 1.5, 2);
    /// session.Printf("DATA %,3d\n", new[] { 1, 2, 3 });
    /// session.Printf("WAV:DATA %b\n", samples); // IEEE 488.2 block of bytes</code></example>
    public void Printf(string format, params object?[] args) =>
        Write(FormatMessage(format, SynchronousArguments(args), out _));

    /// <summary>
    /// Formats and writes a message (viPrintf) without holding a thread; see <see cref="Printf"/>. A
    /// <see cref="CancellationToken"/> passed as the last argument cancels the call.
    /// </summary>
    public Task PrintfAsync(string format, params object?[] args)
    {
        var (arguments, token) = SplitToken(args);
        return PrintfAsync(format, token, arguments);
    }

    /// <summary>Formats and writes a message (viPrintf) without holding a thread; see <see cref="Printf"/>.</summary>
    public Task PrintfAsync(string format, CancellationToken cancellationToken, params object?[] args) =>
        WriteAsync(FormatMessage(format, CheckArguments(args), out _), cancellationToken).AsTask();

    /// <summary>Formats a message with <see cref="VisaFormatter"/>, reporting format errors as VISA errors.</summary>
    private byte[] FormatMessage(string format, IReadOnlyList<object?> args, out int argumentsUsed)
    {
        try
        {
            return VisaFormatter.Format(format, args, Encoding, out argumentsUsed);
        }
        catch (Exception ex) when (FormattingErrorMapping.IsFormattingError(ex))
        {
            throw FormattingErrorMapping.ToVisa(ex);
        }
    }

    /// <summary>
    /// Reads a response and parses it (viScanf); see <see cref="VisaFormatter"/> for the format syntax.
    /// <paramref name="args"/> supply widths and array counts written as <c>*</c>. Returns the converted values in order;
    /// fewer than requested when the input stops matching the format.
    /// </summary>
    public object?[] Scanf(string format, params object?[] args) =>
        AsyncIO.Complete(ScanfAsync(format, SynchronousArguments(args), CancellationToken.None, async: false));

    /// <summary>
    /// Reads a response and parses it (viScanf) without holding a thread; see <see cref="Scanf(string, object?[])"/>.
    /// A <see cref="CancellationToken"/> passed as the last argument cancels the call.
    /// </summary>
    public Task<object?[]> ScanfAsync(string format, params object?[] args)
    {
        var (arguments, token) = SplitToken(args);
        return ScanfAsync(format, token, arguments);
    }

    /// <summary>Reads a response and parses it (viScanf) without holding a thread; see <see cref="Scanf(string, object?[])"/>.</summary>
    public Task<object?[]> ScanfAsync(string format, CancellationToken cancellationToken, params object?[] args) =>
        ScanfAsync(format, CheckArguments(args), cancellationToken, async: true).AsTask();

    private ValueTask<object?[]> ScanfAsync(string format, object?[] args, CancellationToken cancellationToken, bool async) =>
        ExecuteAsync((format, args), static (s, a, ct, async) => s.ScanUnguardedAsync(a.format, a.args, ct, async), cancellationToken, async);

    private async ValueTask<object?[]> ScanUnguardedAsync(string format, IReadOnlyList<object?> args, CancellationToken cancellationToken, bool async)
    {
        var data = VisaFormatter.HasBinaryConversion(format)
            ? await ReadMessageWithBlocksAsync(cancellationToken, async).ConfigureAwait(false)
            : await ReadToEndUnguardedAsync(cancellationToken, async).ConfigureAwait(false);
        return FormattingErrorMapping.Call((format, data, args), static s => VisaFormatter.Parse(s.format, s.data, s.args));
    }

    /// <summary>
    /// Reads a response and returns its first converted value as <typeparamref name="T"/>; throws VI_ERROR_INV_FMT if
    /// nothing matched. See <see cref="Scanf(string, object?[])"/>.
    /// </summary>
    public T Scanf<T>(string format, params object?[] args) => ConvertResult<T>(Scanf(format, args), 0, format);

    /// <summary>
    /// Reads a response and returns its first converted value without holding a thread; see <see cref="Scanf{T}"/>.
    /// A <see cref="CancellationToken"/> passed as the last argument cancels the call.
    /// </summary>
    public Task<T> ScanfAsync<T>(string format, params object?[] args)
    {
        var (arguments, token) = SplitToken(args);
        return ScanfAsync<T>(format, token, arguments);
    }

    /// <summary>Reads a response and returns its first converted value without holding a thread; see <see cref="Scanf{T}"/>.</summary>
    public async Task<T> ScanfAsync<T>(string format, CancellationToken cancellationToken, params object?[] args) =>
        ConvertResult<T>(await ScanfAsync(format, cancellationToken, args).ConfigureAwait(false), 0, format);

    /// <summary>
    /// Formats and writes a command, waits <see cref="QueryDelay"/>, then reads and parses the response (viQueryf).
    /// <paramref name="args"/> first supply the write format's arguments; the arguments left over supply widths and
    /// counts written as <c>*</c> in <paramref name="readFormat"/>. Nothing is appended to the command: put the
    /// terminator in <paramref name="writeFormat"/>.
    /// </summary>
    public object?[] Queryf(string writeFormat, string readFormat, params object?[] args) =>
        AsyncIO.Complete(QueryfAsync(writeFormat, readFormat, SynchronousArguments(args), CancellationToken.None, async: false));

    /// <summary>
    /// Formats and writes a command, then reads and parses the response (viQueryf), without holding a thread; see
    /// <see cref="Queryf(string, string, object?[])"/>. A <see cref="CancellationToken"/> passed as the last argument
    /// cancels the call.
    /// </summary>
    public Task<object?[]> QueryfAsync(string writeFormat, string readFormat, params object?[] args)
    {
        var (arguments, token) = SplitToken(args);
        return QueryfAsync(writeFormat, readFormat, token, arguments);
    }

    /// <summary>
    /// Formats and writes a command, then reads and parses the response (viQueryf), without holding a thread; see
    /// <see cref="Queryf(string, string, object?[])"/>.
    /// </summary>
    public Task<object?[]> QueryfAsync(string writeFormat, string readFormat, CancellationToken cancellationToken, params object?[] args) =>
        QueryfAsync(writeFormat, readFormat, CheckArguments(args), cancellationToken, async: true).AsTask();

    private ValueTask<object?[]> QueryfAsync(string writeFormat, string readFormat, object?[] args, CancellationToken cancellationToken, bool async)
    {
        var message = FormatMessage(writeFormat, args, out int used);
        var readArgs = new ArraySegment<object?>(args, used, args.Length - used);
        return ExecuteAsync((message, readFormat, readArgs), static async (s, a, ct, async) =>
        {
            await s.WriteUnguardedAsync(a.message, ct, async).ConfigureAwait(false);
            await s.QueryDelayAsync(ct, async).ConfigureAwait(false);
            return await s.ScanUnguardedAsync(a.readFormat, a.readArgs, ct, async).ConfigureAwait(false);
        }, cancellationToken, async);
    }

    /// <summary>
    /// Like <see cref="Queryf(string, string, object?[])"/>, returning the first value as <typeparamref name="T"/>;
    /// throws VI_ERROR_INV_FMT if nothing matched.
    /// </summary>
    public T Queryf<T>(string writeFormat, string readFormat, params object?[] args) =>
        ConvertResult<T>(Queryf(writeFormat, readFormat, args), 0, readFormat);

    /// <summary>
    /// Like <see cref="Queryf{T}"/>, without holding a thread. A <see cref="CancellationToken"/> passed as the last
    /// argument cancels the call.
    /// </summary>
    public Task<T> QueryfAsync<T>(string writeFormat, string readFormat, params object?[] args)
    {
        var (arguments, token) = SplitToken(args);
        return QueryfAsync<T>(writeFormat, readFormat, token, arguments);
    }

    /// <summary>Like <see cref="Queryf{T}"/>, without holding a thread.</summary>
    public async Task<T> QueryfAsync<T>(string writeFormat, string readFormat, CancellationToken cancellationToken, params object?[] args) =>
        ConvertResult<T>(await QueryfAsync(writeFormat, readFormat, cancellationToken, args).ConfigureAwait(false), 0, readFormat);

    /// <summary>Separates a trailing CancellationToken from the format arguments.</summary>
    private static (object?[] Args, CancellationToken Token) SplitToken(object?[] args)
    {
        if (args is [.., CancellationToken token]) return (CheckArguments(args[..^1]), token);
        return (CheckArguments(args), default);
    }

    private static object?[] CheckArguments(object?[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        foreach (var arg in args)
        {
            if (arg is CancellationToken)
                throw new ArgumentException("A CancellationToken is not a format argument; pass it as the last argument of an asynchronous method.", nameof(args));
        }
        return args;
    }

    private static object?[] SynchronousArguments(object?[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        foreach (var arg in args)
        {
            if (arg is CancellationToken)
                throw new ArgumentException("A CancellationToken is not a format argument, and synchronous calls cannot be cancelled; use the asynchronous method.", nameof(args));
        }
        return args;
    }

    private static T ConvertResult<T>(object?[] results, int index, string format)
    {
        if (results.Length <= index)
            throw new VisaException(VisaStatus.ErrorInvalidFormat, $"The response does not match the format '{format}'.");
        object? value = results[index];
        if (value is T t) return t;
        if (typeof(T).IsArray && value is Array source)
        {
            // Typed conversions (rather than Array.CreateInstance) keep this AOT-compatible.
            object? converted =
                typeof(T) == typeof(double[]) ? ConvertArray<double>(source) :
                typeof(T) == typeof(float[]) ? ConvertArray<float>(source) :
                typeof(T) == typeof(decimal[]) ? ConvertArray<decimal>(source) :
                typeof(T) == typeof(long[]) ? ConvertArray<long>(source) :
                typeof(T) == typeof(int[]) ? ConvertArray<int>(source) :
                typeof(T) == typeof(short[]) ? ConvertArray<short>(source) :
                typeof(T) == typeof(sbyte[]) ? ConvertArray<sbyte>(source) :
                typeof(T) == typeof(ulong[]) ? ConvertArray<ulong>(source) :
                typeof(T) == typeof(uint[]) ? ConvertArray<uint>(source) :
                typeof(T) == typeof(ushort[]) ? ConvertArray<ushort>(source) :
                typeof(T) == typeof(byte[]) ? ConvertArray<byte>(source) :
                typeof(T) == typeof(string[]) ? ConvertArray<string>(source) :
                null;
            if (converted is not null) return (T)converted;
            throw new VisaException(VisaStatus.ErrorInvalidFormat, $"Cannot convert {value.GetType().Name} to {typeof(T).Name}.");
        }
        try
        {
            return ValueConverter.Convert<T>(value);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            throw new VisaException(VisaStatus.ErrorInvalidFormat, $"Cannot convert '{value}' to {typeof(T).Name}.", ex);
        }
    }

    private static TElement[] ConvertArray<TElement>(Array source)
    {
        var result = new TElement[source.Length];
        for (int i = 0; i < source.Length; i++)
            result[i] = (TElement)Convert.ChangeType(source.GetValue(i), typeof(TElement), CultureInfo.InvariantCulture)!;
        return result;
    }

    /// <summary>
    /// Reads one message, treating IEEE 488.2 blocks as opaque binary data (termination characters inside them do not
    /// end the read). The text around blocks is read byte by byte under one timeout; each block gets its own.
    /// </summary>
    private async ValueTask<byte[]> ReadMessageWithBlocksAsync(CancellationToken cancellationToken, bool async)
    {
        var output = new ArrayBufferWriter<byte>(256);
        var one = new byte[1];
        var deadline = NewDeadline(cancellationToken);
        while (true)
        {
            var result = await ReadCoreAsync(one, deadline, async).ConfigureAwait(false);
            if (result.Count == 1) output.Write(one);
            if (result.Status != ReadStatus.MaximumCountReached) return output.WrittenSpan.ToArray();
            if (one[0] != (byte)'#') continue;

            // Possible block header: '#', a digit, then that many length digits ('#0' starts an indefinite block).
            bool saved = _suppressTermChar;
            _suppressTermChar = true;
            try
            {
                result = await ReadCoreAsync(one, deadline, async).ConfigureAwait(false);
                if (result.Count == 1) output.Write(one);
                if (result.Status != ReadStatus.MaximumCountReached) return output.WrittenSpan.ToArray();
                if (one[0] == (byte)'0' && HasEndIndicator)
                {
                    // Indefinite block: everything up to END belongs to it.
                    output.Write(await ReadToEndUnguardedAsync(cancellationToken, async).ConfigureAwait(false));
                    return output.WrittenSpan.ToArray();
                }
                if (one[0] is < (byte)'1' or > (byte)'9') continue;
                var lengthText = new byte[one[0] - '0'];
                bool ended = await ReadExactlyAsync(lengthText, deadline, cancellationToken, async).ConfigureAwait(false);
                output.Write(lengthText);
                if (ended) return output.WrittenSpan.ToArray();
                if (!Ieee4882.TryParseBlockLength(lengthText, out int length)) continue;
                var (block, endSeen) = await ReadBlockDataAsync(length, cancellationToken, async).ConfigureAwait(false);
                output.Write(block);
                if (endSeen) return output.WrittenSpan.ToArray();
                deadline = NewDeadline(cancellationToken);
            }
            finally
            {
                _suppressTermChar = saved;
            }
        }
    }
}

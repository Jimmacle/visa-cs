using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace VisaCs.Formatting;

/// <summary>
/// IEEE 488.2 data formats: arbitrary blocks (<c>#&lt;n&gt;&lt;length&gt;&lt;data&gt;</c>), binary arrays in a
/// <see cref="ByteOrder"/>, and NR1/NR2/NR3 numbers in comma-separated text responses. Usable over any transport.
/// </summary>
/// <remarks>Errors in the data throw <see cref="VisaException"/> with <see cref="VisaStatus.ErrorInvalidFormat"/>.</remarks>
/// <example><code>
/// byte[] data = Ieee4882.ParseBlock(response);                          // "#18" + 8 bytes + "\n"
/// float[] samples = Ieee4882.BytesToValues&lt;float&gt;(data, ByteOrder.LittleEndian);
/// byte[] message = [.. "SOUR:ARB:DATA "u8, .. Ieee4882.BuildBlock(Ieee4882.ValuesToBytes&lt;float&gt;(samples, ByteOrder.LittleEndian)), .. "\n"u8];
/// double[] trace = Ieee4882.ParseValues&lt;double&gt;("1.5E+00,-2.0E-01\n");
/// </code></example>
public static class Ieee4882
{
    // ------------------------------------------------------------------ blocks

    /// <summary>The definite-length block header <c>#&lt;n&gt;&lt;length&gt;</c> for <paramref name="length"/> data bytes.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative or above 999,999,999.</exception>
    public static byte[] BuildBlockHeader(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        string digits = length.ToString(CultureInfo.InvariantCulture);
        if (digits.Length > 9) throw new ArgumentOutOfRangeException(nameof(length), "IEEE 488.2 blocks hold at most 999,999,999 bytes.");
        return Encoding.ASCII.GetBytes($"#{digits.Length}{digits}");
    }

    /// <summary>A definite-length block: the header followed by <paramref name="data"/>.</summary>
    public static byte[] BuildBlock(ReadOnlySpan<byte> data)
    {
        var header = BuildBlockHeader(data.Length);
        var block = new byte[header.Length + data.Length];
        header.CopyTo(block, 0);
        data.CopyTo(block.AsSpan(header.Length));
        return block;
    }

    /// <summary>Parses the length digits of a definite-length block header (the part after <c>#&lt;n&gt;</c>); false if they are not a valid length.</summary>
    public static bool TryParseBlockLength(ReadOnlySpan<byte> digits, out int length)
    {
        length = 0;
        if (digits.IsEmpty || digits.Length > 9) return false;
        foreach (byte b in digits)
        {
            if (b is < (byte)'0' or > (byte)'9') return false;
            length = length * 10 + (b - '0');
        }
        return true;
    }

    /// <summary>
    /// Parses a block header at the start of <paramref name="header"/> (which must start with <c>#</c>).
    /// <paramref name="headerLength"/> is the number of header bytes; <paramref name="dataLength"/> the announced
    /// data length, or -1 for an indefinite-length block (<c>#0</c>, data up to the end of the message).
    /// False if the header is malformed or not complete yet.
    /// </summary>
    public static bool TryParseBlockHeader(ReadOnlySpan<byte> header, out int headerLength, out int dataLength)
    {
        headerLength = 0;
        dataLength = 0;
        if (header.Length < 2 || header[0] != (byte)'#' || header[1] is < (byte)'0' or > (byte)'9') return false;
        int digits = header[1] - '0';
        if (digits == 0)
        {
            headerLength = 2;
            dataLength = -1;
            return true;
        }
        if (header.Length < 2 + digits || !TryParseBlockLength(header.Slice(2, digits), out dataLength)) return false;
        headerLength = 2 + digits;
        return true;
    }

    /// <summary>
    /// Finds the block in a complete message: text before the first <c>#</c> is skipped, and the data of an
    /// indefinite-length block runs to the end of the message without a final line feed. <paramref name="data"/> is
    /// the range of the data in <paramref name="message"/>. False if there is no valid header or the message is
    /// shorter than the announced length.
    /// </summary>
    public static bool TryParseBlock(ReadOnlySpan<byte> message, out Range data)
    {
        data = default;
        int hash = message.IndexOf((byte)'#');
        if (hash < 0 || !TryParseBlockHeader(message[hash..], out int headerLength, out int length)) return false;
        int start = hash + headerLength;
        if (length < 0)
        {
            int end = message.Length;
            if (end > start && message[end - 1] == (byte)'\n') end--;
            data = start..end;
            return true;
        }
        if (length > message.Length - start) return false;
        data = start..(start + length);
        return true;
    }

    /// <summary>
    /// The data of the block in a complete message (see <see cref="TryParseBlock"/>). Throws
    /// <see cref="VisaStatus.ErrorInvalidFormat"/> if there is no valid block.
    /// </summary>
    public static byte[] ParseBlock(ReadOnlySpan<byte> message) =>
        TryParseBlock(message, out var data)
            ? message[data].ToArray()
            : throw FormatErrors.Invalid("No complete IEEE 488.2 block in the message.");

    // ------------------------------------------------------------------ binary values

    /// <summary>
    /// Reinterprets block data as an array of <typeparamref name="T"/> sent in <paramref name="byteOrder"/>.
    /// Throws <see cref="VisaStatus.ErrorInvalidFormat"/> if the length is not a multiple of the element size.
    /// </summary>
    public static T[] BytesToValues<T>(ReadOnlySpan<byte> data, ByteOrder byteOrder) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        if (data.Length % size != 0)
            throw FormatErrors.Invalid(
                $"Block length {data.Length} is not a multiple of the element size {size}.");
        var values = new T[data.Length / size];
        var bytes = MemoryMarshal.AsBytes(values.AsSpan());
        data.CopyTo(bytes);
        if (NeedsSwap(byteOrder)) SwapBytes(bytes, size);
        return values;
    }

    /// <summary>The bytes of <paramref name="values"/> in <paramref name="byteOrder"/>.</summary>
    public static byte[] ValuesToBytes<T>(ReadOnlySpan<T> values, ByteOrder byteOrder) where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(values).ToArray();
        if (NeedsSwap(byteOrder)) SwapBytes(bytes, Unsafe.SizeOf<T>());
        return bytes;
    }

    private static bool NeedsSwap(ByteOrder byteOrder) => (byteOrder == ByteOrder.BigEndian) == BitConverter.IsLittleEndian;

    /// <summary>Reverses the bytes of each <paramref name="elementSize"/>-byte element in place; a partial last element is left alone.</summary>
    public static void SwapBytes(Span<byte> data, int elementSize)
    {
        if (elementSize <= 1) return;
        for (int i = 0; i + elementSize <= data.Length; i += elementSize)
            data.Slice(i, elementSize).Reverse();
    }

    // ------------------------------------------------------------------ ASCII values

    /// <summary>
    /// Parses separated values with <see cref="TryParseValue"/>; surrounding white space and empty fields are
    /// skipped. Throws <see cref="VisaStatus.ErrorInvalidFormat"/> for a field that does not parse.
    /// </summary>
    public static T[] ParseValues<T>(ReadOnlySpan<char> text, char separator = ',') where T : ISpanParsable<T>
    {
        // Parse each field in place: no per-value string allocations, which matters for long waveform/trace responses.
        var result = new List<T>(text.Count(separator) + 1);
        foreach (var range in text.Split(separator))
        {
            var part = text[range].Trim();
            if (part.IsEmpty) continue;
            if (!TryParseValue<T>(part, out var value))
                throw FormatErrors.Invalid($"Cannot parse '{part}' as {typeof(T).Name}.");
            result.Add(value);
        }
        return result.ToArray();
    }

    /// <summary>
    /// Parses one value with <see cref="TryParseValue"/>; surrounding white space is ignored. Throws
    /// <see cref="VisaStatus.ErrorInvalidFormat"/> if it does not parse.
    /// </summary>
    public static T ParseValue<T>(ReadOnlySpan<char> text) where T : ISpanParsable<T>
    {
        text = text.Trim();
        return TryParseValue<T>(text, out var value)
            ? value
            : throw FormatErrors.Invalid($"Cannot parse '{text}' as {typeof(T).Name}.");
    }

    /// <summary>
    /// Parses one value the way instruments send them, with the invariant culture: decimals accept exponents (NR3),
    /// integers sent in NR2/NR3 form (<c>1.000E+00</c>) are rounded, and booleans accept numbers (non-zero is true),
    /// <c>ON</c>/<c>OFF</c> and <c>TRUE</c>/<c>FALSE</c>. White space is not skipped.
    /// </summary>
    public static bool TryParseValue<T>(ReadOnlySpan<char> text, [MaybeNullWhen(false)] out T value) where T : ISpanParsable<T>
    {
        // decimal's ISpanParsable uses NumberStyles.Number, which rejects exponents.
        if (typeof(T) == typeof(decimal))
        {
            bool ok = decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d);
            value = ok ? (T)(object)d : default;
            return ok;
        }
        if (typeof(T) == typeof(bool) && TryParseBoolean(text, out bool flag))
        {
            value = (T)(object)flag;
            return true;
        }
        if (T.TryParse(text, CultureInfo.InvariantCulture, out value)) return true;
        if (IsInteger<T>() &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
        {
            Span<char> digits = stackalloc char[32];
            if (Math.Round(number, MidpointRounding.AwayFromZero).TryFormat(digits, out int written, "F0", CultureInfo.InvariantCulture))
                return T.TryParse(digits[..written], CultureInfo.InvariantCulture, out value);
        }
        value = default;
        return false;
    }

    private static bool TryParseBoolean(ReadOnlySpan<char> text, out bool value)
    {
        if (text.Equals("ON", StringComparison.OrdinalIgnoreCase) || text.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }
        if (text.Equals("OFF", StringComparison.OrdinalIgnoreCase) || text.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }
        bool ok = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && !double.IsNaN(number);
        value = ok && number != 0;
        return ok;
    }

    private static bool IsInteger<T>() =>
        typeof(T) == typeof(int) || typeof(T) == typeof(long) || typeof(T) == typeof(short) || typeof(T) == typeof(sbyte) ||
        typeof(T) == typeof(uint) || typeof(T) == typeof(ulong) || typeof(T) == typeof(ushort) || typeof(T) == typeof(byte) ||
        typeof(T) == typeof(Int128) || typeof(T) == typeof(UInt128) || typeof(T) == typeof(nint) || typeof(T) == typeof(nuint);

    /// <summary>Formats values with the invariant culture, separated by <paramref name="separator"/>.</summary>
    public static string FormatValues<T>(IEnumerable<T> values, char separator = ',') where T : IFormattable =>
        string.Join(separator, values.Select(v => v.ToString(null, CultureInfo.InvariantCulture)));
}

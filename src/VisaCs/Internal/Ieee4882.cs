using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace VisaCs.Internal;

/// <summary>
/// IEEE 488.2 data formats shared by the session helpers and <see cref="Formatting.VisaFormatter"/>: arbitrary
/// block headers, binary arrays and NR1/NR2/NR3 numbers in text responses.
/// </summary>
internal static class Ieee4882
{
    /// <summary>The definite-length block header <c>#&lt;n&gt;&lt;length&gt;</c> for <paramref name="length"/> data bytes.</summary>
    public static byte[] BuildBlockHeader(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        string digits = length.ToString(CultureInfo.InvariantCulture);
        if (digits.Length > 9) throw new ArgumentOutOfRangeException(nameof(length), "IEEE 488.2 blocks hold at most 999,999,999 bytes.");
        return Encoding.ASCII.GetBytes($"#{digits.Length}{digits}");
    }

    /// <summary>Parses the length digits of a definite-length block header; false if they are not a valid length.</summary>
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

    /// <summary>Reinterprets block data as an array of <typeparamref name="T"/>, swapping bytes if needed.</summary>
    public static T[] BytesToValues<T>(byte[] data, ByteOrder byteOrder) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        if (data.Length % size != 0)
            throw new VisaException(VisaStatus.ErrorInvalidFormat,
                $"Block length {data.Length} is not a multiple of the element size {size}.");
        if (NeedsSwap(byteOrder) && size > 1)
        {
            data = (byte[])data.Clone();
            SwapBytes(data, size);
        }
        return MemoryMarshal.Cast<byte, T>(data).ToArray();
    }

    /// <summary>The bytes of <paramref name="values"/> in <paramref name="byteOrder"/>.</summary>
    public static byte[] ValuesToBytes<T>(ReadOnlySpan<T> values, ByteOrder byteOrder) where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(values).ToArray();
        if (NeedsSwap(byteOrder)) SwapBytes(bytes, Unsafe.SizeOf<T>());
        return bytes;
    }

    private static bool NeedsSwap(ByteOrder byteOrder) => (byteOrder == ByteOrder.BigEndian) == BitConverter.IsLittleEndian;

    public static void SwapBytes(Span<byte> data, int elementSize)
    {
        if (elementSize <= 1) return;
        for (int i = 0; i + elementSize <= data.Length; i += elementSize)
            data.Slice(i, elementSize).Reverse();
    }

    /// <summary>Parses separated values; empty fields are skipped. Throws VI_ERROR_INV_FMT for a field that does not parse.</summary>
    public static T[] ParseValues<T>(ReadOnlySpan<char> text, char separator) where T : ISpanParsable<T>
    {
        // Parse each field in place: no per-value string allocations, which matters for long waveform/trace responses.
        var result = new List<T>(text.Count(separator) + 1);
        foreach (var range in text.Split(separator))
        {
            var part = text[range].Trim();
            if (part.IsEmpty) continue;
            if (!TryParseValue<T>(part, out var value))
                throw new VisaException(VisaStatus.ErrorInvalidFormat, $"Cannot parse '{part}' as {typeof(T).Name}.");
            result.Add(value);
        }
        return result.ToArray();
    }

    /// <summary>Parses one value (surrounding white space is ignored). Throws VI_ERROR_INV_FMT if it does not parse.</summary>
    public static T ParseValue<T>(ReadOnlySpan<char> text) where T : ISpanParsable<T>
    {
        text = text.Trim();
        return TryParseValue<T>(text, out var value)
            ? value
            : throw new VisaException(VisaStatus.ErrorInvalidFormat, $"Cannot parse '{text}' as {typeof(T).Name}.");
    }

    /// <summary>
    /// Parses one value the way instruments send them: decimals accept exponents (NR3), integers sent in NR2/NR3
    /// form (<c>1.000E+00</c>) are rounded, and booleans accept <c>1</c>/<c>0</c> and <c>ON</c>/<c>OFF</c>.
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
    public static string FormatValues<T>(IEnumerable<T> values, char separator) where T : IFormattable =>
        string.Join(separator, values.Select(v => v.ToString(null, CultureInfo.InvariantCulture)));
}

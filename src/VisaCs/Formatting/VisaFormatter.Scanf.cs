using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using VisaCs.Internal;

namespace VisaCs.Formatting;

// viScanf: parsing.
public static partial class VisaFormatter
{
    /// <summary>
    /// Parses a response like viScanf. <paramref name="args"/> supply widths and array counts written as <c>*</c>.
    /// Parsing stops at the first mismatch; the values converted up to that point are returned. Text is read as
    /// Latin-1.
    /// </summary>
    public static object?[] Parse(string format, ReadOnlySpan<byte> input, IReadOnlyList<object?>? args = null)
    {
        ArgumentNullException.ThrowIfNull(format);
        return ParseCore(format, input, args ?? Array.Empty<object?>());
    }

    /// <summary>Parses a string (viSScanf); see <see cref="Parse"/>.</summary>
    public static object?[] ParseString(string format, string input, params object?[] args) =>
        Parse(format, Encoding.Latin1.GetBytes(input), args);

    private static object?[] ParseCore(string format, ReadOnlySpan<byte> data, IReadOnlyList<object?> args)
    {
        var results = new List<object?>();
        int pos = 0;
        int argIndex = 0;

        for (int i = 0; i < format.Length;)
        {
            char f = format[i];
            if (char.IsWhiteSpace(f))
            {
                while (i < format.Length && char.IsWhiteSpace(format[i])) i++;
                SkipWhitespace(data, ref pos);
                continue;
            }
            if (f != '%' || (i + 1 < format.Length && format[i + 1] == '%'))
            {
                i += f == '%' ? 2 : 1;
                if (pos >= data.Length || data[pos] != (byte)f) return results.ToArray();
                pos++;
                continue;
            }
            i++;
            var spec = ParseSpec(format, ref i, scan: true);
            if (spec.WidthFromArg) spec.Width = argIndex < args.Count ? ToInt(args[argIndex++]) : null;
            int count = spec.Array switch
            {
                ArrayMode.Fixed => spec.ArrayCount,
                ArrayMode.FromArgument => argIndex < args.Count ? ToInt(args[argIndex++]) : int.MaxValue,
                _ => int.MaxValue,
            };

            object? value;
            switch (spec.Conversion)
            {
                case 'b':
                case 'B':
                {
                    int hash = data[pos..].IndexOf((byte)'#');
                    if (hash < 0) return results.ToArray();
                    pos += hash + 1;
                    if (pos >= data.Length || data[pos] < '0' || data[pos] > '9') return results.ToArray();
                    int digits = data[pos++] - '0';
                    int length;
                    if (digits == 0)
                    {
                        length = data.Length - pos;
                        if (length > 0 && data[pos + length - 1] == '\n') length--;
                    }
                    else
                    {
                        if (pos + digits > data.Length || !Ieee4882.TryParseBlockLength(data.Slice(pos, digits), out length))
                            return results.ToArray();
                        pos += digits;
                        length = Math.Min(length, data.Length - pos);
                    }
                    value = DecodeBinary(data.Slice(pos, length), spec, spec.Array == ArrayMode.None || spec.Array == ArrayMode.Any ? int.MaxValue : count);
                    pos += length;
                    break;
                }
                case 'y':
                {
                    int size = ElementSize(spec.Size);
                    int elements = count == int.MaxValue ? (data.Length - pos) / size : count;
                    int length = (int)Math.Min((long)elements * size, (data.Length - pos) / size * size);
                    value = DecodeBinary(data.Slice(pos, length), spec, elements);
                    pos += length;
                    break;
                }
                case 't':
                    value = Encoding.Latin1.GetString(data[pos..]);
                    pos = data.Length;
                    break;
                case 'T':
                {
                    int lf = data[pos..].IndexOf((byte)'\n');
                    int end = lf < 0 ? data.Length : pos + lf;
                    value = Encoding.Latin1.GetString(data[pos..end]);
                    pos = lf < 0 ? data.Length : end + 1;
                    break;
                }
                case 'c':
                {
                    int width = spec.Width ?? 1;
                    if (pos + width > data.Length) return results.ToArray();
                    value = width == 1 ? (object)(char)data[pos] : Encoding.Latin1.GetString(data.Slice(pos, width));
                    pos += width;
                    break;
                }
                case '[':
                {
                    int start = pos;
                    int limit = spec.Width ?? int.MaxValue;
                    while (pos < data.Length && pos - start < limit && spec.CharSet!.Contains((char)data[pos]) != spec.NegatedSet) pos++;
                    if (pos == start) return results.ToArray();
                    value = Encoding.Latin1.GetString(data[start..pos]);
                    break;
                }
                case 's':
                {
                    SkipWhitespace(data, ref pos);
                    int start = pos;
                    int limit = spec.Width ?? int.MaxValue;
                    while (pos < data.Length && pos - start < limit && !char.IsWhiteSpace((char)data[pos])) pos++;
                    if (pos == start) return results.ToArray();
                    value = Encoding.Latin1.GetString(data[start..pos]);
                    break;
                }
                default:
                {
                    // Numeric conversions, optionally as comma-separated arrays.
                    if (spec.Array == ArrayMode.None)
                    {
                        if (!TryScanNumber(data, ref pos, spec, out value)) return results.ToArray();
                        break;
                    }
                    var list = new List<object>();
                    while (list.Count < count)
                    {
                        int save = pos;
                        if (!TryScanNumber(data, ref pos, spec, out var element))
                        {
                            pos = save;
                            break;
                        }
                        list.Add(element!);
                        int afterNumber = pos;
                        SkipWhitespace(data, ref pos);
                        if (list.Count < count && pos < data.Length && data[pos] == ',') pos++;
                        else
                        {
                            pos = afterNumber;
                            break;
                        }
                    }
                    value = ToTypedArray(list, spec);
                    break;
                }
            }
            if (!spec.Suppress) results.Add(value);
        }
        return results.ToArray();
    }

    private static void SkipWhitespace(ReadOnlySpan<byte> data, ref int pos)
    {
        while (pos < data.Length && char.IsWhiteSpace((char)data[pos])) pos++;
    }

    private static bool TryScanNumber(ReadOnlySpan<byte> data, ref int pos, Spec spec, out object? value)
    {
        value = null;
        SkipWhitespace(data, ref pos);
        int limit = spec.Width is { } w ? Math.Min(data.Length, pos + w) : data.Length;
        int start = pos;
        char conversion = spec.Conversion;

        if (conversion is 'f' or 'e' or 'E' or 'g' or 'G')
        {
            int p = pos;
            if (p < limit && data[p] is (byte)'+' or (byte)'-') p++;
            int digitsStart = p;
            while (p < limit && char.IsDigit((char)data[p])) p++;
            if (p < limit && data[p] == '.')
            {
                p++;
                while (p < limit && char.IsDigit((char)data[p])) p++;
            }
            if (p == digitsStart || (p == digitsStart + 1 && data[digitsStart] == '.'))
            {
                // inf / nan
                var word = Encoding.ASCII.GetString(data[digitsStart..Math.Min(limit, digitsStart + 3)]).ToUpperInvariant();
                if (word is "INF" or "NAN")
                {
                    pos = digitsStart + 3;
                    bool neg = data[start] == '-';
                    value = word == "NAN" ? double.NaN : neg ? double.NegativeInfinity : double.PositiveInfinity;
                    return true;
                }
                return false;
            }
            if (p < limit && data[p] is (byte)'e' or (byte)'E')
            {
                int q = p + 1;
                if (q < limit && data[q] is (byte)'+' or (byte)'-') q++;
                int expDigits = q;
                while (q < limit && char.IsDigit((char)data[q])) q++;
                if (q > expDigits) p = q;
            }
            if (!double.TryParse(Encoding.ASCII.GetString(data[start..p]), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return false;
            value = number;
            pos = p;
            return true;
        }

        int radix = conversion switch { 'x' or 'X' => 16, 'o' => 8, _ => 10 };
        int q2 = pos;
        bool negative = false;
        if (q2 < limit && data[q2] is (byte)'+' or (byte)'-')
        {
            negative = data[q2] == '-';
            q2++;
        }
        if (conversion == 'i' && q2 + 1 < limit && data[q2] == '0' && (data[q2 + 1] | 0x20) == 'x')
        {
            radix = 16;
            q2 += 2;
        }
        else if (conversion is 'x' or 'X' && q2 + 1 < limit && data[q2] == '0' && (data[q2 + 1] | 0x20) == 'x')
        {
            q2 += 2;
        }
        else if (conversion == 'i' && q2 + 1 < limit && data[q2] == '0' && char.IsDigit((char)data[q2 + 1]))
        {
            radix = 8;
        }
        int digitStart = q2;
        ulong magnitude = 0;
        while (q2 < limit)
        {
            int d = HexValue(data[q2]);
            if (d < 0 || d >= radix) break;
            magnitude = unchecked(magnitude * (ulong)radix + (ulong)d);
            q2++;
        }
        if (q2 == digitStart)
        {
            // An NR2/NR3 value where an integer was expected (e.g. "+1.00000E+00"): accept it.
            if (conversion is 'd' or 'i' && TryScanNumber(data, ref pos, new Spec { Conversion = 'f', Width = spec.Width }, out var d))
            {
                value = ConvertInteger(RoundToInteger((double)d!), spec);
                return true;
            }
            return false;
        }
        // Accept a fractional part on integers ("1.000E+00") by parsing as a float.
        if (conversion is 'd' or 'i' && radix == 10 && q2 < limit && data[q2] is (byte)'.' or (byte)'e' or (byte)'E')
        {
            int save = pos;
            if (TryScanNumber(data, ref pos, new Spec { Conversion = 'f', Width = spec.Width }, out var d))
            {
                value = ConvertInteger(RoundToInteger((double)d!), spec);
                return true;
            }
            pos = save;
        }
        pos = q2;
        long signed = negative ? -unchecked((long)magnitude) : unchecked((long)magnitude);
        value = conversion is 'u' or 'x' or 'X' or 'o' ? ConvertUnsigned(magnitude, spec) : ConvertInteger(signed, spec);
        return true;
    }

    /// <summary>Rounds half away from zero, like the session's integer parsing; saturates outside the long range.</summary>
    private static long RoundToInteger(double value)
    {
        if (double.IsNaN(value)) return 0;
        double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        return rounded >= long.MaxValue ? long.MaxValue : rounded <= long.MinValue ? long.MinValue : (long)rounded;
    }

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    // Each arm is cast to object: otherwise the switch expression unifies short/int/long to long.
    private static object ConvertInteger(long v, Spec spec) => spec.Size switch
    {
        "h" => (object)unchecked((short)v),
        "L" or "ll" => (object)v,
        _ => (object)unchecked((int)v),
    };

    private static object ConvertUnsigned(ulong v, Spec spec) => spec.Size switch
    {
        "h" => (object)unchecked((ushort)v),
        "L" or "ll" => (object)v,
        _ => (object)unchecked((uint)v),
    };

    private static object ToTypedArray(List<object> list, Spec spec)
    {
        if (spec.Conversion is 'f' or 'e' or 'E' or 'g' or 'G') return list.Select(o => (double)o).ToArray();
        if (spec.Conversion is 'u' or 'x' or 'X' or 'o')
        {
            return spec.Size switch
            {
                "h" => (object)list.Select(o => (ushort)o).ToArray(),
                "L" or "ll" => (object)list.Select(o => (ulong)o).ToArray(),
                _ => (object)list.Select(o => (uint)o).ToArray(),
            };
        }
        return spec.Size switch
        {
            "h" => (object)list.Select(o => (short)o).ToArray(),
            "L" or "ll" => (object)list.Select(o => (long)o).ToArray(),
            _ => (object)list.Select(o => (int)o).ToArray(),
        };
    }

    private static object DecodeBinary(ReadOnlySpan<byte> data, Spec spec, int maxCount)
    {
        int size = ElementSize(spec.Size);
        int n = Math.Min(data.Length / size, maxCount);
        bool big = !spec.LittleEndian;
        switch (spec.Size)
        {
            case "h":
            {
                var r = new short[n];
                for (int k = 0; k < n; k++) r[k] = big ? BinaryPrimitives.ReadInt16BigEndian(data[(k * 2)..]) : BinaryPrimitives.ReadInt16LittleEndian(data[(k * 2)..]);
                return r;
            }
            case "l":
            {
                var r = new int[n];
                for (int k = 0; k < n; k++) r[k] = big ? BinaryPrimitives.ReadInt32BigEndian(data[(k * 4)..]) : BinaryPrimitives.ReadInt32LittleEndian(data[(k * 4)..]);
                return r;
            }
            case "L":
            case "ll":
            {
                var r = new long[n];
                for (int k = 0; k < n; k++) r[k] = big ? BinaryPrimitives.ReadInt64BigEndian(data[(k * 8)..]) : BinaryPrimitives.ReadInt64LittleEndian(data[(k * 8)..]);
                return r;
            }
            case "z":
            {
                var r = new float[n];
                for (int k = 0; k < n; k++) r[k] = big ? BinaryPrimitives.ReadSingleBigEndian(data[(k * 4)..]) : BinaryPrimitives.ReadSingleLittleEndian(data[(k * 4)..]);
                return r;
            }
            case "Z":
            {
                var r = new double[n];
                for (int k = 0; k < n; k++) r[k] = big ? BinaryPrimitives.ReadDoubleBigEndian(data[(k * 8)..]) : BinaryPrimitives.ReadDoubleLittleEndian(data[(k * 8)..]);
                return r;
            }
            default:
                return data[..n].ToArray();
        }
    }
}

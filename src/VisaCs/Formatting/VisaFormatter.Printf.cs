using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using System.Text;
using VisaCs.Internal;

namespace VisaCs.Formatting;

// viPrintf: formatting.
public static partial class VisaFormatter
{
    /// <summary>
    /// Formats arguments like viPrintf. Text is encoded with <paramref name="encoding"/> (Latin-1 by default); binary
    /// conversions are written as raw bytes. Arguments beyond those the format uses are ignored.
    /// </summary>
    public static byte[] Format(string format, IReadOnlyList<object?> args, Encoding? encoding = null) =>
        Format(format, args, encoding, out _);

    /// <summary>Formats like <see cref="Format(string, IReadOnlyList{object?}, Encoding?)"/> and reports how many arguments the format used.</summary>
    internal static byte[] Format(string format, IReadOnlyList<object?> args, Encoding? encoding, out int argumentsUsed)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(args);
        encoding ??= Encoding.Latin1;
        var output = new MemoryStream();
        var text = new StringBuilder();
        int argIndex = 0;

        object? NextArg()
        {
            if (argIndex >= args.Count) throw Invalid("Not enough arguments for the format string.");
            return args[argIndex++];
        }

        void FlushText()
        {
            if (text.Length == 0) return;
            output.Write(encoding.GetBytes(text.ToString()));
            text.Clear();
        }

        for (int i = 0; i < format.Length;)
        {
            char c = format[i++];
            if (c != '%')
            {
                text.Append(c);
                continue;
            }
            if (i < format.Length && format[i] == '%')
            {
                text.Append('%');
                i++;
                continue;
            }
            var spec = ParseSpec(format, ref i, scan: false);
            try
            {
                if (spec.WidthFromArg) spec.Width = ToInt(NextArg());
                if (spec.PrecisionFromArg) spec.Precision = ToInt(NextArg());
                int count = spec.Array == ArrayMode.FromArgument ? ToInt(NextArg()) : spec.ArrayCount;
                if (spec.Width < 0)
                {
                    spec.LeftAlign = true;
                    spec.Width = -spec.Width;
                }

                object? arg = NextArg();
                if (spec.IsBinary)
                {
                    FlushText();
                    var values = Elements(arg);
                    if (spec.Array is ArrayMode.Fixed or ArrayMode.FromArgument) values = values.Take(count).ToList();
                    var data = EncodeBinary(values, spec);
                    if (spec.Conversion != 'y') output.Write(Ieee4882.BuildBlockHeader(data.Length));
                    output.Write(data);
                    continue;
                }

                if (spec.Array != ArrayMode.None || (arg is IEnumerable and not string))
                {
                    var values = Elements(arg);
                    if (spec.Array is ArrayMode.Fixed or ArrayMode.FromArgument) values = values.Take(count).ToList();
                    text.Append(string.Join(",", values.Select(v => FormatOne(spec, v))));
                }
                else
                {
                    text.Append(FormatOne(spec, arg));
                }
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
            {
                throw Invalid($"Argument {argIndex} cannot be formatted with '%{spec.Conversion}': {ex.Message}", ex);
            }
        }
        FlushText();
        argumentsUsed = argIndex;
        return output.ToArray();
    }

    /// <summary>Formats to a string (viSPrintf), with Latin-1 for the bytes of binary conversions.</summary>
    public static string FormatString(string format, params object?[] args) =>
        Encoding.Latin1.GetString(Format(format, args));

    private static int ToInt(object? value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private static List<object?> Elements(object? arg) => arg switch
    {
        null => [],
        string s => [s],
        IEnumerable e => e.Cast<object?>().ToList(),
        _ => [arg],
    };

    private static string FormatOne(Spec spec, object? value)
    {
        char conversion = spec.Conversion;
        if (spec.NumberFormat == 1) conversion = 'd';
        else if (spec.NumberFormat == 2) conversion = 'f';
        else if (spec.NumberFormat == 3) conversion = 'E';

        string body;
        string sign = "";
        string prefix = "";
        switch (conversion)
        {
            case 'd':
            case 'i':
            {
                long v = value is double or float or decimal
                    ? (long)Math.Round(Convert.ToDouble(value, CultureInfo.InvariantCulture), MidpointRounding.AwayFromZero)
                    : Convert.ToInt64(value is char ch ? (int)ch : value, CultureInfo.InvariantCulture);
                body = Math.Abs((decimal)v).ToString(CultureInfo.InvariantCulture);
                if (spec.Precision is { } p && body.Length < p) body = body.PadLeft(p, '0');
                sign = v < 0 ? "-" : spec.Plus ? "+" : spec.Space ? " " : "";
                break;
            }
            case 'u':
            case 'x':
            case 'X':
            case 'o':
            {
                ulong v = ToUInt64(value, spec.Size);
                body = conversion switch
                {
                    'x' => v.ToString("x", CultureInfo.InvariantCulture),
                    'X' => v.ToString("X", CultureInfo.InvariantCulture),
                    'o' => Convert.ToString((long)v, 8),
                    _ => v.ToString(CultureInfo.InvariantCulture),
                };
                if (spec.Precision is { } p && body.Length < p) body = body.PadLeft(p, '0');
                if (spec.Alternate && v != 0)
                    prefix = conversion switch { 'x' => "0x", 'X' => "0X", 'o' => body.StartsWith('0') ? "" : "0", _ => "" };
                break;
            }
            case 'f':
            case 'e':
            case 'E':
            case 'g':
            case 'G':
            {
                double v = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                bool negative = v < 0 || (v == 0 && double.IsNegative(v));
                body = FormatFloat(Math.Abs(v), conversion, spec.Precision ?? 6, spec.Alternate);
                sign = negative ? "-" : spec.Plus ? "+" : spec.Space ? " " : "";
                break;
            }
            case 's':
            {
                string s = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                if (spec.Precision is { } p && s.Length > p) s = s[..p];
                return Pad(s, spec, allowZero: false);
            }
            case 'c':
            {
                string s = value switch
                {
                    char ch => ch.ToString(),
                    string str => str.Length > 0 ? str[..1] : "",
                    _ => ((char)Convert.ToInt32(value, CultureInfo.InvariantCulture)).ToString(),
                };
                return Pad(s, spec, allowZero: false);
            }
            default:
                throw new VisaException(VisaStatus.ErrorFormatNotSupported, $"'%{conversion}' cannot format a value.");
        }

        string number = prefix + body;
        int width = spec.Width ?? 0;
        if (spec.ZeroPad && !spec.LeftAlign && sign.Length + number.Length < width && !(spec.Precision is not null && conversion is 'd' or 'i' or 'u' or 'x' or 'X' or 'o'))
            return sign + prefix + body.PadLeft(width - sign.Length - prefix.Length, '0');
        return Pad(sign + number, spec, allowZero: false);
    }

    private static ulong ToUInt64(object? value, string size)
    {
        long v = value is double or float or decimal
            ? (long)Math.Round(Convert.ToDouble(value, CultureInfo.InvariantCulture), MidpointRounding.AwayFromZero)
            : value is ulong u ? unchecked((long)u) : Convert.ToInt64(value is char c ? (int)c : value, CultureInfo.InvariantCulture);
        return size switch
        {
            "h" => (ushort)v,
            "L" or "ll" => unchecked((ulong)v),
            _ => v < 0 ? (uint)v : (ulong)v,
        };
    }

    private static string Pad(string s, Spec spec, bool allowZero)
    {
        int width = spec.Width ?? 0;
        if (s.Length >= width) return s;
        return spec.LeftAlign ? s.PadRight(width) : s.PadLeft(width, allowZero && spec.ZeroPad ? '0' : ' ');
    }

    /// <summary>C-style floating point formatting of a non-negative value.</summary>
    internal static string FormatFloat(double v, char conversion, int precision, bool alternate)
    {
        if (double.IsNaN(v)) return char.IsUpper(conversion) ? "NAN" : "nan";
        if (double.IsInfinity(v)) return char.IsUpper(conversion) ? "INF" : "inf";
        switch (conversion)
        {
            case 'f':
            {
                string s = v.ToString("F" + precision, CultureInfo.InvariantCulture);
                return alternate && precision == 0 ? s + "." : s;
            }
            case 'e':
            case 'E':
                return Exponential(v, precision, conversion, alternate);
            default: // g / G
            {
                int p = precision == 0 ? 1 : precision;
                if (v == 0) return alternate ? "0." + new string('0', p - 1) : "0";
                // Rounding can bump the exponent (e.g. 9.9999995 -> 10.00000), so take it from the rounded form.
                string probe = Exponential(v, p - 1, 'e', false);
                int exponent = int.Parse(probe[(probe.IndexOf('e') + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                string s = exponent < -4 || exponent >= p
                    ? Exponential(v, p - 1, conversion == 'G' ? 'E' : 'e', alternate)
                    : v.ToString("F" + Math.Max(0, p - 1 - exponent), CultureInfo.InvariantCulture);
                if (!alternate)
                {
                    int e = s.IndexOfAny(['e', 'E']);
                    string mantissa = e < 0 ? s : s[..e];
                    string exp = e < 0 ? "" : s[e..];
                    if (mantissa.Contains('.')) mantissa = mantissa.TrimEnd('0').TrimEnd('.');
                    s = mantissa + exp;
                }
                return s;
            }
        }
    }

    private static string Exponential(double v, int precision, char letter, bool alternate)
    {
        string pattern = (precision > 0 ? "0." + new string('0', precision) : alternate ? "0." : "0") + (letter == 'E' ? "E+00" : "e+00");
        return v.ToString(pattern, CultureInfo.InvariantCulture);
    }

    private static byte[] EncodeBinary(List<object?> values, Spec spec)
    {
        int size = ElementSize(spec.Size);
        var data = new byte[values.Count * size];
        bool bigEndian = !spec.LittleEndian;
        for (int k = 0; k < values.Count; k++)
        {
            var slot = data.AsSpan(k * size, size);
            object? v = values[k];
            switch (spec.Size)
            {
                case "":
                    slot[0] = unchecked((byte)Convert.ToInt64(v, CultureInfo.InvariantCulture));
                    break;
                case "h":
                    if (bigEndian) BinaryPrimitives.WriteInt16BigEndian(slot, unchecked((short)Convert.ToInt64(v, CultureInfo.InvariantCulture)));
                    else BinaryPrimitives.WriteInt16LittleEndian(slot, unchecked((short)Convert.ToInt64(v, CultureInfo.InvariantCulture)));
                    break;
                case "l":
                    if (bigEndian) BinaryPrimitives.WriteInt32BigEndian(slot, unchecked((int)Convert.ToInt64(v, CultureInfo.InvariantCulture)));
                    else BinaryPrimitives.WriteInt32LittleEndian(slot, unchecked((int)Convert.ToInt64(v, CultureInfo.InvariantCulture)));
                    break;
                case "L":
                case "ll":
                    if (bigEndian) BinaryPrimitives.WriteInt64BigEndian(slot, Convert.ToInt64(v, CultureInfo.InvariantCulture));
                    else BinaryPrimitives.WriteInt64LittleEndian(slot, Convert.ToInt64(v, CultureInfo.InvariantCulture));
                    break;
                case "z":
                    if (bigEndian) BinaryPrimitives.WriteSingleBigEndian(slot, Convert.ToSingle(v, CultureInfo.InvariantCulture));
                    else BinaryPrimitives.WriteSingleLittleEndian(slot, Convert.ToSingle(v, CultureInfo.InvariantCulture));
                    break;
                case "Z":
                    if (bigEndian) BinaryPrimitives.WriteDoubleBigEndian(slot, Convert.ToDouble(v, CultureInfo.InvariantCulture));
                    else BinaryPrimitives.WriteDoubleLittleEndian(slot, Convert.ToDouble(v, CultureInfo.InvariantCulture));
                    break;
            }
        }
        return data;
    }
}

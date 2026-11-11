using System.Globalization;
using System.Text;

namespace VisaCs.Formatting;

/// <summary>
/// The VISA formatted I/O language (viPrintf / viScanf), usable on its own or through
/// <c>MessageBasedSession.Printf</c> and <c>MessageBasedSession.Scanf</c> in the <c>VisaCs</c> package.
/// </summary>
/// <remarks>
/// <para>
/// Specifier syntax: <c>%[flags][width][.precision][modifiers]conversion</c>.
/// Flags: <c>- + space # 0</c> (and <c>*</c> to suppress assignment in Scanf). Width and precision may be <c>*</c>
/// (taken from the arguments). Modifiers: <c>,N</c> / <c>,*</c> / <c>,#</c> array size (comma-separated lists, or
/// element count for binary); <c>@1 @2 @3</c> IEEE 488.2 NR1/NR2/NR3 numbers; <c>h l L ll</c> integer sizes
/// (16/32/64/64 bits); <c>z Z</c> 32/64-bit floating point binary data; <c>!ob !ol</c> big/little-endian binary data
/// (big-endian by default).
/// </para>
/// <para>
/// Conversions: <c>d i u o x X f e E g G s c %</c>, <c>b</c> (IEEE 488.2 definite-length block; <c>B</c> is accepted
/// as a synonym), <c>y</c> (raw binary), and for Scanf also <c>[set]</c>, <c>t</c> (rest of the message) and
/// <c>T</c> (up to a line feed). Numbers are formatted and parsed with the invariant culture.
/// </para>
/// <para>
/// Scanf returns signed integers as <see cref="int"/> (<see cref="short"/> with <c>h</c>, <see cref="long"/> with
/// <c>L</c>/<c>ll</c>), unsigned conversions (<c>u o x X</c>) as <see cref="uint"/>/<see cref="ushort"/>/<see cref="ulong"/>,
/// floating values as <see cref="double"/>, <c>%c</c> as a <see cref="char"/> (a <see cref="string"/> with a width), strings
/// as <see cref="string"/>, and arrays and blocks as typed arrays (<see cref="byte"/>[] by default). Integer conversions also
/// accept NR2/NR3 input such as <c>1.000E+00</c>, rounded to the nearest integer.
/// </para>
/// <para>
/// Errors in a format or its arguments throw <see cref="VisaException"/> with VI_ERROR_INV_FMT (or VI_ERROR_NSUP_FMT
/// for unknown conversions). Input that stops matching does not throw: Scanf returns the values converted up to that
/// point.
/// </para>
/// </remarks>
public static partial class VisaFormatter
{
    private enum ArrayMode { None, Fixed, FromArgument, Any }

    private sealed class Spec
    {
        public char Conversion;
        public bool LeftAlign, Plus, Space, Alternate, ZeroPad, Suppress;
        public int? Width, Precision;
        public bool WidthFromArg, PrecisionFromArg;
        public ArrayMode Array;
        public int ArrayCount;
        public int NumberFormat; // 0, or 1/2/3 for @1/@2/@3
        public string Size = ""; // "", "h", "l", "L", "ll", "z", "Z"
        public bool LittleEndian;
        public string? CharSet;
        public bool NegatedSet;

        public bool IsBinary => Conversion is 'b' or 'B' or 'y';
    }

    /// <summary>
    /// True if <paramref name="format"/> contains a binary conversion (<c>%b</c>, <c>%B</c>, <c>%y</c>), so that a read
    /// for it must not stop at a termination character inside the binary data. False for invalid formats.
    /// </summary>
    public static bool HasBinaryConversion(string format)
    {
        ArgumentNullException.ThrowIfNull(format);
        try
        {
            for (int i = 0; i < format.Length;)
            {
                if (format[i++] != '%') continue;
                if (i < format.Length && format[i] == '%')
                {
                    i++;
                    continue;
                }
                if (ParseSpec(format, ref i, scan: true).IsBinary) return true;
            }
            return false;
        }
        catch (VisaException)
        {
            return false;
        }
    }

    private static Spec ParseSpec(string format, ref int i, bool scan)
    {
        // format[i] is the character after '%'.
        var spec = new Spec();
        while (i < format.Length)
        {
            switch (format[i])
            {
                case '-': spec.LeftAlign = true; i++; continue;
                case '+': spec.Plus = true; i++; continue;
                case ' ': spec.Space = true; i++; continue;
                case '#': spec.Alternate = true; i++; continue;
                case '0': spec.ZeroPad = true; i++; continue;
            }
            break;
        }
        if (scan && i < format.Length && format[i] == '*')
        {
            // In scanf, '*' suppresses assignment; in printf it takes the width from the arguments.
            spec.Suppress = true;
            i++;
        }
        // Width, precision and VISA modifiers, accepted in any order before the conversion character.
        while (i < format.Length)
        {
            char c = format[i];
            if (char.IsDigit(c) && spec.Width is null && !spec.WidthFromArg)
            {
                spec.Width = ReadNumber(format, ref i);
            }
            else if (c == '*' && !scan && spec.Width is null && !spec.WidthFromArg)
            {
                spec.WidthFromArg = true;
                i++;
            }
            else if (c == '.')
            {
                i++;
                if (i < format.Length && format[i] == '*')
                {
                    spec.PrecisionFromArg = true;
                    i++;
                }
                else
                {
                    spec.Precision = ReadNumber(format, ref i) ?? 0;
                }
            }
            else if (c == ',')
            {
                i++;
                if (i < format.Length && format[i] == '*') { spec.Array = ArrayMode.FromArgument; i++; }
                else if (i < format.Length && format[i] == '#') { spec.Array = ArrayMode.Any; i++; }
                else
                {
                    spec.Array = ArrayMode.Fixed;
                    spec.ArrayCount = ReadNumber(format, ref i) ?? throw Invalid("',' must be followed by a count, '*' or '#'.");
                }
            }
            else if (c == '@' && i + 1 < format.Length && format[i + 1] is '1' or '2' or '3')
            {
                spec.NumberFormat = format[i + 1] - '0';
                i += 2;
            }
            else if (c == '!' && i + 2 < format.Length && format[i + 1] == 'o' && format[i + 2] is 'b' or 'l')
            {
                spec.LittleEndian = format[i + 2] == 'l';
                i += 3;
            }
            else if (c == 'l' && i + 1 < format.Length && format[i + 1] == 'l')
            {
                spec.Size = "ll";
                i += 2;
            }
            else if (c is 'h' or 'l' or 'L' or 'z' or 'Z')
            {
                spec.Size = c.ToString();
                i++;
            }
            else
            {
                break;
            }
        }

        if (i >= format.Length) throw Invalid("Incomplete format specifier.");
        spec.Conversion = format[i++];
        if (spec.Conversion == '[')
        {
            if (i < format.Length && format[i] == '^')
            {
                spec.NegatedSet = true;
                i++;
            }
            int start = i;
            if (i < format.Length && format[i] == ']') i++; // ']' first is literal
            while (i < format.Length && format[i] != ']') i++;
            if (i >= format.Length) throw Invalid("Unterminated %[ set.");
            spec.CharSet = ExpandSet(format[start..i]);
            i++;
        }
        else if ("diuoxXfeEgGscbyBtT%".IndexOf(spec.Conversion) < 0)
        {
            throw FormatErrors.NotSupported($"Unsupported format conversion '%{spec.Conversion}'.");
        }
        return spec;
    }

    private static string ExpandSet(string set)
    {
        var sb = new StringBuilder();
        for (int k = 0; k < set.Length; k++)
        {
            if (k + 2 < set.Length && set[k + 1] == '-')
            {
                for (char c = set[k]; c <= set[k + 2]; c++) sb.Append(c);
                k += 2;
            }
            else
            {
                sb.Append(set[k]);
            }
        }
        return sb.ToString();
    }

    private static int? ReadNumber(string s, ref int i)
    {
        int start = i;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        if (i == start) return null;
        return int.TryParse(s.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw Invalid($"Number too large in format: '{s[start..i]}'.");
    }

    private static VisaException Invalid(string message, Exception? inner = null) => FormatErrors.Invalid(message, inner);

    private static int ElementSize(string size) => size switch
    {
        "h" => 2,
        "l" or "z" => 4,
        "L" or "ll" or "Z" => 8,
        _ => 1,
    };
}

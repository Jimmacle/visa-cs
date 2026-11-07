using System.Globalization;

namespace VisaCs.Internal;

/// <summary>
/// Converts attribute values and formatted-I/O results to the type a caller asks for: numbers between numeric
/// types and enums, enums from their names, and a one-character string or a char to a byte. AOT-compatible.
/// </summary>
internal static class ValueConverter
{
    /// <summary>Converts <paramref name="value"/>; throws <see cref="InvalidCastException"/>, <see cref="FormatException"/> or <see cref="ArgumentException"/> if it cannot.</summary>
    public static T Convert<T>(object? value)
    {
        if (value is T t) return t;
        if (value is null)
        {
            if (typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) is null)
                throw new InvalidCastException($"null cannot be converted to {typeof(T).Name}.");
            return default!;
        }
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (target == typeof(object)) return (T)value;
        if (target.IsEnum)
        {
            if (value is string s) return (T)Enum.Parse(target, s, ignoreCase: true);
            var underlying = System.Convert.ChangeType(value is Enum e ? System.Convert.ToInt64(e, CultureInfo.InvariantCulture) : value,
                Enum.GetUnderlyingType(target), CultureInfo.InvariantCulture);
            return (T)Enum.ToObject(target, underlying!);
        }
        if (value is Enum en) value = System.Convert.ToInt64(en, CultureInfo.InvariantCulture);
        if (target == typeof(byte) && value is char c) return (T)(object)(byte)c;
        if (target == typeof(byte) && value is string str && str.Length == 1) return (T)(object)(byte)str[0];
        return (T)System.Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }
}

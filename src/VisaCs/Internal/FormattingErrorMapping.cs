namespace VisaCs.Internal;

/// <summary>
/// Translates the exceptions of VisaCs.Formatting (<see cref="FormatException"/> for invalid formats and data,
/// <see cref="NotSupportedException"/> for unsupported conversions) into VISA errors. Use it only around calls into
/// that library, so that the same exception types from elsewhere are not reinterpreted.
/// </summary>
internal static class FormattingErrorMapping
{
    /// <summary>True for the exceptions the formatting library throws for bad formats or data.</summary>
    public static bool IsFormattingError(Exception ex) => ex is FormatException or NotSupportedException;

    /// <summary>The VISA exception for a formatting error (see <see cref="IsFormattingError"/>), keeping it as the inner exception.</summary>
    public static VisaException ToVisa(Exception ex) => new(
        ex is NotSupportedException ? VisaStatus.ErrorFormatNotSupported : VisaStatus.ErrorInvalidFormat, ex.Message, ex);

    /// <summary>Runs a call into the formatting library, translating its errors.</summary>
    public static T Call<TState, T>(TState state, Func<TState, T> call)
    {
        try
        {
            return call(state);
        }
        catch (Exception ex) when (IsFormattingError(ex))
        {
            throw ToVisa(ex);
        }
    }
}

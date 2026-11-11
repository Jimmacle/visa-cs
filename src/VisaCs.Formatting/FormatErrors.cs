namespace VisaCs.Formatting;

/// <summary>The exceptions this library throws for bad data or formats, built in one place.</summary>
internal static class FormatErrors
{
    /// <summary>Malformed data, format or arguments.</summary>
    public static FormatException Invalid(string message, Exception? inner = null) => new OwnFormatException(message, inner);

    /// <summary>An unknown or unsupported format conversion.</summary>
    public static NotSupportedException NotSupported(string message) => new(message);
}

/// <summary>
/// The <see cref="FormatException"/> this library throws itself. Callers see a <see cref="FormatException"/>; the type
/// only lets the handler that wraps BCL parse errors of an argument in <c>VisaFormatter.Format</c> leave it as it is.
/// </summary>
internal sealed class OwnFormatException(string message, Exception? inner) : FormatException(message, inner);

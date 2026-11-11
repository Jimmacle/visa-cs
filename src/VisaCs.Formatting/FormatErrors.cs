namespace VisaCs.Formatting;

/// <summary>The exceptions this library throws for bad data or formats, built in one place.</summary>
internal static class FormatErrors
{
    /// <summary>Malformed data, format or arguments (VI_ERROR_INV_FMT).</summary>
    public static VisaException Invalid(string message, Exception? inner = null) => new(VisaStatus.ErrorInvalidFormat, message, inner);

    /// <summary>An unknown format conversion (VI_ERROR_NSUP_FMT).</summary>
    public static VisaException NotSupported(string message) => new(VisaStatus.ErrorFormatNotSupported, message);
}

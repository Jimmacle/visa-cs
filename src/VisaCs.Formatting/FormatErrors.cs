namespace VisaCs.Formatting;

/// <summary>The exceptions this library throws for bad data or formats, built in one place.</summary>
internal static class FormatErrors
{
    // Marks the exceptions built here, so that a handler for BCL parse errors does not wrap them a second time.
    private const string OwnKey = "VisaCs.Formatting";

    /// <summary>Malformed data, format or arguments.</summary>
    public static FormatException Invalid(string message, Exception? inner = null)
    {
        var ex = new FormatException(message, inner);
        ex.Data[OwnKey] = true;
        return ex;
    }

    /// <summary>An unknown or unsupported format conversion.</summary>
    public static NotSupportedException NotSupported(string message) => new(message);

    /// <summary>True if <paramref name="ex"/> was built by <see cref="Invalid"/>.</summary>
    public static bool IsOwn(Exception ex) => ex.Data.Contains(OwnKey);
}

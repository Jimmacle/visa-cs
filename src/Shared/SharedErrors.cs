namespace VisaCs.Internal;

/// <summary>
/// The exceptions the files in src/Shared create and recognize. Those files are compiled into several libraries,
/// each with its own exception types, so they never name an exception type of their own: each library that links
/// them implements these methods in its own <c>Internal/SharedErrors.Implementation.cs</c>.
/// </summary>
internal static partial class SharedErrors
{
    /// <summary>
    /// The operation did not finish in time, or the caller cancelled it; <paramref name="bytesTransferred"/> bytes
    /// were transferred before that.
    /// </summary>
    public static partial Exception Timeout(string? message, Exception? innerException, int bytesTransferred);

    /// <summary>The connection is closed or broken.</summary>
    public static partial Exception ConnectionLost(string message, Exception? innerException);

    /// <summary>The host cannot be resolved or reached.</summary>
    public static partial Exception NotFound(string message, Exception? innerException);

    /// <summary>An OS call failed unexpectedly with <paramref name="errno"/>.</summary>
    public static partial Exception SystemError(string message, int errno);

    /// <summary>True when <paramref name="ex"/> was created by <see cref="Timeout"/> (or is this library's timeout).</summary>
    public static partial bool IsTimeout(Exception ex);
}

namespace VisaCs;

/// <summary>
/// Thrown by all VisaCs libraries (the VISA sessions and the standalone Serial, USB, GPIB and LAN libraries) when an I/O
/// operation fails. <see cref="Status"/> classifies the failure with a VISA status code, and the message starts with
/// that code's visa.h name.
/// </summary>
/// <remarks>
/// Invalid arguments (<see cref="ArgumentException"/>), use after disposal (<see cref="ObjectDisposedException"/>) and
/// cancellation by the caller's token (<see cref="OperationCanceledException"/>) are reported with the standard .NET
/// exceptions instead.
/// </remarks>
public class VisaException : Exception
{
    /// <summary>Creates an exception for a VISA status code.</summary>
    public VisaException(VisaStatus status, string? message = null, Exception? innerException = null)
        : base(FormatMessage(status, message), innerException)
    {
        Status = status;
    }

    /// <summary>The VISA status code (for example <see cref="VisaStatus.ErrorTimeout"/>).</summary>
    public VisaStatus Status { get; }

    /// <summary>The visa.h name of the status code, e.g. <c>VI_ERROR_TMO</c>.</summary>
    public string StatusName => VisaStatusInfo.GetName(Status);

    /// <summary>
    /// The driver's or operating system's own error code when known, for example the NI-488.2 <c>iberr</c> value of a
    /// failed GPIB call; null otherwise.
    /// </summary>
    public int? NativeError { get; init; }

    private static string FormatMessage(VisaStatus status, string? message)
    {
        var name = VisaStatusInfo.GetName(status);
        return string.IsNullOrEmpty(message)
            ? $"{name}: {VisaStatusInfo.GetDescription(status)}"
            : $"{name}: {message}";
    }
}

/// <summary>
/// Thrown when an operation does not complete within its timeout (VI_ERROR_TMO): the session's timeout for VISA
/// sessions, or the <c>timeoutMilliseconds</c> argument of the standalone libraries.
/// </summary>
/// <remarks>
/// When the caller's cancellation token ends an asynchronous operation, the operation throws
/// <see cref="OperationCanceledException"/> instead; its <see cref="Exception.InnerException"/> may be a
/// <see cref="VisaTimeoutException"/> that carries <see cref="BytesTransferred"/>.
/// </remarks>
public class VisaTimeoutException : VisaException
{
    /// <summary>Creates a timeout exception (VI_ERROR_TMO).</summary>
    public VisaTimeoutException(string? message = null, Exception? innerException = null)
        : base(VisaStatus.ErrorTimeout, message, innerException)
    {
    }

    /// <summary>Number of bytes transferred before the timeout; 0 when none were or the count is unknown.</summary>
    public int BytesTransferred { get; init; }
}

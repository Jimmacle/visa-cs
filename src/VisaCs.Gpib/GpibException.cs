namespace VisaCs.Gpib;

/// <summary>
/// What failed in a <see cref="GpibException"/>. Most values are NI-488.2 <c>iberr</c> codes (the mnemonic is given
/// for each); <see cref="GpibException.NativeError"/> then holds the raw <c>iberr</c> value.
/// </summary>
public enum GpibError
{
    /// <summary>An <c>iberr</c> code not listed here (see <see cref="GpibException.NativeError"/>).</summary>
    Unknown,

    /// <summary>EDVR: a system error in the driver (for example the board's device driver is missing or failed).</summary>
    SystemError,

    /// <summary>ECIC: the board is not controller in charge.</summary>
    NotControllerInCharge,

    /// <summary>ENOL: no device listens (nothing at the address, or the device is off).</summary>
    NoListeners,

    /// <summary>EADR: the board is not addressed correctly.</summary>
    NotAddressed,

    /// <summary>EARG: the driver rejected an argument.</summary>
    InvalidArgument,

    /// <summary>ESAC: the board is not system controller.</summary>
    NotSystemController,

    /// <summary>ENEB: no such GPIB board.</summary>
    NoBoard,

    /// <summary>EDMA: a DMA error.</summary>
    DmaError,

    /// <summary>
    /// EOIP: an asynchronous transfer is in progress on the descriptor. Also thrown, without a native error, when a
    /// second asynchronous transfer is started on a descriptor.
    /// </summary>
    AsyncInProgress,

    /// <summary>ECAP: the driver or board does not support the operation.</summary>
    NotSupported,

    /// <summary>EFSO: a file system error.</summary>
    FileSystemError,

    /// <summary>EBUS: a bus error while sending command bytes (no device accepted them).</summary>
    BusError,

    /// <summary>ESTB: the queue of serial poll status bytes overflowed (status bytes were lost).</summary>
    StatusByteOverflow,

    /// <summary>ESRQ: SRQ is stuck asserted (no device on the bus requests service, or one keeps requesting).</summary>
    SrqStuck,

    /// <summary>ETAB: a problem with a table argument (for example the address list of a 488.2 call).</summary>
    TableProblem,

    /// <summary>ELCK: the interface is locked by another process.</summary>
    Locked,

    /// <summary>EARM: ibnotify could not re-arm the callback.</summary>
    NotifyRearmFailed,

    /// <summary>EHDL: the descriptor is invalid.</summary>
    InvalidDescriptor,

    /// <summary>EWIP: a wait is already in progress on the descriptor.</summary>
    WaitInProgress,

    /// <summary>ERST: the event was reset.</summary>
    EventReset,

    /// <summary>EPWR: the interface lost power.</summary>
    PowerLost,

    /// <summary>No NI-488.2 compatible library could be loaded (not an <c>iberr</c> code).</summary>
    LibraryNotFound,

    /// <summary>
    /// The driver refused to open the device descriptor (ibdev failed); <see cref="GpibException.NativeError"/> holds
    /// the <c>iberr</c> it reported.
    /// </summary>
    DeviceOpenFailed,
}

/// <summary>A GPIB driver or bus failure.</summary>
/// <remarks>
/// Invalid arguments throw <see cref="ArgumentException"/>, use after disposal <see cref="ObjectDisposedException"/>,
/// timeouts <see cref="GpibTimeoutException"/> and cancellation by the caller's token
/// <see cref="OperationCanceledException"/>.
/// </remarks>
public class GpibException : IOException
{
    /// <summary>Creates an exception for <paramref name="error"/>.</summary>
    public GpibException(GpibError error, string? message = null, Exception? innerException = null)
        : base(message ?? $"GPIB error: {error}.", innerException)
    {
        Error = error;
    }

    /// <summary>What failed.</summary>
    public GpibError Error { get; }

    /// <summary>The NI-488.2 <c>iberr</c> value of the failed call when known; null otherwise.</summary>
    public int? NativeError { get; init; }
}

/// <summary>
/// A GPIB operation did not complete within its timeout (NI-488.2 EABO). When the caller's cancellation token ends
/// an asynchronous operation, it throws <see cref="OperationCanceledException"/> instead, whose
/// <see cref="Exception.InnerException"/> may be a <see cref="GpibTimeoutException"/> with
/// <see cref="BytesTransferred"/>.
/// </summary>
public class GpibTimeoutException : TimeoutException
{
    /// <summary>Creates a timeout exception.</summary>
    public GpibTimeoutException(string? message = null, Exception? innerException = null)
        : base(message ?? "The GPIB operation timed out.", innerException)
    {
    }

    /// <summary>Number of bytes transferred before the timeout; 0 when none were or the count is unknown.</summary>
    public int BytesTransferred { get; init; }

    /// <summary>The NI-488.2 <c>iberr</c> value (EABO, 6) when the driver reported the timeout; null otherwise.</summary>
    public int? NativeError { get; init; }
}

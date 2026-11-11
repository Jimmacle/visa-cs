using VisaCs.Internal;
using static VisaCs.Gpib.Native.Ni488;

namespace VisaCs.Gpib;

/// <summary>Creates the exceptions this library throws for driver and bus failures.</summary>
internal static class GpibErrors
{
    /// <summary>
    /// The exception for a driver call that failed with <paramref name="iberr"/>: <see cref="GpibTimeoutException"/>
    /// for EABO (with the <paramref name="count"/> of bytes transferred), otherwise <see cref="GpibException"/>.
    /// </summary>
    public static Exception FromIberr(int iberr, string operation, int count = 0) =>
        iberr == EABO
            ? new GpibTimeoutException($"GPIB {operation} timed out.") { BytesTransferred = count, NativeError = iberr }
            : new GpibException(ErrorOf(iberr), $"GPIB {operation} failed: {Describe(iberr)}.") { NativeError = iberr };

    /// <summary>The <see cref="GpibError"/> of an iberr code other than EABO.</summary>
    public static GpibError ErrorOf(int iberr) => iberr switch
    {
        EDVR => GpibError.SystemError,
        ECIC => GpibError.NotControllerInCharge,
        ENOL => GpibError.NoListeners,
        EADR => GpibError.NotAddressed,
        EARG => GpibError.InvalidArgument,
        ESAC => GpibError.NotSystemController,
        ENEB => GpibError.NoBoard,
        EDMA => GpibError.DmaError,
        EOIP => GpibError.AsyncInProgress,
        ECAP => GpibError.NotSupported,
        EFSO => GpibError.FileSystemError,
        EBUS => GpibError.BusError,
        ESTB => GpibError.StatusByteOverflow,
        ESRQ => GpibError.SrqStuck,
        ETAB => GpibError.TableProblem,
        ELCK => GpibError.Locked,
        EARM => GpibError.NotifyRearmFailed,
        EHDL => GpibError.InvalidDescriptor,
        EWIP => GpibError.WaitInProgress,
        ERST => GpibError.EventReset,
        EPWR => GpibError.PowerLost,
        _ => GpibError.Unknown,
    };

    public static GpibException LibraryNotFound(string? loadError) =>
        new(GpibError.LibraryNotFound, $"No NI-488.2 compatible GPIB library was loaded ({loadError}).");

    public static GpibException DeviceOpenFailed(int board, int primaryAddress, int iberr) =>
        new(GpibError.DeviceOpenFailed, $"Cannot open GPIB{board}::{primaryAddress}: {Describe(iberr)}.") { NativeError = iberr };

    public static GpibException TransferInProgress() =>
        new(GpibError.AsyncInProgress, "Another asynchronous GPIB transfer is in progress on this descriptor.");

    /// <summary>
    /// The exception an operation ended by the caller's token throws: <see cref="OperationCanceledException"/> whose
    /// inner exception is a timeout with the bytes transferred.
    /// </summary>
    public static OperationCanceledException Canceled(string message, CancellationToken token, int count = 0) =>
        AsyncIO.Canceled(new GpibTimeoutException(message) { BytesTransferred = count }, token);
}

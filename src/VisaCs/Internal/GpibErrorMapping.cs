using VisaCs.Gpib;

namespace VisaCs.Internal;

/// <summary>
/// Translates the exceptions of the GPIB library (<see cref="GpibException"/>, <see cref="GpibTimeoutException"/>)
/// to <see cref="VisaException"/> and <see cref="VisaTimeoutException"/>. The GPIB sessions make every call into an
/// <see cref="IGpibDriver"/> through <see cref="Call{T}(Func{T})"/> or one of its overloads.
/// </summary>
internal static class GpibErrorMapping
{
    /// <summary>The VISA status of a GPIB error.</summary>
    public static VisaStatus ToStatus(GpibError error) => error switch
    {
        GpibError.SystemError => VisaStatus.ErrorSystemError,
        GpibError.NotControllerInCharge => VisaStatus.ErrorNotControllerInCharge,
        GpibError.NoListeners => VisaStatus.ErrorNoListeners,
        GpibError.NotAddressed or GpibError.InvalidArgument => VisaStatus.ErrorInvalidParameter,
        GpibError.NotSystemController => VisaStatus.ErrorNotSystemController,
        GpibError.NoBoard or GpibError.DeviceOpenFailed => VisaStatus.ErrorResourceNotFound,
        GpibError.AsyncInProgress or GpibError.WaitInProgress => VisaStatus.ErrorInProgress,
        GpibError.NotSupported => VisaStatus.ErrorOperationNotSupported,
        GpibError.BusError => VisaStatus.ErrorBusError,
        GpibError.SrqStuck => VisaStatus.ErrorSrqNotOccurred,
        GpibError.Locked => VisaStatus.ErrorResourceLocked,
        GpibError.InvalidDescriptor => VisaStatus.ErrorInvalidObject,
        GpibError.PowerLost => VisaStatus.ErrorConnectionLost,
        GpibError.LibraryNotFound => VisaStatus.ErrorLibraryNotFound,
        _ => VisaStatus.ErrorIO, // Unknown, DmaError, FileSystemError, StatusByteOverflow, TableProblem, NotifyRearmFailed, EventReset
    };

    /// <summary>
    /// The VISA exception for a GPIB exception, or for an <see cref="OperationCanceledException"/> whose inner
    /// exception is one (the inner exception is replaced); null for anything else.
    /// </summary>
    public static Exception? ToVisa(Exception ex) => ex switch
    {
        GpibTimeoutException t => new VisaTimeoutException(t.Message, t) { BytesTransferred = t.BytesTransferred, NativeError = t.NativeError },
        GpibException g => new VisaException(ToStatus(g.Error), g.Message, g) { NativeError = g.NativeError },
        OperationCanceledException { InnerException: { } inner } c when ToVisa(inner) is { } mapped =>
            new OperationCanceledException(c.Message, mapped, c.CancellationToken),
        _ => null,
    };

    /// <summary>Runs a call into the GPIB library, translating its exceptions.</summary>
    public static T Call<T>(Func<T> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (ToVisa(ex) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <summary>Runs a call into the GPIB library, translating its exceptions.</summary>
    public static void Call(Action call)
    {
        try
        {
            call();
        }
        catch (Exception ex) when (ToVisa(ex) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <summary>Runs an asynchronous call into the GPIB library, translating its exceptions.</summary>
    public static async ValueTask<T> CallAsync<T>(Func<ValueTask<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception ex) when (ToVisa(ex) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <summary>Runs an asynchronous call into the GPIB library, translating its exceptions.</summary>
    public static async ValueTask CallAsync(Func<ValueTask> call)
    {
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (Exception ex) when (ToVisa(ex) is { } mapped)
        {
            throw mapped;
        }
    }
}

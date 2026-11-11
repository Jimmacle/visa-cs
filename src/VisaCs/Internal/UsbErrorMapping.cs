using VisaCs.Usb;

namespace VisaCs.Internal;

/// <summary>
/// Translates the exceptions of <c>VisaCs.Usb</c> to <see cref="VisaException"/> and <see cref="VisaTimeoutException"/>
/// with the status codes the USB sessions have always reported. Every call from the USB sessions and discovery into
/// the library goes through <see cref="Run{T}(Func{T})"/> or <see cref="RunAsync{T}(Func{ValueTask{T}})"/> (or their
/// void forms).
/// </summary>
internal static class UsbErrorMapping
{
    /// <summary>The VISA status for a USB library error.</summary>
    public static VisaStatus ToStatus(UsbError error) => error switch
    {
        UsbError.IOError or UsbError.Stall or UsbError.ProtocolError => VisaStatus.ErrorIO,
        UsbError.NotFound => VisaStatus.ErrorResourceNotFound,
        UsbError.Busy => VisaStatus.ErrorResourceBusy,
        UsbError.AccessDenied => VisaStatus.ErrorNoPermission,
        UsbError.Disconnected => VisaStatus.ErrorConnectionLost,
        UsbError.NotSupported => VisaStatus.ErrorOperationNotSupported,
        UsbError.LibraryNotFound => VisaStatus.ErrorLibraryNotFound,
        UsbError.OutOfMemory => VisaStatus.ErrorAllocation,
        UsbError.Aborted => VisaStatus.ErrorAbort,
        UsbError.SystemError => VisaStatus.ErrorSystemError,
        _ => VisaStatus.ErrorSystemError,
    };

    /// <summary>True for the exceptions this mapping translates.</summary>
    public static bool IsUsbError(Exception ex) => ex is UsbException or UsbTimeoutException;

    /// <summary>
    /// The VISA exception for a USB library exception, with the original as <see cref="Exception.InnerException"/>;
    /// any other exception unchanged.
    /// </summary>
    public static Exception ToVisa(Exception ex) => ex switch
    {
        UsbTimeoutException timeout => new VisaTimeoutException(timeout.Message, timeout) { BytesTransferred = timeout.BytesTransferred },
        UsbException usb => new VisaException(ToStatus(usb.Error), usb.Message, usb) { NativeError = usb.NativeError },
        _ => ex,
    };

    public static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (IsUsbError(ex))
        {
            throw ToVisa(ex);
        }
    }

    public static T Run<T>(Func<T> func)
    {
        try
        {
            return func();
        }
        catch (Exception ex) when (IsUsbError(ex))
        {
            throw ToVisa(ex);
        }
    }

    public static async ValueTask RunAsync(Func<ValueTask> func)
    {
        try
        {
            await func().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUsbError(ex))
        {
            throw ToVisa(ex);
        }
    }

    public static async ValueTask<T> RunAsync<T>(Func<ValueTask<T>> func)
    {
        try
        {
            return await func().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUsbError(ex))
        {
            throw ToVisa(ex);
        }
    }
}

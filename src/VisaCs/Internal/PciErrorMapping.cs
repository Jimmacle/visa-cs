using VisaCs.Pci;

namespace VisaCs.Internal;

/// <summary>
/// Translates the exceptions of VisaCs.Pci into VISA errors. <see cref="PxiSession"/> and <see cref="PxiWindow"/>
/// check width, alignment and bounds themselves before calling into the library, so the library's
/// <see cref="ArgumentOutOfRangeException"/>s never reach VISA callers and are not mapped here.
/// </summary>
internal static class PciErrorMapping
{
    /// <summary>True for the library's device and OS failures, and for an access to a region closed meanwhile.</summary>
    public static bool IsPciError(Exception ex) => ex is PciException or ObjectDisposedException;

    /// <summary>The VISA status for a library error.</summary>
    public static VisaStatus ToStatus(PciError error) => error switch
    {
        PciError.AccessDenied => VisaStatus.ErrorNoPermission,
        PciError.NotFound => VisaStatus.ErrorResourceNotFound,
        PciError.MapFailed => VisaStatus.ErrorResourceBusy,
        PciError.BarNotImplemented => VisaStatus.ErrorInvalidSpace,
        PciError.BusError => VisaStatus.ErrorBusError,
        _ => VisaStatus.ErrorIO,
    };

    /// <summary>The VISA exception for a library error (see <see cref="IsPciError"/>), keeping it as the inner exception.</summary>
    public static VisaException ToVisa(Exception ex) => ex switch
    {
        PciException p => new VisaException(ToStatus(p.Error), p.Message, p) { NativeError = p.NativeError },
        ObjectDisposedException => new VisaException(VisaStatus.ErrorInvalidObject, "The PCI region is closed.", ex),
        _ => new VisaException(VisaStatus.ErrorIO, ex.Message, ex),
    };
}

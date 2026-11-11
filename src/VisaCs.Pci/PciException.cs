namespace VisaCs.Pci;

/// <summary>What went wrong in a <see cref="PciException"/>.</summary>
public enum PciError
{
    /// <summary>The operating system denied access to a sysfs file (needs root or a udev rule), or configuration space beyond the first 64 bytes was read without root.</summary>
    AccessDenied,
    /// <summary>The sysfs file of the device or BAR does not exist.</summary>
    NotFound,
    /// <summary>A memory BAR could not be mapped into the process.</summary>
    MapFailed,
    /// <summary>The device does not implement the BAR.</summary>
    BarNotImplemented,
    /// <summary>A read of an I/O BAR returned fewer bytes than requested.</summary>
    BusError,
    /// <summary>Another I/O error while accessing the device.</summary>
    IOError,
}

/// <summary>A device or operating-system failure of PCI register access.</summary>
public class PciException : IOException
{
    /// <summary>Creates an exception for <paramref name="error"/>.</summary>
    public PciException(PciError error, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    /// <summary>What went wrong.</summary>
    public PciError Error { get; }

    /// <summary>The operating system's error code (errno) when known; null otherwise.</summary>
    public int? NativeError { get; init; }
}

using Microsoft.Win32.SafeHandles;

namespace VisaCs.Pci;

/// <summary>Opens sysfs files and builds every error the library throws.</summary>
internal static class PciAccess
{
    public static SafeFileHandle OpenFile(string path, FileAccess access)
    {
        try
        {
            return File.OpenHandle(path, FileMode.Open, access, FileShare.ReadWrite);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new VisaException(VisaStatus.ErrorNoPermission,
                $"Access to {path} was denied. PCI register access needs root or a udev rule granting access.", ex);
        }
        catch (FileNotFoundException ex)
        {
            throw new VisaException(VisaStatus.ErrorResourceNotFound, $"{path} does not exist.", ex);
        }
    }

    /// <summary>The error for an access to a disposed region.</summary>
    public static VisaException Closed() => new(VisaStatus.ErrorInvalidObject, "The PCI region is closed.");

    public static VisaException WidthNotSupported(int width) =>
        new(VisaStatus.ErrorWidthNotSupported, $"{width}-byte accesses are not supported in this region.");

    public static VisaException Misaligned(long offset, int width) =>
        new(VisaStatus.ErrorAlignmentOffsetNotSupported, $"Offset 0x{offset:X} is not aligned to {width} bytes.");

    public static VisaException OutOfRange(long offset, int width, long size) =>
        new(VisaStatus.ErrorInvalidOffset, $"Offset 0x{offset:X} (+{width}) is outside the {size}-byte region.");

    public static VisaException BarNotImplemented(int index) =>
        new(VisaStatus.ErrorInvalidSpace, $"BAR{index} is not implemented by this device.");

    public static VisaException ShortRead(bool isConfig, long offset) => isConfig
        ? new VisaException(VisaStatus.ErrorNoPermission, $"Configuration space beyond the first 64 bytes needs root (offset 0x{offset:X}).")
        : new VisaException(VisaStatus.ErrorBusError, $"Short PCI read at offset 0x{offset:X}.");

    public static VisaException MapError(string path, Exception ex) => ex is UnauthorizedAccessException
        ? new VisaException(VisaStatus.ErrorNoPermission,
            $"Access to {path} was denied. PCI BAR access needs root or a udev rule granting access to the resource files.", ex)
        : new VisaException(VisaStatus.ErrorResourceBusy, $"Cannot map {path}: {ex.Message}", ex);

    public static bool IsAccessError(Exception ex) => ex is ObjectDisposedException or UnauthorizedAccessException or IOException;

    /// <summary>The VISA error for a failed file access (see <see cref="IsAccessError"/>).</summary>
    public static VisaException Translate(Exception ex, string path, long offset) => ex switch
    {
        ObjectDisposedException => Closed(),
        UnauthorizedAccessException => new VisaException(VisaStatus.ErrorNoPermission, $"Access to {path} at offset 0x{offset:X} was denied.", ex),
        _ => new VisaException(VisaStatus.ErrorIO, $"PCI access to {path} at offset 0x{offset:X} failed: {ex.Message}", ex),
    };
}

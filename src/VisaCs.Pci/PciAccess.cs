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
            throw new PciException(PciError.AccessDenied,
                $"Access to {path} was denied. PCI register access needs root or a udev rule granting access.", ex);
        }
        catch (FileNotFoundException ex)
        {
            throw new PciException(PciError.NotFound, $"{path} does not exist.", ex);
        }
    }

    /// <summary>The error for an access to a disposed region.</summary>
    public static ObjectDisposedException Closed(Exception? inner = null) => new("The PCI region is closed.", inner);

    public static ArgumentOutOfRangeException WidthNotSupported(int width, int maxWidth) =>
        new("width", width, $"{width}-byte accesses are not supported in this region (1, 2, 4 or 8 bytes, at most {maxWidth}).");

    public static ArgumentOutOfRangeException Misaligned(long offset, int width) =>
        new("offset", offset, $"Offset 0x{offset:X} is not aligned to {width} bytes.");

    public static ArgumentOutOfRangeException OutOfRange(long offset, int width, long size) =>
        new("offset", offset, $"Offset 0x{offset:X} (+{width}) is outside the {size}-byte region.");

    public static PciException BarNotImplemented(int index) =>
        new(PciError.BarNotImplemented, $"BAR{index} is not implemented by this device.");

    public static PciException ShortRead(bool isConfig, long offset) => isConfig
        ? new PciException(PciError.AccessDenied, $"Configuration space beyond the first 64 bytes needs root (offset 0x{offset:X}).")
        : new PciException(PciError.BusError, $"Short PCI read at offset 0x{offset:X}.");

    public static PciException MapError(string path, Exception ex) => ex is UnauthorizedAccessException
        ? new PciException(PciError.AccessDenied,
            $"Access to {path} was denied. PCI BAR access needs root or a udev rule granting access to the resource files.", ex)
        : new PciException(PciError.MapFailed, $"Cannot map {path}: {ex.Message}", ex);

    public static bool IsAccessError(Exception ex) => ex is ObjectDisposedException or UnauthorizedAccessException or IOException;

    /// <summary>The error for a failed file access (see <see cref="IsAccessError"/>).</summary>
    public static Exception Translate(Exception ex, string path, long offset) => ex switch
    {
        ObjectDisposedException => Closed(ex),
        UnauthorizedAccessException => new PciException(PciError.AccessDenied, $"Access to {path} at offset 0x{offset:X} was denied.", ex),
        _ => new PciException(PciError.IOError, $"PCI access to {path} at offset 0x{offset:X} failed: {ex.Message}", ex),
    };
}

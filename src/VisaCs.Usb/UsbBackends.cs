namespace VisaCs.Usb;

/// <summary>Chooses the built-in USB backend for the current operating system.</summary>
public static class UsbBackends
{
    /// <summary>
    /// Creates the built-in backend for this operating system: <see cref="WinUsbBackend"/> on Windows,
    /// <see cref="UsbFsBackend"/> on Linux when usbfs is available, otherwise <see cref="LibUsbBackend"/> when
    /// libusb-1.0 can be loaded. Returns null when none is available. Each call creates a new backend, which the
    /// caller owns and disposes.
    /// </summary>
    public static IUsbBackend? CreateDefault()
    {
        if (OperatingSystem.IsWindows()) return new WinUsbBackend();
        if (OperatingSystem.IsLinux() && UsbFsBackend.IsAvailable) return new UsbFsBackend();
        if (LibUsbBackend.IsAvailable) return new LibUsbBackend();
        return null;
    }
}

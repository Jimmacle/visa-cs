namespace VisaCs.Usb;

/// <summary>
/// Enumerates and opens USB devices through one host driver stack. Built-in backends: <see cref="WinUsbBackend"/>
/// (Windows), <see cref="UsbFsBackend"/> (Linux) and <see cref="LibUsbBackend"/> (any OS with libusb-1.0);
/// <see cref="UsbBackends.CreateDefault"/> picks the one for the current OS. Implement this interface to plug in
/// another driver stack or a simulated device.
/// </summary>
/// <remarks>
/// <para>
/// Members may be called from any thread. Opening a device claims the interface exclusively: a second
/// <see cref="Open"/> of the same interface, from this process or another one, fails with
/// <see cref="UsbError.Busy"/> until the first handle is disposed.
/// </para>
/// <para>
/// Dispose the handles before the backend. Disposing <see cref="LibUsbBackend"/> closes the handles it opened and
/// releases libusb; the WinUSB and usbfs backends hold nothing of their own, so disposing them leaves their handles
/// open. Using a disposed backend throws <see cref="ObjectDisposedException"/> (<see cref="LibUsbBackend"/>) or
/// keeps working (the others).
/// </para>
/// </remarks>
public interface IUsbBackend : IDisposable
{
    /// <summary>Short name of the backend, such as "WinUSB", "usbfs" or "libusb".</summary>
    string Name { get; }

    /// <summary>
    /// Lists the attached devices this backend can open, each with the interfaces it can open. Devices that vanish
    /// or cannot be read during the scan are skipped rather than reported as errors.
    /// </summary>
    /// <remarks>
    /// What is known before a device is opened differs per backend. <see cref="WinUsbBackend"/> lists only devices
    /// bound to WinUSB and opens each briefly to read its descriptors; for a device that another program has open it
    /// reports what Windows knows (no endpoints, and the Windows device description as <see cref="UsbDeviceInfo.Product"/>).
    /// <see cref="UsbFsBackend"/> reads sysfs and does no I/O to the devices. <see cref="LibUsbBackend"/> opens each
    /// device to read its strings (ASCII only), and leaves them empty when it has no access.
    /// <see cref="IUsbDeviceHandle.Device"/> and <see cref="IUsbDeviceHandle.Interface"/> are complete once a device
    /// is open.
    /// </remarks>
    IReadOnlyList<UsbDeviceInfo> EnumerateDevices();

    /// <summary>Opens a device and claims one of its interfaces for exclusive use.</summary>
    /// <param name="device">A device returned by this backend's <see cref="EnumerateDevices"/>.</param>
    /// <param name="interfaceNumber">The interface to claim (bInterfaceNumber).</param>
    /// <remarks>
    /// On Linux, a kernel driver bound to the interface (usually usbtmc) is detached while the handle is open and
    /// reattached when it is disposed; an interface claimed by another program is never taken over. Throws
    /// <see cref="UsbException"/> with <see cref="UsbError.NotFound"/> (device gone, no such interface, or not bound
    /// to WinUSB), <see cref="UsbError.Busy"/> (claimed by another handle or program) or
    /// <see cref="UsbError.AccessDenied"/> (no access to the device node), and <see cref="ArgumentException"/> when
    /// <paramref name="device"/> was enumerated by another backend.
    /// </remarks>
    IUsbDeviceHandle Open(UsbDeviceInfo device, int interfaceNumber);
}

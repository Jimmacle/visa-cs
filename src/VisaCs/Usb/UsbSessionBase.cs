using VisaCs.Internal;
using VisaCs.Usb;

namespace VisaCs;

/// <summary>
/// Shared base of the USB sessions (<see cref="UsbSession"/> and <see cref="UsbRawSession"/>): device lookup, control
/// transfers and the USB attributes.
/// </summary>
/// <remarks>
/// Sessions to the same USB interface through the same resource manager backend share one operating system handle,
/// and their transfers take turns: a session waits for another one's transfer at most until its own timeout.
/// Opening the session (also <c>OpenAsync</c>) enumerates and opens the device synchronously.
/// </remarks>
public abstract class UsbSessionBase : MessageBasedSession
{
    private protected UsbSessionBase(ResourceManager manager, UsbResourceName name) : base(manager, name, 128 * 1024)
    {
        Name = name;
    }

    private protected UsbResourceName Name { get; }

    private protected SharedUsbDevice? Shared { get; private set; }

    private protected IUsbDeviceHandle Device => Shared?.Handle ?? throw new VisaException(VisaStatus.ErrorInvalidObject, "The session is not open.");

    private protected SharedUsbDevice SharedDevice => Shared ?? throw new VisaException(VisaStatus.ErrorInvalidObject, "The session is not open.");

    /// <summary>Serializes transfers with other sessions to the same device; waits at most until the deadline.</summary>
    private protected ValueTask<IOGate.Releaser> EnterDeviceAsync(Deadline deadline, bool async) => SharedDevice.EnterAsync(deadline, async);

    /// <summary>USB vendor ID (VI_ATTR_MANF_ID).</summary>
    public ushort ManufacturerId => Name.VendorId;
    /// <summary>USB product ID (VI_ATTR_MODEL_CODE).</summary>
    public ushort ModelCode => Name.ProductId;
    /// <summary>USB serial number as given in the resource name (VI_ATTR_USB_SERIAL_NUM).</summary>
    public string SerialNumber => Name.SerialNumber;
    /// <summary>Manufacturer string read from the device; null if it has none (VI_ATTR_MANF_NAME).</summary>
    public string? ManufacturerName => Device.Device.Manufacturer;
    /// <summary>Product string read from the device; null if it has none (VI_ATTR_MODEL_NAME).</summary>
    public string? ModelName => Device.Device.Product;
    /// <summary>The interface in use (VI_ATTR_USB_INTFC_NUM).</summary>
    public int UsbInterfaceNumber => Device.Interface.Number;
    /// <summary>Interface class (VI_ATTR_USB_CLASS).</summary>
    public byte UsbClass => Device.Interface.Class;
    /// <summary>Interface subclass (VI_ATTR_USB_SUBCLASS).</summary>
    public byte UsbSubClass => Device.Interface.SubClass;

    /// <summary>Name of the USB backend in use ("WinUSB", "usbfs", "libusb", ...).</summary>
    public string BackendName { get; private set; } = "";

    private protected int BulkOutEndpoint { get; set; } = -1;
    private protected int BulkInEndpoint { get; set; } = -1;
    private protected int InterruptInEndpoint { get; set; } = -1;
    private protected int BulkInPacketSize { get; set; } = 512;
    private protected int InterruptPacketSize { get; set; } = 2;

    /// <summary>Chooses the interface to open when the resource name does not specify one.</summary>
    private protected abstract bool IsPreferredInterface(UsbInterfaceInfo info);

    private protected override async ValueTask OpenCoreAsync(int connectTimeoutMs, CancellationToken cancellationToken, bool async)
    {
        var backend = ResourceManager.UsbBackend
            ?? throw new VisaException(VisaStatus.ErrorLibraryNotFound, "No USB backend is available on this platform.");
        BackendName = backend.Name;
        var device = backend.EnumerateDevices().FirstOrDefault(d =>
                d.VendorId == Name.VendorId && d.ProductId == Name.ProductId &&
                (string.Equals(d.SerialNumber, Name.SerialNumber, StringComparison.OrdinalIgnoreCase) ||
                 (Name.SerialNumber.Length == 0 && d.SerialNumber.Length == 0)))
            ?? throw new VisaException(VisaStatus.ErrorResourceNotFound,
                $"USB device {Name.VendorId:X4}:{Name.ProductId:X4} with serial number '{Name.SerialNumber}' was not found.");

        UsbInterfaceInfo? info = Name.InterfaceNumber is { } number
            ? device.Interfaces.FirstOrDefault(i => i.Number == number)
            : device.Interfaces.FirstOrDefault(IsPreferredInterface)
              // The backend could not read the endpoints without opening the device: try its first interface.
              ?? (device.Interfaces.All(i => i.Endpoints.Count == 0) ? device.Interfaces.FirstOrDefault() : null);
        int interfaceNumber = info?.Number ?? Name.InterfaceNumber ?? 0;
        if (info is null && device.Interfaces.Count > 0)
            throw new VisaException(VisaStatus.ErrorResourceNotFound, $"USB interface {interfaceNumber} was not found on {device}.");

        Shared = SharedUsbDevice.Acquire(backend, device, interfaceNumber);
        var handle = Shared.Handle;
        Resource = Name with { InterfaceNumber = handle.Interface.Number };

        var bulkOut = handle.Interface.FindEndpoint(UsbTransferType.Bulk, input: false);
        var bulkIn = handle.Interface.FindEndpoint(UsbTransferType.Bulk, input: true);
        var interrupt = handle.Interface.FindEndpoint(UsbTransferType.Interrupt, input: true);
        BulkOutEndpoint = bulkOut?.Address ?? -1;
        BulkInEndpoint = bulkIn?.Address ?? -1;
        InterruptInEndpoint = interrupt?.Address ?? -1;
        BulkInPacketSize = Math.Max(8, (int)(bulkIn?.MaxPacketSize ?? 512));
        InterruptPacketSize = Math.Max(2, (int)(interrupt?.MaxPacketSize ?? 2));
        Shared.InterruptEndpoint = InterruptInEndpoint;
        Shared.InterruptPacketSize = InterruptPacketSize;
        await OnDeviceOpenedAsync(Deadline.FromMilliseconds(connectTimeoutMs).WithCancellation(cancellationToken), async).ConfigureAwait(false);
    }

    private protected virtual ValueTask OnDeviceOpenedAsync(Deadline deadline, bool async) => default;

    private protected override async ValueTask CloseCoreAsync(bool async)
    {
        var shared = Shared;
        Shared = null;
        if (shared is not null) await shared.ReleaseAsync(async).ConfigureAwait(false);
    }

    /// <summary>USB timeout for one transfer: 0 (no limit) when the deadline is infinite; throws when it has expired.</summary>
    private protected static int ToUsbTimeout(Deadline deadline)
    {
        int remaining = deadline.RemainingMilliseconds;
        if (remaining < 0) return 0; // infinite
        if (remaining == 0) throw new VisaTimeoutException();
        return remaining;
    }

    /// <summary>
    /// USB timeout for a class request that must end even when the session timeout is infinite: the remaining time,
    /// 5 seconds when the deadline is infinite, and one last millisecond when it has expired.
    /// </summary>
    private protected static int ToRequestTimeout(Deadline deadline)
    {
        int remaining = deadline.RemainingMilliseconds;
        return remaining < 0 ? 5000 : Math.Max(1, remaining);
    }

    private protected ValueTask<int> BulkAsync(int endpoint, Memory<byte> buffer, int timeoutMs, CancellationToken cancellationToken, bool async) =>
        async
            ? Device.BulkTransferAsync((byte)endpoint, buffer, timeoutMs, cancellationToken)
            : new(Device.BulkTransfer((byte)endpoint, buffer.Span, timeoutMs));

    private protected ValueTask<int> ControlAsync(byte requestType, byte request, ushort value, ushort index, Memory<byte> buffer, int timeoutMs,
        CancellationToken cancellationToken, bool async) =>
        async
            ? Device.ControlTransferAsync(requestType, request, value, index, buffer, timeoutMs, cancellationToken)
            : new(Device.ControlTransfer(requestType, request, value, index, buffer.Span, timeoutMs));

    private protected ValueTask<int> InterruptAsync(int endpoint, Memory<byte> buffer, int timeoutMs, CancellationToken cancellationToken, bool async) =>
        async
            ? Device.InterruptTransferAsync((byte)endpoint, buffer, timeoutMs, cancellationToken)
            : new(Device.InterruptTransfer((byte)endpoint, buffer.Span, timeoutMs));

    private protected static ValueTask DelayAsync(int milliseconds, bool async)
    {
        if (async) return new ValueTask(Task.Delay(milliseconds));
        Thread.Sleep(milliseconds);
        return default;
    }

    /// <summary>
    /// Performs a control IN transfer on the default pipe (viUsbControlIn) and returns the bytes received, at most
    /// <paramref name="length"/> (wLength). Bit 7 of <paramref name="requestType"/> is set for the caller. Bounded by
    /// the session timeout, and waits for transfers of other sessions to the same device.
    /// </summary>
    public byte[] ControlIn(byte requestType, byte request, ushort value, ushort index, ushort length) =>
        AsyncIO.Complete(ControlInAsync(requestType, request, value, index, length, CancellationToken.None, async: false));

    /// <summary>Performs a control IN transfer without blocking; see <see cref="ControlIn"/>.</summary>
    public Task<byte[]> ControlInAsync(byte requestType, byte request, ushort value, ushort index, ushort length,
        CancellationToken cancellationToken = default) =>
        ControlInAsync(requestType, request, value, index, length, cancellationToken, async: true).AsTask();

    private ValueTask<byte[]> ControlInAsync(byte requestType, byte request, ushort value, ushort index, ushort length,
        CancellationToken cancellationToken, bool async) =>
        ExecuteAsync((requestType, request, value, index, length), static async (s, a, ct, async) =>
        {
            var session = (UsbSessionBase)s;
            var deadline = session.NewDeadline(ct);
            var buffer = new byte[a.length];
            int n;
            using (await session.EnterDeviceAsync(deadline, async).ConfigureAwait(false))
                n = await session.ControlAsync((byte)(a.requestType | 0x80), a.request, a.value, a.index, buffer, ToUsbTimeout(deadline),
                    ct, async).ConfigureAwait(false);
            return n == a.length ? buffer : buffer.AsSpan(0, n).ToArray();
        }, cancellationToken, async);

    /// <summary>
    /// Performs a control OUT transfer on the default pipe (viUsbControlOut); wLength is the length of
    /// <paramref name="data"/>. Bit 7 of <paramref name="requestType"/> is cleared for the caller. Bounded by the
    /// session timeout, and waits for transfers of other sessions to the same device.
    /// </summary>
    public void ControlOut(byte requestType, byte request, ushort value, ushort index, ReadOnlySpan<byte> data) =>
        AsyncIO.Complete(ControlOutAsync(requestType, request, value, index, data.ToArray(), CancellationToken.None, async: false));

    /// <summary>Performs a control OUT transfer without blocking; see <see cref="ControlOut"/>.</summary>
    public Task ControlOutAsync(byte requestType, byte request, ushort value, ushort index, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default) =>
        ControlOutAsync(requestType, request, value, index, data, cancellationToken, async: true).AsTask();

    private async ValueTask ControlOutAsync(byte requestType, byte request, ushort value, ushort index, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken, bool async) =>
        await ExecuteAsync((requestType, request, value, index, data), static async (s, a, ct, async) =>
        {
            var session = (UsbSessionBase)s;
            var deadline = session.NewDeadline(ct);
            using (await session.EnterDeviceAsync(deadline, async).ConfigureAwait(false))
                await session.ControlAsync((byte)(a.requestType & 0x7F), a.request, a.value, a.index, a.data.ToArray(),
                    ToUsbTimeout(deadline), ct, async).ConfigureAwait(false);
            return true;
        }, cancellationToken, async).ConfigureAwait(false);

    private protected override void RegisterAttributes(AttributeTable table)
    {
        base.RegisterAttributes(table);
        table.Add(VisaAttribute.ManufacturerId, () => (int)ManufacturerId);
        table.Add(VisaAttribute.ModelCode, () => (int)ModelCode);
        table.Add(VisaAttribute.UsbSerialNumber, () => SerialNumber);
        table.Add(VisaAttribute.ManufacturerName, () => ManufacturerName ?? "");
        table.Add(VisaAttribute.ModelName, () => ModelName ?? "");
        table.Add(VisaAttribute.UsbInterfaceNumber, () => UsbInterfaceNumber);
        table.Add(VisaAttribute.UsbClass, () => (int)UsbClass);
        table.Add(VisaAttribute.UsbSubclass, () => (int)UsbSubClass);
        table.Add(VisaAttribute.UsbProtocol, () => (int)Device.Interface.Protocol);
        table.Add(VisaAttribute.UsbAlternateSetting, () => (int)Device.Interface.AlternateSetting);
        table.Add(VisaAttribute.UsbNumberOfInterfaces, () => Device.Device.Interfaces.Count);
        table.Add(VisaAttribute.UsbNumberOfPipes, () => Device.Interface.Endpoints.Count);
        table.Add(VisaAttribute.UsbBulkOutPipe, () => BulkOutEndpoint);
        table.Add(VisaAttribute.UsbBulkInPipe, () => BulkInEndpoint);
        table.Add(VisaAttribute.UsbInterruptInPipe, () => InterruptInEndpoint);
        table.Add(VisaAttribute.UsbMaxInterruptSize, () => InterruptPacketSize);
        table.Add(VisaAttribute.UsbControlPipe, () => 0);
    }
}

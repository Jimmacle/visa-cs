namespace VisaCs.Usb;

/// <summary>
/// An opened USB device with one claimed interface: control transfers on the default pipe, and bulk and interrupt
/// transfers on the interface's endpoints. Get one from <see cref="IUsbBackend.Open"/> and dispose it to release the
/// interface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transfers.</b> The direction comes from bit 7 of the endpoint address (of the request type for control
/// transfers). Every transfer returns the number of bytes transferred; an IN transfer can return fewer bytes than the
/// buffer holds (a short packet ends it). On the built-in backends the endpoint decides between bulk and interrupt,
/// so <see cref="BulkTransfer"/> and <see cref="InterruptTransfer"/> are interchangeable.
/// </para>
/// <para>
/// <b>Timeouts.</b> <c>timeoutMilliseconds</c> bounds one transfer; 0 and <see cref="Timeout.Infinite"/> (-1) wait
/// without limit. A transfer that times out is cancelled in the operating system, then throws
/// <see cref="VisaTimeoutException"/>. Data that an IN transfer had already received is discarded, and an OUT
/// transfer may have been partly sent, so the protocol on top must resynchronize with the device (for example with
/// the USBTMC abort or clear requests). A transfer that completes despite a late timeout or cancellation returns its
/// result.
/// </para>
/// <para>
/// <b>Asynchronous transfers</b> hold no thread while they are pending. Cancelling the token cancels the transfer in
/// the operating system, like a timeout, and then throws <see cref="OperationCanceledException"/>. With
/// <see cref="LibUsbBackend"/> on macOS, cancelling or timing out a transfer also aborts the other transfers pending
/// on the same endpoint.
/// </para>
/// <para>
/// <b>Errors.</b> <see cref="VisaException"/> with <see cref="VisaStatus.ErrorConnectionLost"/> when the device is
/// unplugged, and <see cref="VisaStatus.ErrorIO"/> when an endpoint stalls (see <see cref="ClearHalt"/>) or a transfer
/// fails otherwise.
/// </para>
/// <para>
/// <b>Thread safety.</b> All members may be called from any thread, concurrently. Synchronous and asynchronous
/// transfers can be mixed, on different endpoints or on the same one; transfers on one endpoint complete in the order
/// they were started.
/// </para>
/// <para>
/// <b>Disposal.</b> Disposing the handle cancels every transfer still in progress, synchronous or asynchronous, and
/// those throw <see cref="ObjectDisposedException"/>; Dispose may wait a few seconds for the operating system to
/// report them. Afterwards every member throws <see cref="ObjectDisposedException"/>.
/// </para>
/// </remarks>
public interface IUsbDeviceHandle : IDisposable
{
    /// <summary>
    /// The opened device. It can hold more than the <see cref="UsbDeviceInfo"/> given to <see cref="IUsbBackend.Open"/>,
    /// for example the strings and endpoints that <see cref="WinUsbBackend"/> could not read during enumeration.
    /// </summary>
    UsbDeviceInfo Device { get; }

    /// <summary>The claimed interface, with its endpoints.</summary>
    UsbInterfaceInfo Interface { get; }

    /// <summary>
    /// Performs a control transfer on the default pipe. The setup packet's wLength is the buffer length, which can
    /// be at most 65535 bytes (<see cref="ArgumentOutOfRangeException"/> otherwise).
    /// </summary>
    int ControlTransfer(byte requestType, byte request, ushort value, ushort index, Span<byte> buffer, int timeoutMilliseconds);

    /// <summary>Performs a control transfer on the default pipe without blocking; see <see cref="ControlTransfer"/>.</summary>
    ValueTask<int> ControlTransferAsync(byte requestType, byte request, ushort value, ushort index, Memory<byte> buffer, int timeoutMilliseconds,
        CancellationToken cancellationToken = default);

    /// <summary>Performs a bulk transfer on <paramref name="endpoint"/>.</summary>
    int BulkTransfer(byte endpoint, Span<byte> buffer, int timeoutMilliseconds);

    /// <summary>Performs a bulk transfer on <paramref name="endpoint"/> without blocking.</summary>
    ValueTask<int> BulkTransferAsync(byte endpoint, Memory<byte> buffer, int timeoutMilliseconds, CancellationToken cancellationToken = default);

    /// <summary>Performs an interrupt transfer on <paramref name="endpoint"/>.</summary>
    int InterruptTransfer(byte endpoint, Span<byte> buffer, int timeoutMilliseconds);

    /// <summary>Performs an interrupt transfer on <paramref name="endpoint"/> without blocking.</summary>
    ValueTask<int> InterruptTransferAsync(byte endpoint, Memory<byte> buffer, int timeoutMilliseconds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears a halt (stall) on an endpoint: sends CLEAR_FEATURE(ENDPOINT_HALT) to the device and resets the host's
    /// data toggle for the endpoint. Throws <see cref="VisaException"/> when the request fails.
    /// </summary>
    void ClearHalt(byte endpoint);
}

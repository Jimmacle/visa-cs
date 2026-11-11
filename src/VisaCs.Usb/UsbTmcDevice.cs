using System.Buffers.Binary;
using VisaCs.Internal;

namespace VisaCs.Usb;

/// <summary>
/// A USBTMC / USB488 instrument on an opened USB interface: messages with their END (EOM) flag, vendor-specific
/// messages, device clear, the status byte, trigger, remote/local control and service requests. Create it with
/// <see cref="Open"/>, or over a handle from <see cref="IUsbBackend.Open"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Timeouts.</b> <c>timeoutMilliseconds</c> bounds the whole call, including the wait for another caller's
/// operation: a negative value (<see cref="Timeout.Infinite"/>) waits without limit, and 0 does not wait (the call
/// fails with a timeout unless the device is free and every transfer completes at once), as for a single
/// <see cref="IUsbDeviceHandle"/> transfer. A call that runs out of time throws <see cref="UsbTimeoutException"/>;
/// cancelling the token of an asynchronous call throws <see cref="OperationCanceledException"/>. When a bulk transfer
/// times out or is cancelled, the USBTMC abort sequence (INITIATE_ABORT_BULK_OUT/IN) runs with its own short
/// timeouts, so the device is ready for the next message. Class requests (clear, status byte, remote/local) wait at
/// most 5 seconds when the timeout is infinite, and at least 1 millisecond.
/// </para>
/// <para>
/// <b>Sharing.</b> All members may be called from any thread. Each operation (a message write, a read transfer, a
/// <see cref="Query"/>, a clear, ...) has the device to itself; others wait for it. bTags are counted per instance, so
/// several users of one instance never repeat the previous header's bTag, and status byte responses on the
/// interrupt endpoint reach the caller that asked for them. Other code that uses the same handle can take turns with
/// this instance through a shared gate (see the constructor), and can own the interrupt-IN endpoint itself (see
/// <see cref="ExternalInterruptReader"/>).
/// </para>
/// <para>
/// <b>Errors.</b> <see cref="UsbException"/> with <see cref="UsbError.ProtocolError"/> when the device answers with a
/// failure status or an invalid message, <see cref="UsbError.NotSupported"/> when the interface lacks the capability,
/// and whatever the <see cref="IUsbDeviceHandle"/> throws for failed transfers.
/// </para>
/// </remarks>
public sealed class UsbTmcDevice : IDisposable, IAsyncDisposable
{
    // USBTMC message IDs
    private const byte DevDepMsgOut = 1;
    private const byte RequestDevDepMsgIn = 2;
    private const byte DevDepMsgIn = 2;
    private const byte VendorSpecificOut = 126;
    private const byte RequestVendorSpecificIn = 127;
    private const byte VendorSpecificIn = 127;
    private const byte Usb488Trigger = 128;

    // USBTMC class requests
    private const byte InitiateAbortBulkOut = 1;
    private const byte CheckAbortBulkOutStatus = 2;
    private const byte InitiateAbortBulkIn = 3;
    private const byte CheckAbortBulkInStatus = 4;
    private const byte InitiateClear = 5;
    private const byte CheckClearStatus = 6;
    private const byte GetCapabilities = 7;
    private const byte IndicatorPulseRequest = 64;
    private const byte ReadStatusByteRequest = 128;
    private const byte RenControlRequest = 160;
    private const byte GoToLocalRequest = 161;
    private const byte LocalLockoutRequest = 162;

    private const byte StatusSuccess = 0x01;
    private const byte StatusPending = 0x02;

    private const int HeaderSize = 12;
    private const int MaxTransferSize = 1024 * 1024;
    private const int MessageChunkSize = 64 * 1024;

    private readonly IUsbDeviceHandle _handle;
    private readonly bool _leaveOpen;
    private readonly int _bulkOut;
    private readonly int _bulkIn;
    private readonly int _interruptIn;
    private readonly int _bulkInPacketSize;
    private readonly int _interruptPacketSize;
    private readonly ushort _interfaceNumber;

    private readonly SemaphoreSlim _gate;
    private byte[] _transferBuffer = new byte[HeaderSize + MessageChunkSize];
    private readonly byte[] _requestHeader = new byte[HeaderSize];

    private readonly Lock _tagSync = new();
    private readonly Queue<(byte Tag, byte Stb)> _statusResponses = new();
    private readonly ChangeSignal _statusChanged = new();
    // Random start values, so that a reopened device does not see the bTag of the previous user again.
    private byte _bTag = (byte)Random.Shared.Next(1, 256);
    private byte _statusTag = (byte)Random.Shared.Next(2, 128);

    private readonly List<Action<UsbTmcInterrupt>> _handlers = new(); // also the lock of the reader fields
    private CancellationTokenSource? _readerStop;
    private Task? _reader;
    private volatile bool _externalReader;
    private volatile bool _disposed;

    // Replaced as a whole, so that readers on other threads never see a half-written value.
    private volatile object? _capabilities; // boxed UsbTmcCapabilities

    /// <summary>
    /// Creates the client over an opened interface. Does no I/O: call <see cref="ReadCapabilities"/> to learn what
    /// the device supports (<see cref="Open"/> does).
    /// </summary>
    /// <param name="handle">The interface, normally a USBTMC interface (class 0xFE, subclass 3).</param>
    /// <param name="leaveOpen">True to leave <paramref name="handle"/> open when this instance is disposed.</param>
    /// <param name="gate">
    /// A semaphore with a count of 1 that other users of <paramref name="handle"/> also take around their transfers,
    /// so that they and this instance take turns; it is not disposed with this instance. Null for a private one.
    /// </param>
    /// <remarks>
    /// An interface without bulk endpoints is accepted; the message operations then throw
    /// <see cref="UsbError.NotSupported"/>. <see cref="ControlTransfer"/>,
    /// <see cref="RunExclusive{T}"/> and the interrupt handlers work on any interface.
    /// </remarks>
    public UsbTmcDevice(IUsbDeviceHandle handle, bool leaveOpen = false, SemaphoreSlim? gate = null)
    {
        ArgumentNullException.ThrowIfNull(handle);
        _handle = handle;
        _leaveOpen = leaveOpen;
        _gate = gate ?? new SemaphoreSlim(1, 1);
        var info = handle.Interface;
        var bulkOut = info.FindEndpoint(UsbTransferType.Bulk, input: false);
        var bulkIn = info.FindEndpoint(UsbTransferType.Bulk, input: true);
        var interrupt = info.FindEndpoint(UsbTransferType.Interrupt, input: true);
        _bulkOut = bulkOut?.Address ?? -1;
        _bulkIn = bulkIn?.Address ?? -1;
        _interruptIn = interrupt?.Address ?? -1;
        _bulkInPacketSize = Math.Max(8, (int)(bulkIn?.MaxPacketSize ?? 512));
        _interruptPacketSize = Math.Max(2, (int)(interrupt?.MaxPacketSize ?? 2));
        _interfaceNumber = info.Number;
        IsUsb488 = info.IsUsbTmc && info.Protocol == 1;
    }

    /// <summary>
    /// Opens a USBTMC interface of <paramref name="device"/> through <paramref name="backend"/> and reads its
    /// capabilities. Dispose the result to close the interface.
    /// </summary>
    /// <param name="backend">The backend that enumerated <paramref name="device"/>.</param>
    /// <param name="device">The device.</param>
    /// <param name="interfaceNumber">The interface to open; null for the first USBTMC interface.</param>
    /// <param name="timeoutMilliseconds">Time for the GET_CAPABILITIES request. When the device does not answer,
    /// <see cref="Capabilities"/> stays all false.</param>
    /// <exception cref="UsbException">
    /// <see cref="UsbError.NotFound"/> when the device has no such interface or the interface has no bulk
    /// endpoints, or an error of <see cref="IUsbBackend.Open"/>.
    /// </exception>
    public static UsbTmcDevice Open(IUsbBackend backend, UsbDeviceInfo device, int? interfaceNumber = null, int timeoutMilliseconds = 5000)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        int number;
        if (interfaceNumber is { } n)
        {
            number = n;
        }
        else
        {
            var info = device.Interfaces.FirstOrDefault(i => i.IsUsbTmc)
                // The backend could not read the interfaces without opening the device: try its first interface.
                ?? (device.Interfaces.All(i => i.Endpoints.Count == 0) ? device.Interfaces.FirstOrDefault() : null)
                ?? throw UsbErrors.NotFound($"{device} has no USBTMC interface.");
            number = info.Number;
        }

        var handle = backend.Open(device, number);
        var tmc = new UsbTmcDevice(handle);
        try
        {
            if (tmc._bulkOut < 0 || tmc._bulkIn < 0)
                throw UsbErrors.NotFound($"USB interface {number} of {device} has no bulk endpoints; it is not a USBTMC interface.");
            try
            {
                tmc.ReadCapabilities(timeoutMilliseconds);
            }
            catch (Exception ex) when (ex is UsbException or UsbTimeoutException)
            {
                // Capabilities stay all false.
            }
            return tmc;
        }
        catch
        {
            tmc.Dispose();
            throw;
        }
    }

    /// <summary>The opened interface.</summary>
    public IUsbDeviceHandle Handle => _handle;

    /// <summary>True when the interface is a USB488 interface (protocol 1): status byte, trigger, remote/local and SRQ.</summary>
    public bool IsUsb488 { get; }

    /// <summary>True when the interface has an interrupt-IN endpoint (service requests and status byte responses).</summary>
    public bool HasInterruptEndpoint => _interruptIn >= 0;

    /// <summary>
    /// The capabilities of the last successful <see cref="ReadCapabilities"/>; all false until then. They decide
    /// whether the device handles a termination character, and whether trigger, remote/local control and
    /// INDICATOR_PULSE are allowed. Safe to read while another thread reads the capabilities.
    /// </summary>
    public UsbTmcCapabilities Capabilities => _capabilities is UsbTmcCapabilities c ? c : default;

    // ------------------------------------------------------------------ public API

    /// <summary>Reads the device's GET_CAPABILITIES response and stores it in <see cref="Capabilities"/>.</summary>
    /// <exception cref="UsbException"><see cref="UsbError.ProtocolError"/> when the device reports a failure or answers too briefly.</exception>
    public UsbTmcCapabilities ReadCapabilities(int timeoutMilliseconds) =>
        AsyncIO.Complete(ReadCapabilitiesCoreAsync(Start(timeoutMilliseconds, default), async: false));

    /// <summary>Reads the device's capabilities without blocking; see <see cref="ReadCapabilities"/>.</summary>
    public async ValueTask<UsbTmcCapabilities> ReadCapabilitiesAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        try
        {
            return await ReadCapabilitiesCoreAsync(Start(timeoutMilliseconds, cancellationToken), async: true).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is UsbException or UsbTimeoutException) && cancellationToken.IsCancellationRequested)
        {
            throw AsyncIO.Canceled(ex, cancellationToken);
        }
    }

    /// <summary>
    /// Sends <paramref name="data"/> as one device-dependent message (DEV_DEP_MSG_OUT transfers of at most 1 MiB).
    /// <paramref name="end"/> sets EOM on the last transfer; without it, the next write continues the message.
    /// </summary>
    public void Write(ReadOnlySpan<byte> data, bool end, int timeoutMilliseconds)
    {
        var deadline = Start(timeoutMilliseconds, default);
        using (AsyncIO.Complete(EnterAsync(deadline, async: false)))
            WriteSync(data, DevDepMsgOut, end, deadline);
    }

    /// <summary>Sends a message without blocking; see <see cref="Write"/>.</summary>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, bool end, int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        WriteMessageAsync(data, DevDepMsgOut, end, timeoutMilliseconds, cancellationToken);

    /// <summary>Sends <paramref name="data"/> in VENDOR_SPECIFIC_OUT transfers (no EOM flag).</summary>
    public void WriteVendorSpecific(ReadOnlySpan<byte> data, int timeoutMilliseconds)
    {
        var deadline = Start(timeoutMilliseconds, default);
        using (AsyncIO.Complete(EnterAsync(deadline, async: false)))
            WriteSync(data, VendorSpecificOut, end: false, deadline);
    }

    /// <summary>Sends vendor-specific data without blocking; see <see cref="WriteVendorSpecific"/>.</summary>
    public ValueTask WriteVendorSpecificAsync(ReadOnlyMemory<byte> data, int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        WriteMessageAsync(data, VendorSpecificOut, end: false, timeoutMilliseconds, cancellationToken);

    /// <summary>
    /// Reads one Bulk-IN transfer of a device-dependent message: asks for up to <paramref name="buffer"/>'s length
    /// (at most 1 MiB) with REQUEST_DEV_DEP_MSG_IN. The result's End is true when the device set EOM; otherwise the
    /// message continues in the next read.
    /// </summary>
    /// <param name="buffer">Receives the data.</param>
    /// <param name="timeoutMilliseconds">Timeout of the call.</param>
    /// <param name="termChar">
    /// Asks the device to end the transfer after this byte. Used only when <see cref="Capabilities"/> reports
    /// <see cref="UsbTmcCapabilities.SupportsTermChar"/>; End stays false when the transfer ends there without EOM.
    /// </param>
    public UsbTmcReadResult Read(Span<byte> buffer, int timeoutMilliseconds, byte? termChar = null)
    {
        if (buffer.IsEmpty) return default;
        var deadline = Start(timeoutMilliseconds, default);
        using (AsyncIO.Complete(EnterAsync(deadline, async: false)))
        {
            var result = AsyncIO.Complete(ReceiveAsync(buffer.Length, vendor: false, termChar, deadline, async: false));
            _transferBuffer.AsSpan(HeaderSize, result.Count).CopyTo(buffer);
            return result;
        }
    }

    /// <summary>Reads one transfer of a message without blocking; see <see cref="Read"/>.</summary>
    public ValueTask<UsbTmcReadResult> ReadAsync(Memory<byte> buffer, int timeoutMilliseconds, byte? termChar = null,
        CancellationToken cancellationToken = default) =>
        ReadTransferAsync(buffer, vendor: false, termChar, timeoutMilliseconds, cancellationToken);

    /// <summary>
    /// Reads one VENDOR_SPECIFIC_IN transfer of up to <paramref name="buffer"/>'s length (at most 1 MiB). These
    /// transfers have no EOM flag: End is true when the device sent less than was asked for.
    /// </summary>
    public UsbTmcReadResult ReadVendorSpecific(Span<byte> buffer, int timeoutMilliseconds)
    {
        if (buffer.IsEmpty) return default;
        var deadline = Start(timeoutMilliseconds, default);
        using (AsyncIO.Complete(EnterAsync(deadline, async: false)))
        {
            var result = AsyncIO.Complete(ReceiveAsync(buffer.Length, vendor: true, null, deadline, async: false));
            _transferBuffer.AsSpan(HeaderSize, result.Count).CopyTo(buffer);
            return result;
        }
    }

    /// <summary>Reads one vendor-specific transfer without blocking; see <see cref="ReadVendorSpecific"/>.</summary>
    public ValueTask<UsbTmcReadResult> ReadVendorSpecificAsync(Memory<byte> buffer, int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        ReadTransferAsync(buffer, vendor: true, null, timeoutMilliseconds, cancellationToken);

    /// <summary>Reads a whole device-dependent message: transfers until the one with EOM.</summary>
    public byte[] ReadMessage(int timeoutMilliseconds)
    {
        var deadline = Start(timeoutMilliseconds, default);
        using (AsyncIO.Complete(EnterAsync(deadline, async: false)))
            return AsyncIO.Complete(ReadMessageCoreAsync(deadline, async: false));
    }

    /// <summary>Reads a whole message without blocking; see <see cref="ReadMessage"/>.</summary>
    public async ValueTask<byte[]> ReadMessageAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        try
        {
            var deadline = Start(timeoutMilliseconds, cancellationToken);
            using (await EnterAsync(deadline, async: true).ConfigureAwait(false))
                return await ReadMessageCoreAsync(deadline, async: true).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is UsbException or UsbTimeoutException) && cancellationToken.IsCancellationRequested)
        {
            throw AsyncIO.Canceled(ex, cancellationToken);
        }
    }

    /// <summary>
    /// Sends <paramref name="command"/> as a message with END and reads the whole response message, with no other
    /// operation on this instance in between.
    /// </summary>
    public byte[] Query(ReadOnlySpan<byte> command, int timeoutMilliseconds)
    {
        var deadline = Start(timeoutMilliseconds, default);
        using (AsyncIO.Complete(EnterAsync(deadline, async: false)))
        {
            WriteSync(command, DevDepMsgOut, end: true, deadline);
            return AsyncIO.Complete(ReadMessageCoreAsync(deadline, async: false));
        }
    }

    /// <summary>Sends a command and reads the response without blocking; see <see cref="Query"/>.</summary>
    public async ValueTask<byte[]> QueryAsync(ReadOnlyMemory<byte> command, int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        try
        {
            var deadline = Start(timeoutMilliseconds, cancellationToken);
            using (await EnterAsync(deadline, async: true).ConfigureAwait(false))
            {
                await WriteCoreAsync(command, DevDepMsgOut, end: true, deadline, async: true).ConfigureAwait(false);
                return await ReadMessageCoreAsync(deadline, async: true).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when ((ex is UsbException or UsbTimeoutException) && cancellationToken.IsCancellationRequested)
        {
            throw AsyncIO.Canceled(ex, cancellationToken);
        }
    }

    /// <summary>
    /// Clears the device (INITIATE_CLEAR, then CHECK_CLEAR_STATUS until done): the device discards its input and
    /// output buffers. Also clears a halt on the Bulk-OUT endpoint.
    /// </summary>
    public void Clear(int timeoutMilliseconds) => AsyncIO.Complete(RunAsync(static (d, dl, async) => d.ClearCoreAsync(dl, async),
        Start(timeoutMilliseconds, default), async: false));

    /// <summary>Clears the device without blocking; see <see cref="Clear"/>.</summary>
    public ValueTask ClearAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        RunPublicAsync(static (d, dl, async) => d.ClearCoreAsync(dl, async), timeoutMilliseconds, cancellationToken);

    /// <summary>
    /// Reads the status byte with the USB488 READ_STATUS_BYTE request. With an interrupt-IN endpoint the device sends
    /// the byte there; it is matched to this request by its bTag.
    /// </summary>
    /// <exception cref="UsbException"><see cref="UsbError.NotSupported"/> on a non-USB488 interface.</exception>
    public byte ReadStatusByte(int timeoutMilliseconds)
    {
        var deadline = Start(timeoutMilliseconds, default);
        using (AsyncIO.Complete(EnterAsync(deadline, async: false)))
            return AsyncIO.Complete(ReadStatusByteCoreAsync(deadline, async: false));
    }

    /// <summary>Reads the status byte without blocking; see <see cref="ReadStatusByte"/>.</summary>
    public async ValueTask<byte> ReadStatusByteAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        try
        {
            var deadline = Start(timeoutMilliseconds, cancellationToken);
            using (await EnterAsync(deadline, async: true).ConfigureAwait(false))
                return await ReadStatusByteCoreAsync(deadline, async: true).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is UsbException or UsbTimeoutException) && cancellationToken.IsCancellationRequested)
        {
            throw AsyncIO.Canceled(ex, cancellationToken);
        }
    }

    /// <summary>Sends the USB488 TRIGGER message.</summary>
    /// <exception cref="UsbException">
    /// <see cref="UsbError.NotSupported"/> unless the interface is USB488 and
    /// <see cref="UsbTmcCapabilities.AcceptsTrigger"/> is set.
    /// </exception>
    public void Trigger(int timeoutMilliseconds) => AsyncIO.Complete(RunAsync(static (d, dl, async) => d.TriggerCoreAsync(dl, async),
        Start(timeoutMilliseconds, default), async: false));

    /// <summary>Sends the USB488 TRIGGER message without blocking; see <see cref="Trigger"/>.</summary>
    public ValueTask TriggerAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        RunPublicAsync(static (d, dl, async) => d.TriggerCoreAsync(dl, async), timeoutMilliseconds, cancellationToken);

    /// <summary>Asserts or deasserts remote enable (USB488 REN_CONTROL).</summary>
    /// <exception cref="UsbException">
    /// <see cref="UsbError.NotSupported"/> unless the interface is USB488 and
    /// <see cref="UsbTmcCapabilities.AcceptsRenControl"/> is set.
    /// </exception>
    public void RenControl(bool enable, int timeoutMilliseconds) =>
        AsyncIO.Complete(Usb488RequestAsync(RenControlRequest, enable ? (ushort)1 : (ushort)0, Start(timeoutMilliseconds, default), async: false));

    /// <summary>Asserts or deasserts remote enable without blocking; see <see cref="RenControl"/>.</summary>
    public ValueTask RenControlAsync(bool enable, int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        Usb488RequestPublicAsync(RenControlRequest, enable ? (ushort)1 : (ushort)0, timeoutMilliseconds, cancellationToken);

    /// <summary>Returns the device to local control (USB488 GO_TO_LOCAL); requirements as for <see cref="RenControl"/>.</summary>
    public void GoToLocal(int timeoutMilliseconds) =>
        AsyncIO.Complete(Usb488RequestAsync(GoToLocalRequest, 0, Start(timeoutMilliseconds, default), async: false));

    /// <summary>Returns the device to local control without blocking; see <see cref="GoToLocal"/>.</summary>
    public ValueTask GoToLocalAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        Usb488RequestPublicAsync(GoToLocalRequest, 0, timeoutMilliseconds, cancellationToken);

    /// <summary>Disables the device's local controls (USB488 LOCAL_LOCKOUT); requirements as for <see cref="RenControl"/>.</summary>
    public void LocalLockout(int timeoutMilliseconds) =>
        AsyncIO.Complete(Usb488RequestAsync(LocalLockoutRequest, 0, Start(timeoutMilliseconds, default), async: false));

    /// <summary>Disables the device's local controls without blocking; see <see cref="LocalLockout"/>.</summary>
    public ValueTask LocalLockoutAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        Usb488RequestPublicAsync(LocalLockoutRequest, 0, timeoutMilliseconds, cancellationToken);

    /// <summary>Asks the device to flash its activity indicator (USBTMC INDICATOR_PULSE).</summary>
    /// <exception cref="UsbException">
    /// <see cref="UsbError.NotSupported"/> unless <see cref="UsbTmcCapabilities.AcceptsIndicatorPulse"/> is set.
    /// </exception>
    public void IndicatorPulse(int timeoutMilliseconds) =>
        AsyncIO.Complete(RunAsync(static (d, dl, async) => d.IndicatorPulseCoreAsync(dl, async), Start(timeoutMilliseconds, default), async: false));

    /// <summary>Asks the device to flash its activity indicator without blocking; see <see cref="IndicatorPulse"/>.</summary>
    public ValueTask IndicatorPulseAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        RunPublicAsync(static (d, dl, async) => d.IndicatorPulseCoreAsync(dl, async), timeoutMilliseconds, cancellationToken);

    /// <summary>
    /// Performs a control transfer on the default pipe (see <see cref="IUsbDeviceHandle.ControlTransfer"/>) between
    /// the operations of other callers, and returns the number of bytes transferred.
    /// </summary>
    public int ControlTransfer(byte requestType, byte request, ushort value, ushort index, Span<byte> buffer, int timeoutMilliseconds)
    {
        var deadline = Start(timeoutMilliseconds, default);
        using (AsyncIO.Complete(EnterAsync(deadline, async: false)))
            return _handle.ControlTransfer(requestType, request, value, index, buffer, ToUsbTimeout(deadline));
    }

    /// <summary>Performs a control transfer without blocking; see <see cref="ControlTransfer"/>.</summary>
    public async ValueTask<int> ControlTransferAsync(byte requestType, byte request, ushort value, ushort index, Memory<byte> buffer,
        int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        try
        {
            var deadline = Start(timeoutMilliseconds, cancellationToken);
            using (await EnterAsync(deadline, async: true).ConfigureAwait(false))
                return await _handle.ControlTransferAsync(requestType, request, value, index, buffer, ToUsbTimeout(deadline), cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is UsbException or UsbTimeoutException) && cancellationToken.IsCancellationRequested)
        {
            throw AsyncIO.Canceled(ex, cancellationToken);
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> on <see cref="Handle"/> while no other operation of this instance is in
    /// progress, for transfers outside the USBTMC protocol. <paramref name="timeoutMilliseconds"/> bounds only the
    /// wait for the device; the action sets its own transfer timeouts. The action must not call this instance.
    /// </summary>
    public T RunExclusive<T>(Func<IUsbDeviceHandle, T> action, int timeoutMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(action);
        using (AsyncIO.Complete(EnterAsync(Start(timeoutMilliseconds, default), async: false)))
            return action(_handle);
    }

    /// <summary>Runs <paramref name="action"/> without blocking while waiting for the device; see <see cref="RunExclusive{T}"/>.</summary>
    public async ValueTask<T> RunExclusiveAsync<T>(Func<IUsbDeviceHandle, CancellationToken, ValueTask<T>> action, int timeoutMilliseconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            using (await EnterAsync(Start(timeoutMilliseconds, cancellationToken), async: true).ConfigureAwait(false))
                return await action(_handle, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is UsbException or UsbTimeoutException) && cancellationToken.IsCancellationRequested)
        {
            throw AsyncIO.Canceled(ex, cancellationToken);
        }
    }

    // ------------------------------------------------------------------ interrupt endpoint

    /// <summary>
    /// Adds a handler for packets from the interrupt-IN endpoint, such as USB488 service requests
    /// (<see cref="UsbTmcInterrupt.IsServiceRequest"/>). The first handler starts a loop that reads the endpoint
    /// (holding no thread while idle); handlers run on that loop and must return quickly. Status byte responses to
    /// <see cref="ReadStatusByte"/> of a USB488 interface are not passed on. A handler that throws is ignored.
    /// </summary>
    /// <exception cref="UsbException"><see cref="UsbError.NotSupported"/> when the interface has no interrupt-IN endpoint.</exception>
    public void AddInterruptHandler(Action<UsbTmcInterrupt> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_interruptIn < 0) throw UsbErrors.NotSupported("Receiving interrupt packets (the interface has no interrupt-IN endpoint)");
        if (_externalReader) throw new InvalidOperationException("Other code reads the interrupt endpoint (ExternalInterruptReader is set).");
        lock (_handlers)
        {
            _handlers.Add(handler);
            if (_reader is not null) return;
            _readerStop = new CancellationTokenSource();
            var stop = _readerStop.Token;
            _reader = Task.Run(() => ReadInterruptsAsync(stop));
        }
    }

    /// <summary>
    /// Removes a handler added with <see cref="AddInterruptHandler"/>. Removing the last one stops the read loop and
    /// waits up to a second for it to end.
    /// </summary>
    public void RemoveInterruptHandler(Action<UsbTmcInterrupt> handler) => AsyncIO.Complete(RemoveInterruptHandlerAsync(handler, async: false));

    /// <summary>Removes an interrupt handler without blocking; see <see cref="RemoveInterruptHandler"/>.</summary>
    public ValueTask RemoveInterruptHandlerAsync(Action<UsbTmcInterrupt> handler) => RemoveInterruptHandlerAsync(handler, async: true);

    private ValueTask RemoveInterruptHandlerAsync(Action<UsbTmcInterrupt> handler, bool async)
    {
        bool stop;
        lock (_handlers)
        {
            stop = _handlers.Remove(handler) && _handlers.Count == 0;
        }
        return stop ? StopReaderAsync(async) : default;
    }

    private bool ReaderRunning
    {
        get
        {
            lock (_handlers) return _reader is not null;
        }
    }

    private async ValueTask StopReaderAsync(bool async)
    {
        Task? reader;
        lock (_handlers)
        {
            reader = _reader;
            _reader = null;
            _readerStop?.Cancel();
            _readerStop = null;
        }
        if (reader is null) return;
        try
        {
            if (async) await reader.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            else reader.Wait(1000);
        }
        catch
        {
            // The loop ends on its own.
        }
    }

    private async Task ReadInterruptsAsync(CancellationToken stop)
    {
        var packet = new byte[_interruptPacketSize];
        while (!stop.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await _handle.InterruptTransferAsync((byte)_interruptIn, packet, Timeout.Infinite, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (UsbTimeoutException)
            {
                continue;
            }
            catch
            {
                try { await Task.Delay(50, stop).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                continue;
            }
            if (n > 0) Dispatch(packet.AsSpan(0, n).ToArray());
        }
    }

    /// <summary>
    /// True while other code reads the interrupt-IN endpoint and passes every packet to <see cref="ProcessInterrupt"/>,
    /// for example to share the endpoint with code that uses the same handle outside the USBTMC protocol.
    /// <see cref="ReadStatusByte"/> then waits for the status byte response to arrive that way instead of reading the
    /// endpoint itself, and <see cref="AddInterruptHandler"/> throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public bool ExternalInterruptReader
    {
        get => _externalReader;
        set => _externalReader = value;
    }

    /// <summary>
    /// Takes a packet that other code read from the interrupt-IN endpoint (see <see cref="ExternalInterruptReader"/>).
    /// Returns true when it was a USB488 status byte response, which this instance hands to the
    /// <see cref="ReadStatusByte"/> caller waiting for it; false for any other packet, which the caller handles itself.
    /// </summary>
    public bool ProcessInterrupt(ReadOnlySpan<byte> packet)
    {
        if (!IsStatusResponse(packet)) return false;
        lock (_tagSync)
        {
            _statusResponses.Enqueue(((byte)(packet[0] & 0x7F), packet[1]));
            _statusChanged.Pulse();
        }
        return true;
    }

    /// <summary>A USB488 READ_STATUS_BYTE response: [0x80 | bTag, STB], with bTag 2..127 (1 is a service request).</summary>
    private bool IsStatusResponse(ReadOnlySpan<byte> data) => IsUsb488 && data.Length >= 2 && (data[0] & 0x80) != 0 && (data[0] & 0x7F) > 1;

    /// <summary>Queues a USB488 status byte response for its requester, and passes anything else to the handlers.</summary>
    private void Dispatch(byte[] data)
    {
        if (ProcessInterrupt(data)) return;
        Action<UsbTmcInterrupt>[] handlers;
        lock (_handlers) handlers = _handlers.ToArray();
        if (handlers.Length == 0) return;
        var e = new UsbTmcInterrupt(data);
        foreach (var h in handlers)
        {
            try { h(e); } catch { /* ignore */ }
        }
    }

    /// <summary>Waits for the READ_STATUS_BYTE response with <paramref name="tag"/>; null on timeout.</summary>
    private async ValueTask<byte?> TakeStatusResponseAsync(byte tag, Deadline deadline, bool async)
    {
        while (true)
        {
            Task next;
            lock (_tagSync)
            {
                while (_statusResponses.TryDequeue(out var item))
                {
                    if (item.Tag == tag) return item.Stb;
                }
                next = _statusChanged.Next;
            }
            if (!await ChangeSignal.WaitAsync(next, deadline, async).ConfigureAwait(false)) return null;
        }
    }

    // ------------------------------------------------------------------ disposal

    /// <summary>Stops the interrupt read loop and closes the handle (unless created with leaveOpen).</summary>
    public void Dispose() => AsyncIO.Complete(DisposeCoreAsync(async: false));

    /// <summary>Stops the interrupt read loop without blocking and closes the handle (unless created with leaveOpen).</summary>
    public ValueTask DisposeAsync() => DisposeCoreAsync(async: true);

    private async ValueTask DisposeCoreAsync(bool async)
    {
        if (_disposed) return;
        _disposed = true;
        lock (_handlers) _handlers.Clear();
        await StopReaderAsync(async).ConfigureAwait(false);
        if (!_leaveOpen) _handle.Dispose();
    }

    // ------------------------------------------------------------------ helpers

    private Deadline Start(int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Deadline.FromMilliseconds(timeoutMilliseconds).WithCancellation(cancellationToken);
    }

    /// <summary>Waits for the operations of other callers to finish; dispose the result to leave.</summary>
    private async ValueTask<IOGate.Releaser> EnterAsync(Deadline deadline, bool async)
    {
        int timeout = deadline.RemainingMilliseconds; // -1: no limit
        bool entered = async
            ? await _gate.WaitAsync(timeout, deadline.CancellationToken).ConfigureAwait(false)
            : _gate.Wait(timeout, deadline.CancellationToken);
        if (!entered) throw UsbErrors.Timeout("Another operation kept the USB device busy until the timeout.");
        return new IOGate.Releaser(_gate);
    }

    private async ValueTask RunAsync(Func<UsbTmcDevice, Deadline, bool, ValueTask> operation, Deadline deadline, bool async)
    {
        using (await EnterAsync(deadline, async).ConfigureAwait(false))
            await operation(this, deadline, async).ConfigureAwait(false);
    }

    private async ValueTask RunPublicAsync(Func<UsbTmcDevice, Deadline, bool, ValueTask> operation, int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync(operation, Start(timeoutMilliseconds, cancellationToken), async: true).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is UsbException or UsbTimeoutException) && cancellationToken.IsCancellationRequested)
        {
            throw AsyncIO.Canceled(ex, cancellationToken);
        }
    }

    private ValueTask Usb488RequestAsync(byte request, ushort value, Deadline deadline, bool async) =>
        RunAsync(async (d, dl, a) =>
        {
            if (!d.IsUsb488 || !d.Capabilities.AcceptsRenControl)
                throw UsbErrors.NotSupported("Remote/local control on a device without USB488 REN_CONTROL capability");
            await d.ControlRequestAsync(request, value, dl, a).ConfigureAwait(false);
        }, deadline, async);

    private async ValueTask Usb488RequestPublicAsync(byte request, ushort value, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        try
        {
            await Usb488RequestAsync(request, value, Start(timeoutMilliseconds, cancellationToken), async: true).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is UsbException or UsbTimeoutException) && cancellationToken.IsCancellationRequested)
        {
            throw AsyncIO.Canceled(ex, cancellationToken);
        }
    }

    private async ValueTask WriteMessageAsync(ReadOnlyMemory<byte> data, byte messageId, bool end, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        try
        {
            var deadline = Start(timeoutMilliseconds, cancellationToken);
            using (await EnterAsync(deadline, async: true).ConfigureAwait(false))
                await WriteCoreAsync(data, messageId, end, deadline, async: true).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is UsbException or UsbTimeoutException) && cancellationToken.IsCancellationRequested)
        {
            throw AsyncIO.Canceled(ex, cancellationToken);
        }
    }

    private async ValueTask<UsbTmcReadResult> ReadTransferAsync(Memory<byte> buffer, bool vendor, byte? termChar, int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        if (buffer.IsEmpty) return default;
        try
        {
            var deadline = Start(timeoutMilliseconds, cancellationToken);
            using (await EnterAsync(deadline, async: true).ConfigureAwait(false))
            {
                var result = await ReceiveAsync(buffer.Length, vendor, termChar, deadline, async: true).ConfigureAwait(false);
                _transferBuffer.AsMemory(HeaderSize, result.Count).CopyTo(buffer);
                return result;
            }
        }
        catch (Exception ex) when ((ex is UsbException or UsbTimeoutException) && cancellationToken.IsCancellationRequested)
        {
            throw AsyncIO.Canceled(ex, cancellationToken);
        }
    }

    /// <summary>
    /// USB timeout for one transfer: the remaining time, -1 (no limit) when the deadline is infinite, and 0 (do not
    /// wait) when it has expired.
    /// </summary>
    private static int ToUsbTimeout(Deadline deadline) => deadline.RemainingMilliseconds;

    /// <summary>
    /// USB timeout for a class request that must end even when the timeout is infinite: the remaining time,
    /// 5 seconds when the deadline is infinite, and one last millisecond when it has expired.
    /// </summary>
    private static int ToRequestTimeout(Deadline deadline)
    {
        int remaining = deadline.RemainingMilliseconds;
        return remaining < 0 ? 5000 : Math.Max(1, remaining);
    }

    private static int RoundUp(int value, int multiple) => (value + multiple - 1) / multiple * multiple;

    private ValueTask<int> BulkAsync(int endpoint, Memory<byte> buffer, int timeoutMs, CancellationToken cancellationToken, bool async) =>
        async
            ? _handle.BulkTransferAsync((byte)endpoint, buffer, timeoutMs, cancellationToken)
            : new(_handle.BulkTransfer((byte)endpoint, buffer.Span, timeoutMs));

    private ValueTask<int> ControlAsync(byte requestType, byte request, ushort value, ushort index, Memory<byte> buffer, int timeoutMs,
        CancellationToken cancellationToken, bool async) =>
        async
            ? _handle.ControlTransferAsync(requestType, request, value, index, buffer, timeoutMs, cancellationToken)
            : new(_handle.ControlTransfer(requestType, request, value, index, buffer.Span, timeoutMs));

    private ValueTask<int> InterruptAsync(Memory<byte> buffer, int timeoutMs, CancellationToken cancellationToken, bool async) =>
        async
            ? _handle.InterruptTransferAsync((byte)_interruptIn, buffer, timeoutMs, cancellationToken)
            : new(_handle.InterruptTransfer((byte)_interruptIn, buffer.Span, timeoutMs));

    private static ValueTask DelayAsync(int milliseconds, bool async)
    {
        if (async) return new ValueTask(Task.Delay(milliseconds));
        Thread.Sleep(milliseconds);
        return default;
    }

    private byte NextTag()
    {
        lock (_tagSync)
        {
            _bTag = (byte)(_bTag == 255 ? 1 : _bTag + 1);
            return _bTag;
        }
    }

    private byte NextStatusTag()
    {
        lock (_tagSync)
        {
            _statusTag = (byte)(_statusTag >= 127 ? 2 : _statusTag + 1);
            return _statusTag;
        }
    }

    // ------------------------------------------------------------------ protocol (called with the gate held)

    private async ValueTask<UsbTmcCapabilities> ReadCapabilitiesCoreAsync(Deadline deadline, bool async)
    {
        var c = new byte[0x18];
        int n = await ControlAsync(0xA1, GetCapabilities, 0, _interfaceNumber, c, ToUsbTimeout(deadline), deadline.CancellationToken, async)
            .ConfigureAwait(false);
        if (n < 6 || c[0] != StatusSuccess)
            throw UsbErrors.Protocol($"USBTMC GET_CAPABILITIES failed (status 0x{c[0]:X2}, {n} bytes).");
        bool has488 = n >= 16;
        var capabilities = new UsbTmcCapabilities(
            BinaryPrimitives.ReadUInt16LittleEndian(c.AsSpan(2)),
            (c[4] & 4) != 0, (c[4] & 2) != 0, (c[4] & 1) != 0, (c[5] & 1) != 0,
            has488 ? BinaryPrimitives.ReadUInt16LittleEndian(c.AsSpan(12)) : (ushort)0,
            has488 && (c[14] & 4) != 0, has488 && (c[14] & 2) != 0, has488 && (c[14] & 1) != 0,
            has488 && (c[15] & 8) != 0, has488 && (c[15] & 4) != 0, has488 && (c[15] & 2) != 0, has488 && (c[15] & 1) != 0);
        _capabilities = capabilities; // a new box: published whole
        return capabilities;
    }

    /// <summary>Writes a USBTMC header and the chunk into the transfer buffer; returns the padded length and the bTag.</summary>
    private (int Length, byte Tag) BuildPacket(ReadOnlySpan<byte> chunk, byte messageId, bool eom)
    {
        int total = (HeaderSize + chunk.Length + 3) & ~3;
        if (_transferBuffer.Length < total) _transferBuffer = new byte[total];
        var span = _transferBuffer.AsSpan(0, total);
        span.Clear();
        byte tag = NextTag();
        span[0] = messageId;
        span[1] = tag;
        span[2] = (byte)~tag;
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)chunk.Length);
        if (messageId == DevDepMsgOut) span[8] = (byte)(eom ? 1 : 0); // EOM (reserved in vendor-specific messages)
        chunk.CopyTo(span[HeaderSize..]);
        return (total, tag);
    }

    private void CheckBulkEndpoints()
    {
        if (_bulkOut < 0 || _bulkIn < 0) throw UsbErrors.NotSupported("USBTMC messages on an interface without bulk endpoints");
    }

    private void WriteSync(ReadOnlySpan<byte> data, byte messageId, bool end, Deadline deadline)
    {
        CheckBulkEndpoints();
        int offset = 0;
        do
        {
            int n = Math.Min(MaxTransferSize, data.Length - offset);
            var (length, tag) = BuildPacket(data.Slice(offset, n), messageId, end && offset + n >= data.Length);
            AsyncIO.Complete(SendBulkOutAsync(_transferBuffer.AsMemory(0, length), tag, deadline, async: false));
            offset += n;
        } while (offset < data.Length);
    }

    private async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> data, byte messageId, bool end, Deadline deadline, bool async)
    {
        CheckBulkEndpoints();
        int offset = 0;
        do
        {
            int n = Math.Min(MaxTransferSize, data.Length - offset);
            var (length, tag) = BuildPacket(data.Span.Slice(offset, n), messageId, end && offset + n >= data.Length);
            await SendBulkOutAsync(_transferBuffer.AsMemory(0, length), tag, deadline, async).ConfigureAwait(false);
            offset += n;
        } while (offset < data.Length);
    }

    /// <summary>Sends one Bulk-OUT message; runs the abort sequence when it times out or is cancelled.</summary>
    private async ValueTask SendBulkOutAsync(Memory<byte> packet, byte tag, Deadline deadline, bool async)
    {
        try
        {
            int sent = await BulkAsync(_bulkOut, packet, ToUsbTimeout(deadline), deadline.CancellationToken, async).ConfigureAwait(false);
            if (sent < packet.Length) throw UsbErrors.Protocol("Short USBTMC bulk-out transfer.");
        }
        catch (Exception ex) when (ex is UsbTimeoutException or OperationCanceledException)
        {
            await AbortBulkOutAsync(tag, async).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Requests and reads one Bulk-IN transfer of up to <paramref name="length"/> bytes (at most 1 MiB). The data is
    /// left in the transfer buffer after the header.
    /// </summary>
    private async ValueTask<UsbTmcReadResult> ReceiveAsync(int length, bool vendor, byte? termChar, Deadline deadline, bool async)
    {
        CheckBulkEndpoints();
        int request = Math.Min(length, MaxTransferSize);
        bool deviceTermChar = !vendor && termChar.HasValue && Capabilities.SupportsTermChar;

        for (int attempt = 0; ; attempt++)
        {
            byte tag = NextTag();
            var header = _requestHeader;
            Array.Clear(header);
            header[0] = vendor ? RequestVendorSpecificIn : RequestDevDepMsgIn;
            header[1] = tag;
            header[2] = (byte)~tag;
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)request);
            if (deviceTermChar)
            {
                header[8] = 0x02;
                header[9] = termChar!.Value;
            }
            await SendBulkOutAsync(header, tag, deadline, async).ConfigureAwait(false);

            // Read the DEV_DEP_MSG_IN transfer: header, payload and alignment bytes, ending with a short packet.
            int capacity = RoundUp(HeaderSize + request + 3, _bulkInPacketSize);
            if (_transferBuffer.Length < capacity) _transferBuffer = new byte[capacity];
            int received = 0;
            int expected = -1;
            int strayZeroLengthPackets = 0;
            try
            {
                while (received < capacity)
                {
                    int want = capacity - received;
                    if (expected >= 0) want = Math.Min(want, RoundUp(expected - received, _bulkInPacketSize));
                    int n = await BulkAsync(_bulkIn, _transferBuffer.AsMemory(received, want), ToUsbTimeout(deadline),
                        deadline.CancellationToken, async).ConfigureAwait(false);
                    if (n == 0 && received == 0 && strayZeroLengthPackets++ < 2) continue; // ZLP left over from a previous transfer
                    received += n;
                    if (expected < 0 && received >= HeaderSize)
                        expected = HeaderSize + (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(_transferBuffer.AsSpan(4)), (uint)request);
                    if (expected >= 0 && received >= expected) break;
                    if (n == 0 || n % _bulkInPacketSize != 0) break; // a short packet ends the transfer
                }
            }
            catch (Exception ex) when (ex is UsbTimeoutException or OperationCanceledException)
            {
                await AbortBulkInAsync(tag, async).ConfigureAwait(false);
                throw;
            }

            if (received < HeaderSize) throw UsbErrors.Protocol("Short USBTMC bulk-in transfer.");
            var response = _transferBuffer.AsSpan(0, received);
            if (response[0] != (vendor ? VendorSpecificIn : DevDepMsgIn) || response[1] != tag || response[2] != (byte)~tag)
            {
                // Stale data from an earlier aborted transfer: try again a few times.
                if (attempt < 3) continue;
                throw UsbErrors.Protocol("Invalid USBTMC bulk-in header (bTag mismatch).");
            }
            int size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(response[4..]), (uint)request);
            size = Math.Min(size, received - HeaderSize);
            // Vendor-specific transfers have no EOM flag: a transfer shorter than requested ends the message.
            bool end = vendor ? size < request : (response[8] & 0x01) != 0;
            return new UsbTmcReadResult(size, end);
        }
    }

    private async ValueTask<byte[]> ReadMessageCoreAsync(Deadline deadline, bool async)
    {
        var message = new MemoryStream();
        while (true)
        {
            var result = await ReceiveAsync(MessageChunkSize, vendor: false, null, deadline, async).ConfigureAwait(false);
            message.Write(_transferBuffer, HeaderSize, result.Count);
            if (result.End) return message.ToArray();
        }
    }

    // The abort sequences run with their own short timeouts and are not cancellable: they restore the device after
    // a transfer that timed out or was cancelled.

    private async ValueTask AbortBulkOutAsync(byte tag, bool async)
    {
        try
        {
            var status = new byte[8];
            int n = await ControlAsync(0xA2, InitiateAbortBulkOut, tag, (ushort)_bulkOut, status.AsMemory(0, 2), 1000, default, async)
                .ConfigureAwait(false);
            if (n < 1 || status[0] != StatusSuccess) return;
            for (int i = 0; i < 100; i++)
            {
                n = await ControlAsync(0xA2, CheckAbortBulkOutStatus, 0, (ushort)_bulkOut, status, 1000, default, async).ConfigureAwait(false);
                if (n < 1 || status[0] != StatusPending) break;
                await DelayAsync(5, async).ConfigureAwait(false);
            }
            _handle.ClearHalt((byte)_bulkOut);
        }
        catch
        {
            // Best effort recovery.
        }
    }

    private async ValueTask AbortBulkInAsync(byte tag, bool async)
    {
        try
        {
            var status = new byte[8];
            int n = await ControlAsync(0xA2, InitiateAbortBulkIn, tag, (ushort)_bulkIn, status.AsMemory(0, 2), 1000, default, async)
                .ConfigureAwait(false);
            if (n < 1 || status[0] != StatusSuccess) return;
            var scratch = new byte[RoundUp(4096, _bulkInPacketSize)];
            for (int i = 0; i < 100; i++)
            {
                try
                {
                    int r = await BulkAsync(_bulkIn, scratch, 100, default, async).ConfigureAwait(false);
                    if (r % _bulkInPacketSize != 0) break;
                }
                catch (UsbTimeoutException)
                {
                    break;
                }
            }
            for (int i = 0; i < 100; i++)
            {
                n = await ControlAsync(0xA2, CheckAbortBulkInStatus, 0, (ushort)_bulkIn, status, 1000, default, async).ConfigureAwait(false);
                if (n < 1 || status[0] != StatusPending) break;
                if ((status[1] & 1) != 0)
                {
                    try { await BulkAsync(_bulkIn, scratch, 100, default, async).ConfigureAwait(false); } catch (UsbTimeoutException) { }
                }
                await DelayAsync(5, async).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best effort recovery.
        }
    }

    private async ValueTask ClearCoreAsync(Deadline deadline, bool async)
    {
        CheckBulkEndpoints();
        int timeout = ToRequestTimeout(deadline);
        var status = new byte[2];
        int n = await ControlAsync(0xA1, InitiateClear, 0, _interfaceNumber, status.AsMemory(0, 1), timeout, deadline.CancellationToken, async)
            .ConfigureAwait(false);
        if (n < 1 || status[0] != StatusSuccess)
            throw UsbErrors.Protocol($"USBTMC INITIATE_CLEAR failed (status 0x{status[0]:X2}).");

        var scratch = new byte[RoundUp(4096, _bulkInPacketSize)];
        while (true)
        {
            n = await ControlAsync(0xA1, CheckClearStatus, 0, _interfaceNumber, status, timeout, deadline.CancellationToken, async)
                .ConfigureAwait(false);
            if (n < 1) throw UsbErrors.Protocol("USBTMC CHECK_CLEAR_STATUS failed.");
            if (status[0] != StatusPending) break;
            if (n >= 2 && (status[1] & 1) != 0)
            {
                try { await BulkAsync(_bulkIn, scratch, 100, deadline.CancellationToken, async).ConfigureAwait(false); } catch (UsbTimeoutException) { }
            }
            else
            {
                await DelayAsync(5, async).ConfigureAwait(false);
            }
            if (deadline.HasExpired) throw UsbErrors.Timeout("USBTMC device clear did not complete.");
        }
        _handle.ClearHalt((byte)_bulkOut);
    }

    private async ValueTask<byte> ReadStatusByteCoreAsync(Deadline deadline, bool async)
    {
        if (!IsUsb488) throw UsbErrors.NotSupported("Reading the status byte of a USBTMC (non-USB488) device");

        byte tag = NextStatusTag();
        var response = new byte[3];
        int n = await ControlAsync(0xA1, ReadStatusByteRequest, tag, _interfaceNumber, response, ToRequestTimeout(deadline),
            deadline.CancellationToken, async).ConfigureAwait(false);
        if (n < 1) throw UsbErrors.Protocol("USB488 READ_STATUS_BYTE failed.");
        if (response[0] != StatusSuccess)
            throw UsbErrors.Protocol($"USB488 READ_STATUS_BYTE failed (status 0x{response[0]:X2}).");

        if (_interruptIn < 0) return response[2];

        // With an interrupt-IN endpoint, the status byte arrives there as [0x80 | bTag, STB].
        if (_externalReader || ReaderRunning)
        {
            return await TakeStatusResponseAsync(tag, deadline, async).ConfigureAwait(false)
                ?? throw UsbErrors.Timeout("No USB488 status byte notification received.");
        }
        var packet = new byte[_interruptPacketSize];
        while (true)
        {
            int r = await InterruptAsync(packet, ToUsbTimeout(deadline), deadline.CancellationToken, async).ConfigureAwait(false);
            if (r < 2) continue;
            if ((packet[0] & 0x80) != 0 && (packet[0] & 0x7F) == tag) return packet[1];
            Dispatch(packet.AsSpan(0, r).ToArray());
        }
    }

    private async ValueTask TriggerCoreAsync(Deadline deadline, bool async)
    {
        if (!IsUsb488 || !Capabilities.AcceptsTrigger)
            throw UsbErrors.NotSupported("TRIGGER on a device without USB488 trigger capability");
        CheckBulkEndpoints();
        var packet = new byte[HeaderSize];
        byte tag = NextTag();
        packet[0] = Usb488Trigger;
        packet[1] = tag;
        packet[2] = (byte)~tag;
        await SendBulkOutAsync(packet, tag, deadline, async).ConfigureAwait(false);
    }

    private async ValueTask IndicatorPulseCoreAsync(Deadline deadline, bool async)
    {
        if (!Capabilities.AcceptsIndicatorPulse) throw UsbErrors.NotSupported("INDICATOR_PULSE");
        await ControlRequestAsync(IndicatorPulseRequest, 0, deadline, async).ConfigureAwait(false);
    }

    /// <summary>A class request to the interface that answers with a one-byte USBTMC status.</summary>
    private async ValueTask ControlRequestAsync(byte request, ushort value, Deadline deadline, bool async)
    {
        var status = new byte[1];
        int n = await ControlAsync(0xA1, request, value, _interfaceNumber, status, ToRequestTimeout(deadline), deadline.CancellationToken, async)
            .ConfigureAwait(false);
        if (n < 1 || status[0] != StatusSuccess)
            throw UsbErrors.Protocol($"USBTMC request {request} failed (status 0x{status[0]:X2}).");
    }
}

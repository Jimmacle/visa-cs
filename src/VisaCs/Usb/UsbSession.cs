using System.Buffers.Binary;
using VisaCs.Internal;
using VisaCs.Usb;

namespace VisaCs;

/// <summary>USBTMC / USB488 instrument session (<c>USB::VID::PID::SERIAL[::intf]::INSTR</c>).</summary>
/// <remarks>
/// A transfer that times out or is cancelled is followed by the USBTMC abort sequence (INITIATE_ABORT_BULK_IN/OUT),
/// so the device is ready for the next message. The abort sequences run with their own short timeouts.
/// </remarks>
public sealed class UsbSession : UsbSessionBase
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
    private const byte RenControl = 160;
    private const byte GoToLocal = 161;
    private const byte LocalLockout = 162;

    private const byte StatusSuccess = 0x01;
    private const byte StatusPending = 0x02;

    private const int HeaderSize = 12;
    private const int MaxTransferSize = 1024 * 1024;

    private byte[] _transferBuffer = new byte[HeaderSize + 64 * 1024];
    private readonly byte[] _requestHeader = new byte[HeaderSize];

    internal UsbSession(ResourceManager manager, UsbResourceName name) : base(manager, name)
    {
    }

    /// <summary>
    /// Capabilities reported by the device's GET_CAPABILITIES response, read when the session opens. All false or 0
    /// when the device did not answer: the host then handles the termination character, and trigger and remote/local
    /// control throw VI_ERROR_NSUP_OPER.
    /// </summary>
    public UsbTmcCapabilities Capabilities { get; private set; }

    /// <summary>USBTMC or USB488, from the interface protocol (VI_ATTR_USB_PROTOCOL).</summary>
    public UsbProtocol UsbProtocol => Device.Interface.Protocol == 1 ? UsbProtocol.Usb488 : UsbProtocol.UsbTmc;

    /// <inheritdoc/>
    public override bool Is4882Compliant => UsbProtocol == UsbProtocol.Usb488 && Capabilities.IsUsb4882;

    /// <inheritdoc/>
    public override IReadOnlyCollection<EventType> SupportedEventTypes { get; } = [EventType.ServiceRequest, EventType.UsbInterrupt];

    private protected override bool IsPreferredInterface(UsbInterfaceInfo info) => info.IsUsbTmc;

    private protected override async ValueTask OnDeviceOpenedAsync(Deadline deadline, bool async)
    {
        if (BulkOutEndpoint < 0 || BulkInEndpoint < 0)
            throw new VisaException(VisaStatus.ErrorResourceNotFound, "The USB interface has no bulk endpoints; it is not a USBTMC interface.");
        int timeout = Math.Max(500, deadline.RemainingOr(500));
        Capabilities = await QueryCapabilitiesAsync(timeout, deadline.CancellationToken, async).ConfigureAwait(false);
    }

    private async ValueTask<UsbTmcCapabilities> QueryCapabilitiesAsync(int timeoutMs, CancellationToken cancellationToken, bool async)
    {
        var c = new byte[0x18];
        try
        {
            int n = await ControlAsync(0xA1, GetCapabilities, 0, Device.Interface.Number, c, timeoutMs, cancellationToken, async).ConfigureAwait(false);
            if (n < 6 || c[0] != StatusSuccess) return default;
            bool has488 = n >= 16;
            return new UsbTmcCapabilities(
                BinaryPrimitives.ReadUInt16LittleEndian(c.AsSpan(2)),
                (c[4] & 4) != 0, (c[4] & 2) != 0, (c[4] & 1) != 0, (c[5] & 1) != 0,
                has488 ? BinaryPrimitives.ReadUInt16LittleEndian(c.AsSpan(12)) : (ushort)0,
                has488 && (c[14] & 4) != 0, has488 && (c[14] & 2) != 0, has488 && (c[14] & 1) != 0,
                has488 && (c[15] & 8) != 0, has488 && (c[15] & 4) != 0, has488 && (c[15] & 2) != 0, has488 && (c[15] & 1) != 0);
        }
        catch (VisaException)
        {
            return default;
        }
    }

    // ------------------------------------------------------------------ bulk messages

    private protected override bool SupportsIOProtocol(IOProtocol protocol) => protocol is IOProtocol.Normal or IOProtocol.UsbTmcVendor;

    /// <summary>Sends one Bulk-OUT message; runs the abort sequence when it times out or is cancelled.</summary>
    private async ValueTask SendBulkOutAsync(Memory<byte> packet, byte tag, Deadline deadline, bool async)
    {
        try
        {
            int sent = await BulkAsync(BulkOutEndpoint, packet, ToUsbTimeout(deadline), deadline.CancellationToken, async).ConfigureAwait(false);
            if (sent < packet.Length) throw new VisaException(VisaStatus.ErrorIO, "Short USBTMC bulk-out transfer.");
        }
        catch (Exception ex) when (ex is VisaTimeoutException or OperationCanceledException)
        {
            await AbortBulkOutAsync(tag, async).ConfigureAwait(false);
            throw;
        }
    }

    private protected override async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> data, bool end, Deadline deadline, bool async)
    {
        using var gate = await EnterDeviceAsync(deadline, async).ConfigureAwait(false);
        bool vendor = IOProtocol == IOProtocol.UsbTmcVendor;
        int offset = 0;
        do
        {
            int n = Math.Min(MaxTransferSize, data.Length - offset);
            bool last = offset + n >= data.Length;
            int total = (HeaderSize + n + 3) & ~3;
            if (_transferBuffer.Length < total) _transferBuffer = new byte[total];
            var packet = _transferBuffer.AsMemory(0, total);
            packet.Span.Clear();
            byte tag = SharedDevice.NextTag();
            var span = packet.Span;
            span[0] = vendor ? VendorSpecificOut : DevDepMsgOut;
            span[1] = tag;
            span[2] = (byte)~tag;
            BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)n);
            if (!vendor) span[8] = (byte)(last && end ? 1 : 0); // EOM (reserved in vendor-specific messages)
            data.Span.Slice(offset, n).CopyTo(span[HeaderSize..]);
            await SendBulkOutAsync(packet, tag, deadline, async).ConfigureAwait(false);
            offset += n;
        } while (offset < data.Length);
    }

    private protected override async ValueTask<FillResult> FillCoreAsync(Memory<byte> target, Deadline deadline, bool async)
    {
        using var gate = await EnterDeviceAsync(deadline, async).ConfigureAwait(false);
        int request = Math.Min(target.Length, MaxTransferSize);
        int term = EffectiveTermChar;
        bool vendor = IOProtocol == IOProtocol.UsbTmcVendor;
        bool deviceTermChar = !vendor && term >= 0 && Capabilities.SupportsTermChar;

        for (int attempt = 0; ; attempt++)
        {
            byte tag = SharedDevice.NextTag();
            var header = _requestHeader;
            Array.Clear(header);
            header[0] = vendor ? RequestVendorSpecificIn : RequestDevDepMsgIn;
            header[1] = tag;
            header[2] = (byte)~tag;
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)request);
            if (deviceTermChar)
            {
                header[8] = 0x02;
                header[9] = (byte)term;
            }
            await SendBulkOutAsync(header, tag, deadline, async).ConfigureAwait(false);

            // Read the DEV_DEP_MSG_IN transfer: header, payload and alignment bytes, ending with a short packet.
            int capacity = RoundUp(HeaderSize + request + 3, BulkInPacketSize);
            if (_transferBuffer.Length < capacity) _transferBuffer = new byte[capacity];
            int received = 0;
            int expected = -1;
            int strayZeroLengthPackets = 0;
            try
            {
                while (received < capacity)
                {
                    int want = capacity - received;
                    if (expected >= 0) want = Math.Min(want, RoundUp(expected - received, BulkInPacketSize));
                    int n = await BulkAsync(BulkInEndpoint, _transferBuffer.AsMemory(received, want), ToUsbTimeout(deadline),
                        deadline.CancellationToken, async).ConfigureAwait(false);
                    if (n == 0 && received == 0 && strayZeroLengthPackets++ < 2) continue; // ZLP left over from a previous transfer
                    received += n;
                    if (expected < 0 && received >= HeaderSize)
                        expected = HeaderSize + (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(_transferBuffer.AsSpan(4)), (uint)request);
                    if (expected >= 0 && received >= expected) break;
                    if (n == 0 || n % BulkInPacketSize != 0) break; // a short packet ends the transfer
                }
            }
            catch (Exception ex) when (ex is VisaTimeoutException or OperationCanceledException)
            {
                await AbortBulkInAsync(tag, async).ConfigureAwait(false);
                throw;
            }

            if (received < HeaderSize)
                throw new VisaException(VisaStatus.ErrorIO, "Short USBTMC bulk-in transfer.");
            var response = _transferBuffer.AsSpan(0, received);
            if (response[0] != (vendor ? VendorSpecificIn : DevDepMsgIn) || response[1] != tag || response[2] != (byte)~tag)
            {
                // Stale data from an earlier aborted transfer: try again a few times.
                if (attempt < 3) continue;
                throw new VisaException(VisaStatus.ErrorIO, "Invalid USBTMC bulk-in header (bTag mismatch).");
            }
            int size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(response[4..]), (uint)request);
            size = Math.Min(size, received - HeaderSize);
            response.Slice(HeaderSize, size).CopyTo(target.Span);
            // Vendor-specific transfers have no EOM flag: a transfer shorter than requested ends the message.
            bool end = vendor ? size < request : (response[8] & 0x01) != 0;
            return new FillResult(size, end);
        }
    }

    private static int RoundUp(int value, int multiple) => (value + multiple - 1) / multiple * multiple;

    // The abort sequences run with their own short timeouts and are not cancellable: they restore the device after
    // a transfer that timed out or was cancelled.

    private async ValueTask AbortBulkOutAsync(byte tag, bool async)
    {
        try
        {
            var status = new byte[8];
            int n = await ControlAsync(0xA2, InitiateAbortBulkOut, tag, (ushort)BulkOutEndpoint, status.AsMemory(0, 2), 1000, default, async)
                .ConfigureAwait(false);
            if (n < 1 || status[0] != StatusSuccess) return;
            for (int i = 0; i < 100; i++)
            {
                n = await ControlAsync(0xA2, CheckAbortBulkOutStatus, 0, (ushort)BulkOutEndpoint, status, 1000, default, async).ConfigureAwait(false);
                if (n < 1 || status[0] != StatusPending) break;
                await DelayAsync(5, async).ConfigureAwait(false);
            }
            Device.ClearHalt((byte)BulkOutEndpoint);
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
            int n = await ControlAsync(0xA2, InitiateAbortBulkIn, tag, (ushort)BulkInEndpoint, status.AsMemory(0, 2), 1000, default, async)
                .ConfigureAwait(false);
            if (n < 1 || status[0] != StatusSuccess) return;
            var scratch = new byte[RoundUp(4096, BulkInPacketSize)];
            for (int i = 0; i < 100; i++)
            {
                try
                {
                    int r = await BulkAsync(BulkInEndpoint, scratch, 100, default, async).ConfigureAwait(false);
                    if (r % BulkInPacketSize != 0) break;
                }
                catch (VisaTimeoutException)
                {
                    break;
                }
            }
            for (int i = 0; i < 100; i++)
            {
                n = await ControlAsync(0xA2, CheckAbortBulkInStatus, 0, (ushort)BulkInEndpoint, status, 1000, default, async).ConfigureAwait(false);
                if (n < 1 || status[0] != StatusPending) break;
                if ((status[1] & 1) != 0)
                {
                    try { await BulkAsync(BulkInEndpoint, scratch, 100, default, async).ConfigureAwait(false); } catch (VisaTimeoutException) { }
                }
                await DelayAsync(5, async).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best effort recovery.
        }
    }

    // ------------------------------------------------------------------ device operations

    private protected override async ValueTask ClearCoreAsync(Deadline deadline, bool async)
    {
        using var gate = await EnterDeviceAsync(deadline, async).ConfigureAwait(false);
        int timeout = ToRequestTimeout(deadline);
        var status = new byte[2];
        int n = await ControlAsync(0xA1, InitiateClear, 0, Device.Interface.Number, status.AsMemory(0, 1), timeout, deadline.CancellationToken, async)
            .ConfigureAwait(false);
        if (n < 1 || status[0] != StatusSuccess)
            throw new VisaException(VisaStatus.ErrorIO, $"USBTMC INITIATE_CLEAR failed (status 0x{status[0]:X2}).");

        var scratch = new byte[RoundUp(4096, BulkInPacketSize)];
        while (true)
        {
            n = await ControlAsync(0xA1, CheckClearStatus, 0, Device.Interface.Number, status, timeout, deadline.CancellationToken, async)
                .ConfigureAwait(false);
            if (n < 1) throw new VisaException(VisaStatus.ErrorIO, "USBTMC CHECK_CLEAR_STATUS failed.");
            if (status[0] != StatusPending) break;
            if (n >= 2 && (status[1] & 1) != 0)
            {
                try { await BulkAsync(BulkInEndpoint, scratch, 100, deadline.CancellationToken, async).ConfigureAwait(false); } catch (VisaTimeoutException) { }
            }
            else
            {
                await DelayAsync(5, async).ConfigureAwait(false);
            }
            if (deadline.HasExpired) throw new VisaTimeoutException("USBTMC device clear did not complete.");
        }
        Device.ClearHalt((byte)BulkOutEndpoint);
    }

    private protected override async ValueTask<byte> ReadStatusByteCoreAsync(Deadline deadline, bool async)
    {
        using var gate = await EnterDeviceAsync(deadline, async).ConfigureAwait(false);
        if (UsbProtocol != UsbProtocol.Usb488)
            throw VisaException.NotSupported("Reading the status byte of a USBTMC (non-USB488) device");

        var shared = SharedDevice;
        byte tag = shared.NextStatusTag();
        var response = new byte[3];
        int n = await ControlAsync(0xA1, ReadStatusByteRequest, tag, Device.Interface.Number, response, ToRequestTimeout(deadline),
            deadline.CancellationToken, async).ConfigureAwait(false);
        if (n < 1) throw new VisaException(VisaStatus.ErrorIO, "USB488 READ_STATUS_BYTE failed.");
        if (response[0] != StatusSuccess)
            throw new VisaException(VisaStatus.ErrorIO, $"USB488 READ_STATUS_BYTE failed (status 0x{response[0]:X2}).");

        if (InterruptInEndpoint < 0) return response[2];

        // With an interrupt-IN endpoint, the status byte arrives there as [0x80 | bTag, STB].
        if (shared.ReaderRunning)
        {
            return await shared.TakeStatusResponseAsync(tag, deadline, async).ConfigureAwait(false)
                ?? throw new VisaTimeoutException("No USB488 status byte notification received.");
        }
        var packet = new byte[Math.Max(2, InterruptPacketSize)];
        while (true)
        {
            int r = await InterruptAsync(InterruptInEndpoint, packet, ToUsbTimeout(deadline), deadline.CancellationToken, async).ConfigureAwait(false);
            if (r < 2) continue;
            if ((packet[0] & 0x80) != 0 && (packet[0] & 0x7F) == tag) return packet[1];
            HandleInterruptPacket(packet.AsSpan(0, r));
        }
    }

    private protected override async ValueTask TriggerCoreAsync(Deadline deadline, bool async)
    {
        using var gate = await EnterDeviceAsync(deadline, async).ConfigureAwait(false);
        if (UsbProtocol != UsbProtocol.Usb488 || !Capabilities.AcceptsTrigger)
            throw VisaException.NotSupported("TRIGGER on a device without USB488 trigger capability");
        var packet = new byte[HeaderSize];
        byte tag = SharedDevice.NextTag();
        packet[0] = Usb488Trigger;
        packet[1] = tag;
        packet[2] = (byte)~tag;
        await SendBulkOutAsync(packet, tag, deadline, async).ConfigureAwait(false);
    }

    private protected override async ValueTask ControlRenCoreAsync(RemoteLocalMode mode, Deadline deadline, bool async)
    {
        using var gate = await EnterDeviceAsync(deadline, async).ConfigureAwait(false);
        if (UsbProtocol != UsbProtocol.Usb488 || !Capabilities.AcceptsRenControl)
            throw VisaException.NotSupported("Remote/local control on a device without USB488 REN_CONTROL capability");
        int timeout = ToRequestTimeout(deadline);
        var ct = deadline.CancellationToken;
        switch (mode)
        {
            case RemoteLocalMode.Deassert:
                await Usb488RequestAsync(RenControl, 0, timeout, ct, async).ConfigureAwait(false);
                break;
            case RemoteLocalMode.Assert:
            case RemoteLocalMode.AssertAddress:
                await Usb488RequestAsync(RenControl, 1, timeout, ct, async).ConfigureAwait(false);
                break;
            case RemoteLocalMode.DeassertGoToLocal:
                await Usb488RequestAsync(GoToLocal, 0, timeout, ct, async).ConfigureAwait(false);
                await Usb488RequestAsync(RenControl, 0, timeout, ct, async).ConfigureAwait(false);
                break;
            case RemoteLocalMode.AssertLocalLockout:
            case RemoteLocalMode.AssertAddressLocalLockout:
                await Usb488RequestAsync(RenControl, 1, timeout, ct, async).ConfigureAwait(false);
                await Usb488RequestAsync(LocalLockout, 0, timeout, ct, async).ConfigureAwait(false);
                break;
            case RemoteLocalMode.AddressGoToLocal:
                await Usb488RequestAsync(GoToLocal, 0, timeout, ct, async).ConfigureAwait(false);
                break;
        }
    }

    private async ValueTask Usb488RequestAsync(byte request, ushort value, int timeout, CancellationToken cancellationToken, bool async)
    {
        var status = new byte[1];
        int n = await ControlAsync(0xA1, request, value, Device.Interface.Number, status, timeout, cancellationToken, async).ConfigureAwait(false);
        if (n < 1 || status[0] != StatusSuccess)
            throw new VisaException(VisaStatus.ErrorIO, $"USB488 request {request} failed (status 0x{status[0]:X2}).");
    }

    /// <summary>
    /// Asks the device to flash its activity indicator (USBTMC INDICATOR_PULSE). Throws VI_ERROR_NSUP_OPER when
    /// <see cref="UsbTmcCapabilities.AcceptsIndicatorPulse"/> is false.
    /// </summary>
    public void IndicatorPulse() => AsyncIO.Complete(IndicatorPulseAsync(CancellationToken.None, async: false));

    /// <summary>Asks the device to flash its activity indicator without blocking; see <see cref="IndicatorPulse()"/>.</summary>
    public Task IndicatorPulseAsync(CancellationToken cancellationToken = default) => IndicatorPulseAsync(cancellationToken, async: true).AsTask();

    private async ValueTask IndicatorPulseAsync(CancellationToken cancellationToken, bool async) =>
        await ExecuteAsync(0, static async (s, _, ct, async) =>
        {
            var session = (UsbSession)s;
            if (!session.Capabilities.AcceptsIndicatorPulse) throw VisaException.NotSupported("INDICATOR_PULSE");
            var deadline = session.NewDeadline(ct);
            using (await session.EnterDeviceAsync(deadline, async).ConfigureAwait(false))
                await session.ControlAsync(0xA1, IndicatorPulseRequest, 0, session.Device.Interface.Number, new byte[1], 1000, ct, async)
                    .ConfigureAwait(false);
            return true;
        }, cancellationToken, async).ConfigureAwait(false);

    // ------------------------------------------------------------------ interrupt endpoint (SRQ)

    private Action<byte[]>? _interruptHandler;

    private protected override ValueTask OnEventEnabledAsync(EventType eventType, bool async)
    {
        if (InterruptInEndpoint < 0)
            throw new VisaException(VisaStatus.ErrorInvalidEvent, "The device has no interrupt-IN endpoint, so it cannot send service requests.");
        if (_interruptHandler is not null) return default;
        _interruptHandler = packet => HandleInterruptPacket(packet);
        Shared!.Subscribe(_interruptHandler);
        return default;
    }

    private protected override ValueTask OnEventDisabledAsync(EventType eventType, bool async)
    {
        if (IsEventEnabled(EventType.ServiceRequest) || IsEventEnabled(EventType.UsbInterrupt)) return default;
        var handler = Interlocked.Exchange(ref _interruptHandler, null);
        return handler is not null && Shared is { } shared ? shared.UnsubscribeAsync(handler, async) : default;
    }

    private void HandleInterruptPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length >= 2 && (packet[0] & 0x80) != 0)
        {
            int tag = packet[0] & 0x7F;
            if (tag == 1) RaiseEvent(new ServiceRequestEventArgs(packet[1]));
            else Shared?.AddStatusResponse((byte)tag, packet[1]);
        }
        else
        {
            RaiseEvent(new UsbInterruptEventArgs(packet.ToArray()));
        }
    }

    private protected override async ValueTask CloseCoreAsync(bool async)
    {
        var handler = Interlocked.Exchange(ref _interruptHandler, null);
        if (handler is not null && Shared is { } shared) await shared.UnsubscribeAsync(handler, async).ConfigureAwait(false);
        await base.CloseCoreAsync(async).ConfigureAwait(false);
    }

    private protected override void RegisterAttributes(AttributeTable table)
    {
        base.RegisterAttributes(table);
        table.Add(VisaAttribute.UsbProtocol, () => (int)UsbProtocol);
    }
}

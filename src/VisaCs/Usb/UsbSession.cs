using VisaCs.Internal;
using VisaCs.Usb;

namespace VisaCs;

/// <summary>USBTMC / USB488 instrument session (<c>USB::VID::PID::SERIAL[::intf]::INSTR</c>).</summary>
/// <remarks>
/// The USBTMC protocol is implemented by <see cref="UsbTmcDevice"/>, which the sessions to one interface share. A
/// transfer that times out or is cancelled is followed by the USBTMC abort sequence (INITIATE_ABORT_BULK_IN/OUT), so
/// the device is ready for the next message. The abort sequences run with their own short timeouts.
/// </remarks>
public sealed class UsbSession : UsbSessionBase
{
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

    /// <summary>The USBTMC client of the interface, shared with the other sessions to it.</summary>
    private UsbTmcDevice Tmc => SharedDevice.Tmc;

    private protected override bool IsPreferredInterface(UsbInterfaceInfo info) => info.IsUsbTmc;

    private protected override async ValueTask OnDeviceOpenedAsync(Deadline deadline, bool async)
    {
        if (BulkOutEndpoint < 0 || BulkInEndpoint < 0)
            throw new VisaException(VisaStatus.ErrorResourceNotFound, "The USB interface has no bulk endpoints; it is not a USBTMC interface.");
        int timeout = Math.Max(500, deadline.RemainingOr(500));
        try
        {
            Capabilities = async
                ? await Tmc.ReadCapabilitiesAsync(timeout, deadline.CancellationToken).ConfigureAwait(false)
                : Tmc.ReadCapabilities(timeout);
        }
        catch (Exception ex) when (UsbErrorMapping.IsUsbError(ex))
        {
            Capabilities = default; // the host handles the termination character
        }
    }

    // ------------------------------------------------------------------ bulk messages

    private protected override bool SupportsIOProtocol(IOProtocol protocol) => protocol is IOProtocol.Normal or IOProtocol.UsbTmcVendor;

    private protected override ValueTask WriteCoreAsync(ReadOnlyMemory<byte> data, bool end, Deadline deadline, bool async)
    {
        var tmc = Tmc;
        int timeout = ToUsbTimeout(deadline);
        bool vendor = IOProtocol == IOProtocol.UsbTmcVendor;
        var token = deadline.CancellationToken;
        if (async)
        {
            return UsbErrorMapping.RunAsync(() => vendor
                ? tmc.WriteVendorSpecificAsync(data, timeout, token)
                : tmc.WriteAsync(data, end, timeout, token));
        }
        UsbErrorMapping.Run(() =>
        {
            if (vendor) tmc.WriteVendorSpecific(data.Span, timeout);
            else tmc.Write(data.Span, end, timeout);
        });
        return default;
    }

    private protected override async ValueTask<FillResult> FillCoreAsync(Memory<byte> target, Deadline deadline, bool async)
    {
        var tmc = Tmc;
        int timeout = ToUsbTimeout(deadline);
        bool vendor = IOProtocol == IOProtocol.UsbTmcVendor;
        int term = EffectiveTermChar;
        var token = deadline.CancellationToken;
        // The device ends a transfer at the termination character only when it says it can.
        byte? termChar = term >= 0 && Capabilities.SupportsTermChar ? (byte)term : null;
        UsbTmcReadResult result;
        if (async)
        {
            result = await UsbErrorMapping.RunAsync(() => vendor
                ? tmc.ReadVendorSpecificAsync(target, timeout, token)
                : tmc.ReadAsync(target, timeout, termChar, token)).ConfigureAwait(false);
        }
        else
        {
            result = UsbErrorMapping.Run(() => vendor ? tmc.ReadVendorSpecific(target.Span, timeout) : tmc.Read(target.Span, timeout, termChar));
        }
        return new FillResult(result.Count, result.End);
    }

    // ------------------------------------------------------------------ device operations

    private protected override ValueTask ClearCoreAsync(Deadline deadline, bool async)
    {
        var tmc = Tmc;
        int timeout = deadline.RemainingMilliseconds;
        var token = deadline.CancellationToken;
        if (async) return UsbErrorMapping.RunAsync(() => tmc.ClearAsync(timeout, token));
        UsbErrorMapping.Run(() => tmc.Clear(timeout));
        return default;
    }

    private protected override ValueTask<byte> ReadStatusByteCoreAsync(Deadline deadline, bool async)
    {
        if (UsbProtocol != UsbProtocol.Usb488)
            throw VisaException.NotSupported("Reading the status byte of a USBTMC (non-USB488) device");
        var tmc = Tmc;
        // The response on the interrupt endpoint has never been waited for once the time was up.
        int timeout = InterruptInEndpoint >= 0 ? ToUsbTimeout(deadline) : deadline.RemainingMilliseconds;
        var token = deadline.CancellationToken;
        return async
            ? UsbErrorMapping.RunAsync(() => tmc.ReadStatusByteAsync(timeout, token))
            : new(UsbErrorMapping.Run(() => tmc.ReadStatusByte(timeout)));
    }

    private protected override ValueTask TriggerCoreAsync(Deadline deadline, bool async)
    {
        if (UsbProtocol != UsbProtocol.Usb488 || !Capabilities.AcceptsTrigger)
            throw VisaException.NotSupported("TRIGGER on a device without USB488 trigger capability");
        var tmc = Tmc;
        int timeout = ToUsbTimeout(deadline);
        var token = deadline.CancellationToken;
        if (async) return UsbErrorMapping.RunAsync(() => tmc.TriggerAsync(timeout, token));
        UsbErrorMapping.Run(() => tmc.Trigger(timeout));
        return default;
    }

    private protected override async ValueTask ControlRenCoreAsync(RemoteLocalMode mode, Deadline deadline, bool async)
    {
        if (UsbProtocol != UsbProtocol.Usb488 || !Capabilities.AcceptsRenControl)
            throw VisaException.NotSupported("Remote/local control on a device without USB488 REN_CONTROL capability");
        switch (mode)
        {
            case RemoteLocalMode.Deassert:
                await RenControlAsync(false, deadline, async).ConfigureAwait(false);
                break;
            case RemoteLocalMode.Assert:
            case RemoteLocalMode.AssertAddress:
                await RenControlAsync(true, deadline, async).ConfigureAwait(false);
                break;
            case RemoteLocalMode.DeassertGoToLocal:
                await GoToLocalAsync(deadline, async).ConfigureAwait(false);
                await RenControlAsync(false, deadline, async).ConfigureAwait(false);
                break;
            case RemoteLocalMode.AssertLocalLockout:
            case RemoteLocalMode.AssertAddressLocalLockout:
                await RenControlAsync(true, deadline, async).ConfigureAwait(false);
                await LocalLockoutAsync(deadline, async).ConfigureAwait(false);
                break;
            case RemoteLocalMode.AddressGoToLocal:
                await GoToLocalAsync(deadline, async).ConfigureAwait(false);
                break;
        }
    }

    private ValueTask RenControlAsync(bool enable, Deadline deadline, bool async)
    {
        var tmc = Tmc;
        int timeout = deadline.RemainingMilliseconds;
        var token = deadline.CancellationToken;
        if (async) return UsbErrorMapping.RunAsync(() => tmc.RenControlAsync(enable, timeout, token));
        UsbErrorMapping.Run(() => tmc.RenControl(enable, timeout));
        return default;
    }

    private ValueTask GoToLocalAsync(Deadline deadline, bool async)
    {
        var tmc = Tmc;
        int timeout = deadline.RemainingMilliseconds;
        var token = deadline.CancellationToken;
        if (async) return UsbErrorMapping.RunAsync(() => tmc.GoToLocalAsync(timeout, token));
        UsbErrorMapping.Run(() => tmc.GoToLocal(timeout));
        return default;
    }

    private ValueTask LocalLockoutAsync(Deadline deadline, bool async)
    {
        var tmc = Tmc;
        int timeout = deadline.RemainingMilliseconds;
        var token = deadline.CancellationToken;
        if (async) return UsbErrorMapping.RunAsync(() => tmc.LocalLockoutAsync(timeout, token));
        UsbErrorMapping.Run(() => tmc.LocalLockout(timeout));
        return default;
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
            var tmc = session.Tmc;
            int timeout = session.NewDeadline(ct).RemainingMilliseconds;
            if (async) await UsbErrorMapping.RunAsync(() => tmc.IndicatorPulseAsync(timeout, ct)).ConfigureAwait(false);
            else UsbErrorMapping.Run(() => tmc.IndicatorPulse(timeout));
            return true;
        }, cancellationToken, async).ConfigureAwait(false);

    // ------------------------------------------------------------------ interrupt endpoint (SRQ)

    private Action<UsbTmcInterrupt>? _interruptHandler;

    private protected override ValueTask OnEventEnabledAsync(EventType eventType, bool async)
    {
        if (InterruptInEndpoint < 0)
            throw new VisaException(VisaStatus.ErrorInvalidEvent, "The device has no interrupt-IN endpoint, so it cannot send service requests.");
        if (_interruptHandler is not null) return default;
        _interruptHandler = HandleInterrupt;
        Shared!.Subscribe(_interruptHandler);
        return default;
    }

    private protected override ValueTask OnEventDisabledAsync(EventType eventType, bool async)
    {
        if (IsEventEnabled(EventType.ServiceRequest) || IsEventEnabled(EventType.UsbInterrupt)) return default;
        var handler = Interlocked.Exchange(ref _interruptHandler, null);
        return handler is not null && Shared is { } shared ? shared.UnsubscribeAsync(handler, async) : default;
    }

    private void HandleInterrupt(UsbTmcInterrupt packet)
    {
        // Other USB488 notifications are status byte responses, which the client hands to their requester.
        if (packet.IsServiceRequest) RaiseEvent(new ServiceRequestEventArgs(packet.StatusByte));
        else if (!packet.IsUsb488Notification) RaiseEvent(new UsbInterruptEventArgs(packet.Data));
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

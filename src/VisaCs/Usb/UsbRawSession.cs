using VisaCs.Internal;
using VisaCs.Usb;

namespace VisaCs;

/// <summary>USB RAW session (<c>USB::VID::PID::SERIAL[::intf]::RAW</c>): bulk transfers with no USBTMC framing.</summary>
/// <remarks>
/// Without an interface number in the resource name, the session opens the first interface that has bulk
/// endpoints. A short bulk packet (shorter than the endpoint's packet size) ends a read, like END. Packets from the
/// interrupt-IN endpoint arrive as <see cref="EventType.UsbInterrupt"/> events.
/// </remarks>
public sealed class UsbRawSession : UsbSessionBase
{
    internal UsbRawSession(ResourceManager manager, UsbResourceName name) : base(manager, name)
    {
    }

    /// <inheritdoc/>
    public override bool Is4882Compliant => false;

    /// <inheritdoc/>
    public override IReadOnlyCollection<EventType> SupportedEventTypes { get; } = [EventType.UsbInterrupt];

    private Action<UsbTmcInterrupt>? _interruptHandler;

    private protected override bool IsPreferredInterface(UsbInterfaceInfo info) =>
        info.Endpoints.Any(e => e.Type == UsbTransferType.Bulk);

    private protected override ValueTask OnDeviceOpenedAsync(Deadline deadline, bool async)
    {
        if (BulkOutEndpoint < 0 && BulkInEndpoint < 0)
            throw new VisaException(VisaStatus.ErrorResourceNotFound, "The USB interface has no bulk endpoints.");
        return default;
    }

    /// <summary>One bulk transfer between the operations of other sessions to the device.</summary>
    private ValueTask<int> BulkAsync(int endpoint, Memory<byte> buffer, Deadline deadline, bool async)
    {
        int wait = deadline.RemainingMilliseconds;
        if (!async) return new(Tmc.RunExclusive(h => h.BulkTransfer((byte)endpoint, buffer.Span, ToUsbTimeout(deadline)), wait));
        return Tmc.RunExclusiveAsync((h, ct) => h.BulkTransferAsync((byte)endpoint, buffer, ToUsbTimeout(deadline), ct), wait,
            deadline.CancellationToken);
    }

    private protected override async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> data, bool end, Deadline deadline, bool async)
    {
        if (BulkOutEndpoint < 0) throw VisaException.NotSupported("Writing (no bulk-out endpoint)");
        var buffer = data.ToArray();
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await BulkAsync(BulkOutEndpoint, buffer.AsMemory(offset, Math.Min(1024 * 1024, buffer.Length - offset)), deadline, async)
                .ConfigureAwait(false);
            if (n <= 0) throw new VisaException(VisaStatus.ErrorIO, "USB bulk-out transfer failed.");
            offset += n;
        }
    }

    private protected override async ValueTask<FillResult> FillCoreAsync(Memory<byte> target, Deadline deadline, bool async)
    {
        if (BulkInEndpoint < 0) throw VisaException.NotSupported("Reading (no bulk-in endpoint)");
        int want = target.Length / BulkInPacketSize * BulkInPacketSize;
        if (want == 0)
        {
            // Never request less than one packet (that would overflow); use a temporary buffer.
            var temp = new byte[BulkInPacketSize];
            int r = await BulkAsync(BulkInEndpoint, temp, deadline, async).ConfigureAwait(false);
            int copy = Math.Min(r, target.Length);
            temp.AsSpan(0, copy).CopyTo(target.Span);
            return new FillResult(copy, r < BulkInPacketSize);
        }
        int n = await BulkAsync(BulkInEndpoint, target[..want], deadline, async).ConfigureAwait(false);
        return new FillResult(n, n % BulkInPacketSize != 0 || n == 0); // a short packet ends the message
    }

    private protected override async ValueTask ClearCoreAsync(Deadline deadline, bool async)
    {
        int wait = deadline.RemainingMilliseconds;
        Func<IUsbDeviceHandle, bool> clear = h =>
        {
            if (BulkOutEndpoint >= 0) h.ClearHalt((byte)BulkOutEndpoint);
            if (BulkInEndpoint >= 0) h.ClearHalt((byte)BulkInEndpoint);
            return true;
        };
        if (async) await Tmc.RunExclusiveAsync((h, _) => new ValueTask<bool>(clear(h)), wait, deadline.CancellationToken).ConfigureAwait(false);
        else Tmc.RunExclusive(clear, wait);
    }

    private protected override ValueTask OnEventEnabledAsync(EventType eventType, bool async)
    {
        if (InterruptInEndpoint < 0) throw new VisaException(VisaStatus.ErrorInvalidEvent, "The interface has no interrupt-IN endpoint.");
        if (_interruptHandler is not null) return default;
        _interruptHandler = packet => RaiseEvent(new UsbInterruptEventArgs(packet.Data));
        Shared!.Subscribe(_interruptHandler);
        return default;
    }

    private protected override ValueTask OnEventDisabledAsync(EventType eventType, bool async)
    {
        var handler = Interlocked.Exchange(ref _interruptHandler, null);
        return handler is not null && Shared is { } shared ? shared.UnsubscribeAsync(handler, async) : default;
    }

    private protected override async ValueTask CloseCoreAsync(bool async)
    {
        await OnEventDisabledAsync(EventType.UsbInterrupt, async).ConfigureAwait(false);
        await base.CloseCoreAsync(async).ConfigureAwait(false);
    }
}

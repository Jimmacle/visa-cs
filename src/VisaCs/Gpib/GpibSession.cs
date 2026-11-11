using VisaCs.Gpib;
using VisaCs.Internal;

namespace VisaCs;

/// <summary>
/// GPIB instrument session (<c>GPIB0::22::INSTR</c>). Service request events carry the status byte: while the event
/// is enabled the session waits for this device's request (RQS from the driver's automatic serial polling, or SRQ
/// followed by a serial poll of this device) without holding a thread.
/// </summary>
public sealed class GpibSession : GpibSessionBase
{
    private readonly GpibInstrumentResourceName _name;
    private readonly Lock _boardLock = new();
    private IGpibDevice? _device;
    private IGpibBoard? _board;

    internal GpibSession(ResourceManager manager, GpibInstrumentResourceName name) : base(manager, name) => _name = name;

    /// <summary>The device's primary address (VI_ATTR_GPIB_PRIMARY_ADDR).</summary>
    public int PrimaryAddress => _name.PrimaryAddress;

    /// <summary>The device's secondary address, if any (VI_ATTR_GPIB_SECONDARY_ADDR).</summary>
    public int? SecondaryAddress => _name.SecondaryAddress;

    private IGpibDevice Device
    {
        get
        {
            ThrowIfDisposed();
            return _device ?? throw new VisaException(VisaStatus.ErrorInvalidObject, "The session is not open.");
        }
    }

    private protected override IGpibIO IO => Device;

    /// <summary>The board, opened on first use for REN control and line states.</summary>
    private IGpibBoard Board
    {
        get
        {
            ThrowIfDisposed();
            lock (_boardLock) return _board ??= GpibErrorMapping.Call(() => Driver.OpenBoard(_name.Board));
        }
    }

    /// <summary>Re-address the device before every read and write (VI_ATTR_GPIB_READDR_EN).</summary>
    public bool ReaddressingEnabled
    {
        get => GpibErrorMapping.Call(() => Device.ReaddressingEnabled);
        set => GpibErrorMapping.Call(() => Device.ReaddressingEnabled = value);
    }

    /// <summary>Unaddress the device after every read and write (VI_ATTR_GPIB_UNADDR_EN).</summary>
    public bool UnaddressingEnabled
    {
        get => GpibErrorMapping.Call(() => Device.UnaddressingEnabled);
        set => GpibErrorMapping.Call(() => Device.UnaddressingEnabled = value);
    }

    /// <summary>State of the REN line (VI_ATTR_GPIB_REN_STATE).</summary>
    public LineState RenState => LineFrom(Board, GpibLines.Ren);

    private protected override ValueTask OpenCoreAsync(int connectTimeoutMs, CancellationToken cancellationToken, bool async)
    {
        _device = GpibErrorMapping.Call(() => Driver.OpenDevice(_name.Board, _name.PrimaryAddress, _name.SecondaryAddress));
        return default;
    }

    private protected override void CloseDescriptors()
    {
        if (Interlocked.Exchange(ref _device, null) is { } device) GpibErrorMapping.Call(device.Dispose);
        lock (_boardLock)
        {
            if (_board is { } board) GpibErrorMapping.Call(board.Dispose);
            _board = null;
        }
    }

    private protected override ValueTask<byte> ReadStatusByteCoreAsync(Deadline deadline, bool async)
    {
        var device = Device;
        int timeout = GpibTimeouts.Milliseconds(deadline);
        return async
            ? GpibErrorMapping.CallAsync(() => device.SerialPollAsync(timeout, deadline.CancellationToken))
            : new(GpibErrorMapping.Call(() => device.SerialPoll(timeout)));
    }

    private protected override ValueTask ClearCoreAsync(Deadline deadline, bool async)
    {
        var device = Device;
        int timeout = GpibTimeouts.Milliseconds(deadline);
        if (async) return GpibErrorMapping.CallAsync(() => device.ClearAsync(timeout, deadline.CancellationToken));
        GpibErrorMapping.Call(() => device.Clear(timeout));
        return default;
    }

    private protected override ValueTask TriggerCoreAsync(Deadline deadline, bool async)
    {
        var device = Device;
        int timeout = GpibTimeouts.Milliseconds(deadline);
        if (async) return GpibErrorMapping.CallAsync(() => device.TriggerAsync(timeout, deadline.CancellationToken));
        GpibErrorMapping.Call(() => device.Trigger(timeout));
        return default;
    }

    private protected override async ValueTask ControlRenCoreAsync(RemoteLocalMode mode, Deadline deadline, bool async)
    {
        // UNL, then this device's listen address (and secondary address).
        byte[] address = _name.SecondaryAddress is { } sad
            ? [GpibCommand.Unlisten, GpibCommand.Listen(_name.PrimaryAddress), GpibCommand.Secondary(sad)]
            : [GpibCommand.Unlisten, GpibCommand.Listen(_name.PrimaryAddress)];
        switch (mode)
        {
            case RemoteLocalMode.Deassert:
                SetRemoteEnable(false);
                break;
            case RemoteLocalMode.Assert:
                SetRemoteEnable(true);
                break;
            case RemoteLocalMode.DeassertGoToLocal:
                GpibErrorMapping.Call(Device.GoToLocal);
                SetRemoteEnable(false);
                break;
            case RemoteLocalMode.AssertAddress:
                SetRemoteEnable(true);
                await GpibTimeouts.SendCommandAsync(Board, address, deadline, async).ConfigureAwait(false);
                break;
            case RemoteLocalMode.AssertLocalLockout:
                SetRemoteEnable(true);
                await GpibTimeouts.SendCommandAsync(Board, new[] { GpibCommand.LocalLockout }, deadline, async).ConfigureAwait(false);
                break;
            case RemoteLocalMode.AssertAddressLocalLockout:
                SetRemoteEnable(true);
                await GpibTimeouts.SendCommandAsync(Board, (byte[])[.. address, GpibCommand.LocalLockout], deadline, async).ConfigureAwait(false);
                break;
            case RemoteLocalMode.AddressGoToLocal:
                GpibErrorMapping.Call(Device.GoToLocal);
                break;
        }
    }

    private void SetRemoteEnable(bool asserted)
    {
        var board = Board;
        GpibErrorMapping.Call(() => board.SetRemoteEnable(asserted));
    }

    private protected override Func<CancellationToken, ValueTask<ServiceRequestEventArgs?>> CreateServiceRequestWait()
    {
        var device = Device;
        return async token =>
            await GpibErrorMapping.CallAsync(() => device.WaitForServiceRequestAsync(1000, token)).ConfigureAwait(false) is { } stb
                ? new ServiceRequestEventArgs(stb)
                : null;
    }

    private protected override void RegisterAttributes(AttributeTable table)
    {
        base.RegisterAttributes(table);
        table.Add(VisaAttribute.GpibPrimaryAddress, () => PrimaryAddress);
        table.Add(VisaAttribute.GpibSecondaryAddress, () => SecondaryAddress ?? 0xFFFF);
        table.Add(VisaAttribute.GpibReaddressingEnabled, () => ReaddressingEnabled, v => ReaddressingEnabled = v);
        table.Add(VisaAttribute.GpibUnaddressingEnabled, () => UnaddressingEnabled, v => UnaddressingEnabled = v);
        table.Add(VisaAttribute.GpibRenState, () => RenState);
    }
}

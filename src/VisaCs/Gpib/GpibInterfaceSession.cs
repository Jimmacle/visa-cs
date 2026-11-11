using System.Diagnostics;
using VisaCs.Gpib;
using VisaCs.Internal;

namespace VisaCs;

/// <summary>
/// GPIB interface (board) session (<c>GPIB0::INTFC</c>): the board as controller. Reads and writes are board level, so
/// address the talker and listeners with <see cref="SendCommand"/> first.
/// </summary>
/// <remarks>
/// A service request event is raised when SRQ becomes asserted: serial-poll the requesting device to release SRQ.
/// While SRQ stays asserted the event is raised again once a second, because SRQ is sampled and a release that is
/// followed quickly by a new request cannot be told apart from no release. The board sees SRQ only while the driver's
/// automatic serial polling is off (NI-488.2 reports board SRQI only then).
/// </remarks>
public sealed class GpibInterfaceSession : GpibSessionBase
{
    /// <summary>While SRQ stays asserted after an event, how often to check whether it was released.</summary>
    private const int SrqReleaseCheckMs = 20;

    /// <summary>While SRQ stays asserted after an event, when to raise the event again.</summary>
    private const int SrqRepeatMs = 1000;

    private readonly GpibInterfaceResourceName _name;
    private IGpibBoard? _board;

    internal GpibInterfaceSession(ResourceManager manager, GpibInterfaceResourceName name) : base(manager, name) => _name = name;

    private IGpibBoard Board
    {
        get
        {
            ThrowIfDisposed();
            return _board ?? throw new VisaException(VisaStatus.ErrorInvalidObject, "The session is not open.");
        }
    }

    private protected override IGpibIO IO => Board;

    /// <summary>The board's primary address, 0 to 30 (VI_ATTR_GPIB_PRIMARY_ADDR).</summary>
    public int PrimaryAddress
    {
        get => GpibErrorMapping.Call(() => Board.PrimaryAddress);
        set => GpibErrorMapping.Call(() => Board.PrimaryAddress = value);
    }

    /// <summary>The board's secondary address, 0 to 30, or null for none (VI_ATTR_GPIB_SECONDARY_ADDR).</summary>
    public int? SecondaryAddress
    {
        get => GpibErrorMapping.Call(() => Board.SecondaryAddress);
        set => GpibErrorMapping.Call(() => Board.SecondaryAddress = value);
    }

    /// <summary>State of the ATN line (VI_ATTR_GPIB_ATN_STATE).</summary>
    public LineState AtnState => LineFrom(Board, GpibLines.Atn);

    /// <summary>State of the SRQ line (VI_ATTR_GPIB_SRQ_STATE).</summary>
    public LineState SrqState => LineFrom(Board, GpibLines.Srq);

    /// <summary>State of the REN line (VI_ATTR_GPIB_REN_STATE).</summary>
    public LineState RenState => LineFrom(Board, GpibLines.Ren);

    /// <summary>State of the NDAC line (VI_ATTR_GPIB_NDAC_STATE).</summary>
    public LineState NdacState => LineFrom(Board, GpibLines.Ndac);

    /// <summary>True if the board is controller in charge (VI_ATTR_GPIB_CIC_STATE).</summary>
    public bool IsControllerInCharge => GpibErrorMapping.Call(() => Board.IsControllerInCharge);

    /// <summary>True if the board is the system controller (VI_ATTR_GPIB_SYS_CNTRL_STATE).</summary>
    public bool IsSystemController => GpibErrorMapping.Call(() => Board.IsSystemController);

    /// <summary>Whether the board is addressed to talk or to listen (VI_ATTR_GPIB_ADDR_STATE).</summary>
    public GpibAddressState AddressState => GpibErrorMapping.Call(() => Board.AddressState);

    private protected override ValueTask OpenCoreAsync(int connectTimeoutMs, CancellationToken cancellationToken, bool async)
    {
        _board = GpibErrorMapping.Call(() => Driver.OpenBoard(_name.Board));
        return default;
    }

    private protected override void CloseDescriptors()
    {
        if (Interlocked.Exchange(ref _board, null) is { } board) GpibErrorMapping.Call(board.Dispose);
    }

    private protected override ValueTask ClearCoreAsync(Deadline deadline, bool async) =>
        GpibTimeouts.SendCommandAsync(Board, new[] { GpibCommand.DeviceClear }, deadline, async);

    private protected override ValueTask TriggerCoreAsync(Deadline deadline, bool async) =>
        GpibTimeouts.SendCommandAsync(Board, new[] { GpibCommand.GroupExecuteTrigger }, deadline, async);

    private protected override async ValueTask ControlRenCoreAsync(RemoteLocalMode mode, Deadline deadline, bool async)
    {
        switch (mode)
        {
            case RemoteLocalMode.Deassert:
            case RemoteLocalMode.DeassertGoToLocal:
                SetRemoteEnable(false);
                break;
            case RemoteLocalMode.AssertLocalLockout:
            case RemoteLocalMode.AssertAddressLocalLockout:
                SetRemoteEnable(true);
                await GpibTimeouts.SendCommandAsync(Board, new[] { GpibCommand.LocalLockout }, deadline, async).ConfigureAwait(false);
                break;
            case RemoteLocalMode.AddressGoToLocal:
                // GTL to the devices that are addressed to listen.
                await GpibTimeouts.SendCommandAsync(Board, new[] { GpibCommand.GoToLocal }, deadline, async).ConfigureAwait(false);
                break;
            default:
                SetRemoteEnable(true);
                break;
        }
    }

    /// <summary>Sends GPIB command bytes with ATN asserted (viGpibCommand); <see cref="GpibCommand"/> names them.</summary>
    public void SendCommand(ReadOnlySpan<byte> commandBytes) =>
        AsyncIO.Complete(SendCommandAsync(commandBytes.ToArray(), CancellationToken.None, async: false));

    /// <summary>Sends GPIB command bytes with ATN asserted (viGpibCommand); <see cref="GpibCommand"/> names them.</summary>
    public Task SendCommandAsync(ReadOnlyMemory<byte> commandBytes, CancellationToken cancellationToken = default) =>
        SendCommandAsync(commandBytes, cancellationToken, async: true).AsTask();

    private async ValueTask SendCommandAsync(ReadOnlyMemory<byte> commandBytes, CancellationToken cancellationToken, bool async) =>
        await ExecuteAsync(commandBytes, static async (s, bytes, ct, async) =>
        {
            var session = (GpibInterfaceSession)s;
            await GpibTimeouts.SendCommandAsync(session.Board, bytes, session.NewDeadline(ct), async).ConfigureAwait(false);
            return true;
        }, cancellationToken, async).ConfigureAwait(false);

    /// <summary>Pulses IFC, which makes the board controller in charge (viGpibSendIFC). A short call; there is no asynchronous form.</summary>
    public void SendInterfaceClear() => Execute(() => GpibErrorMapping.Call(Board.SendInterfaceClear));

    /// <summary>Controls the ATN line (viGpibControlATN). A short call; there is no asynchronous form.</summary>
    public void ControlAtn(GpibAtnMode mode) => Execute(() => GpibErrorMapping.Call(() => Board.ControlAtn(mode)));

    /// <summary>
    /// Passes controller-in-charge to another device (viGpibPassControl); the device must take control within the
    /// session timeout.
    /// </summary>
    public void PassControl(int primaryAddress, int? secondaryAddress = null) =>
        Execute(() =>
        {
            var board = Board;
            int timeout = GpibTimeouts.Milliseconds(NewDeadline());
            GpibErrorMapping.Call(() => board.PassControl(primaryAddress, secondaryAddress, timeout));
        });

    private void SetRemoteEnable(bool asserted)
    {
        var board = Board;
        GpibErrorMapping.Call(() => board.SetRemoteEnable(asserted));
    }

    private protected override Func<CancellationToken, ValueTask<ServiceRequestEventArgs?>> CreateServiceRequestWait()
    {
        var board = Board;
        long reportedAt = 0; // when SRQ was reported, if it has not been seen released since (a Stopwatch timestamp)
        return async token =>
        {
            if (reportedAt != 0)
            {
                if (await GpibErrorMapping.CallAsync(() => board.WaitForServiceRequestAsync(0, token)).ConfigureAwait(false))
                {
                    if (Stopwatch.GetElapsedTime(reportedAt).TotalMilliseconds < SrqRepeatMs)
                    {
                        await Task.Delay(SrqReleaseCheckMs, token).ConfigureAwait(false);
                        return null;
                    }
                    reportedAt = Stopwatch.GetTimestamp();
                    return new ServiceRequestEventArgs();
                }
                reportedAt = 0;
            }
            if (!await GpibErrorMapping.CallAsync(() => board.WaitForServiceRequestAsync(1000, token)).ConfigureAwait(false)) return null;
            reportedAt = Stopwatch.GetTimestamp();
            return new ServiceRequestEventArgs();
        };
    }

    private protected override void RegisterAttributes(AttributeTable table)
    {
        base.RegisterAttributes(table);
        table.Add(VisaAttribute.GpibPrimaryAddress, () => PrimaryAddress, v => PrimaryAddress = v);
        table.Add(VisaAttribute.GpibSecondaryAddress, () => SecondaryAddress ?? 0xFFFF,
            v => SecondaryAddress = v is < 0 or > 30 ? null : v);
        table.Add(VisaAttribute.GpibAtnState, () => AtnState);
        table.Add(VisaAttribute.GpibSrqState, () => SrqState);
        table.Add(VisaAttribute.GpibRenState, () => RenState);
        table.Add(VisaAttribute.GpibNdacState, () => NdacState);
        table.Add(VisaAttribute.GpibCicState, () => IsControllerInCharge);
        table.Add(VisaAttribute.GpibSystemControllerState, () => IsSystemController);
        table.Add(VisaAttribute.GpibAddressState, () => AddressState);
    }
}

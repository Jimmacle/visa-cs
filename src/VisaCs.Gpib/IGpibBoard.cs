namespace VisaCs.Gpib;

/// <summary>
/// The interface board itself (a GPIB INTFC resource), opened with <see cref="IGpibDriver.OpenBoard"/>. Reads and
/// writes are board level: address the talker and listeners with <see cref="SendCommand"/> first. The transfer,
/// timeout, cancellation and disposal rules are those of <see cref="IGpibIO"/>.
/// </summary>
/// <remarks>
/// All objects opened for one board share its descriptor and settings (timeout, termination character, addresses).
/// Disposing one does not take the board offline.
/// </remarks>
public interface IGpibBoard : IGpibIO
{
    /// <summary>Sends command bytes (for example UNL 0x3F, LAD 0x20 + address) with ATN asserted. The board must be controller in charge.</summary>
    void SendCommand(ReadOnlySpan<byte> commandBytes, int timeoutMilliseconds);

    /// <summary>Sends command bytes with ATN asserted; see <see cref="SendCommand"/>.</summary>
    ValueTask SendCommandAsync(ReadOnlyMemory<byte> commandBytes, int timeoutMilliseconds, CancellationToken cancellationToken = default);

    /// <summary>Pulses IFC (interface clear), which makes the board controller in charge. The board must be system controller.</summary>
    void SendInterfaceClear();

    /// <summary>Asserts or deasserts REN (remote enable). The board must be system controller.</summary>
    void SetRemoteEnable(bool asserted);

    /// <summary>Asserts or deasserts ATN.</summary>
    void ControlAtn(GpibAtnMode mode);

    /// <summary>Passes controller-in-charge to the device at the given address, which must accept it within the timeout.</summary>
    void PassControl(int primaryAddress, int? secondaryAddress, int timeoutMilliseconds);

    /// <summary>The states of the bus control lines.</summary>
    GpibLineStatus GetLines();

    /// <summary>The board's own primary address (0 to 30).</summary>
    int PrimaryAddress { get; set; }

    /// <summary>The board's own secondary address (0 to 30), or null for none.</summary>
    int? SecondaryAddress { get; set; }

    /// <summary>True if the board is the system controller.</summary>
    bool IsSystemController { get; }

    /// <summary>True if the board is controller in charge.</summary>
    bool IsControllerInCharge { get; }

    /// <summary>Whether the board is addressed to talk or to listen.</summary>
    GpibAddressState AddressState { get; }

    /// <summary>
    /// Waits up to the timeout until SRQ is asserted (0 checks once). Returns true at once while it stays asserted:
    /// serial-poll the requesting device to release it.
    /// </summary>
    /// <remarks>
    /// <see cref="Ni488Driver"/> sees SRQ through the board's SRQI status, which NI-488.2 reports only while automatic
    /// serial polling (IbcAUTOPOLL) is off; with autopolling the driver polls the device itself. Wait on the device
    /// instead (<see cref="IGpibDevice.WaitForServiceRequest"/>), which works either way.
    /// </remarks>
    bool WaitForServiceRequest(int timeoutMilliseconds);

    /// <summary>Waits up to the timeout until SRQ is asserted; see <see cref="WaitForServiceRequest"/>.</summary>
    ValueTask<bool> WaitForServiceRequestAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default);
}

namespace VisaCs.Gpib;

/// <summary>
/// A device on the bus (a GPIB INSTR resource), opened with <see cref="IGpibDriver.OpenDevice"/>. The transfer,
/// timeout, cancellation and disposal rules are those of <see cref="IGpibIO"/>.
/// </summary>
public interface IGpibDevice : IGpibIO
{
    /// <summary>Serial-polls the device and returns its status byte (bit 6 set: the device requested service).</summary>
    byte SerialPoll(int timeoutMilliseconds);

    /// <summary>Serial-polls the device and returns its status byte; see <see cref="SerialPoll"/>.</summary>
    ValueTask<byte> SerialPollAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default);

    /// <summary>Sends Selected Device Clear (SDC) to the device.</summary>
    void Clear(int timeoutMilliseconds);

    /// <summary>Sends Selected Device Clear (SDC) to the device.</summary>
    ValueTask ClearAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default);

    /// <summary>Sends Group Execute Trigger (GET) to the device.</summary>
    void Trigger(int timeoutMilliseconds);

    /// <summary>Sends Group Execute Trigger (GET) to the device.</summary>
    ValueTask TriggerAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default);

    /// <summary>Sends Go To Local (GTL): the device returns to front-panel control.</summary>
    void GoToLocal();

    /// <summary>
    /// Whether the driver addresses the device before every read and write; when false it skips addressing while the
    /// device is still addressed. The initial value is the driver's.
    /// </summary>
    bool ReaddressingEnabled { get; set; }

    /// <summary>Whether the driver unaddresses the device (UNT, UNL) after every read and write. The initial value is the driver's.</summary>
    bool UnaddressingEnabled { get; set; }

    /// <summary>
    /// Waits until this device requests service and returns its status byte, or null when the timeout passes first
    /// (0 checks once without waiting).
    /// </summary>
    /// <remarks>
    /// A request is recognized by RQS, which the driver's automatic serial polling sets, or, while SRQ is asserted, by a
    /// serial poll of this device whose status byte has bit 6 set. While another device holds SRQ, this device is
    /// polled at most every 100 ms.
    /// </remarks>
    byte? WaitForServiceRequest(int timeoutMilliseconds);

    /// <summary>Waits until this device requests service; see <see cref="WaitForServiceRequest"/>.</summary>
    ValueTask<byte?> WaitForServiceRequestAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default);
}

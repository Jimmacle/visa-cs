namespace VisaCs.Gpib;

/// <summary>
/// Addressing state of a GPIB board (VI_ATTR_GPIB_ADDR_STATE). In namespace <c>VisaCs</c> because the VISA interface
/// session uses it too.
/// </summary>
public enum GpibAddressState
{
    /// <summary>Neither talker nor listener (VI_GPIB_UNADDRESSED).</summary>
    Unaddressed = 0,
    /// <summary>Addressed to talk (VI_GPIB_TALKER).</summary>
    Talker = 1,
    /// <summary>Addressed to listen (VI_GPIB_LISTENER).</summary>
    Listener = 2,
}

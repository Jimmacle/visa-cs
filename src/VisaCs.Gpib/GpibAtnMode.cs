namespace VisaCs;

/// <summary>
/// ATN line control for <see cref="Gpib.IGpibBoard.ControlAtn"/> and <c>GpibInterfaceSession.ControlAtn</c>
/// (VI_GPIB_ATN_*). In namespace <c>VisaCs</c> because the VISA interface session uses it too.
/// </summary>
public enum GpibAtnMode
{
    /// <summary>Deassert ATN: the board goes to standby so devices exchange data (ibgts 0).</summary>
    Deassert = 0,
    /// <summary>Assert ATN synchronously, without corrupting a data byte in transfer (take control, ibcac 1).</summary>
    Assert = 1,
    /// <summary>
    /// Deassert ATN and take part in the data handshake as an acceptor without being addressed to listen
    /// (shadow handshake, ibgts 1).
    /// </summary>
    DeassertHandshake = 2,
    /// <summary>Assert ATN at once, even in the middle of a data byte (ibcac 0).</summary>
    AssertImmediate = 3,
}

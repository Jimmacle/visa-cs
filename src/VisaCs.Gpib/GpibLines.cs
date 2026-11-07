namespace VisaCs.Gpib;

/// <summary>GPIB bus lines, as reported by <see cref="IGpibBoard.GetLines"/> (the bit values of NI-488.2's iblines).</summary>
[Flags]
public enum GpibLines
{
    /// <summary>No line.</summary>
    None = 0,
    /// <summary>Data valid (DAV).</summary>
    Dav = 0x01,
    /// <summary>Not data accepted (NDAC).</summary>
    Ndac = 0x02,
    /// <summary>Not ready for data (NRFD).</summary>
    Nrfd = 0x04,
    /// <summary>Interface clear (IFC).</summary>
    Ifc = 0x08,
    /// <summary>Remote enable (REN).</summary>
    Ren = 0x10,
    /// <summary>Service request (SRQ).</summary>
    Srq = 0x20,
    /// <summary>Attention (ATN).</summary>
    Atn = 0x40,
    /// <summary>End or identify (EOI).</summary>
    Eoi = 0x80,
}

/// <summary>States of the GPIB control lines.</summary>
/// <param name="Monitored">The lines the board can sense; the others are not reported.</param>
/// <param name="Asserted">The monitored lines that are asserted.</param>
public readonly record struct GpibLineStatus(GpibLines Monitored, GpibLines Asserted)
{
    /// <summary>True if <paramref name="line"/> is asserted, false if not, null if the board cannot sense it.</summary>
    public bool? IsAsserted(GpibLines line) => (Monitored & line) == 0 ? null : (Asserted & line) != 0;
}

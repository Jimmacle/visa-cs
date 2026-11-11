namespace VisaCs.Gpib.Native;

/// <summary>Constants of the NI-488.2 C API (ni4882.h): ibsta bits, iberr codes, ibconfig/ibask options.</summary>
internal static class Ni488
{
    // ibsta bits
    public const int ERR = 0x8000, TIMO = 0x4000, END = 0x2000, SRQI = 0x1000, RQS = 0x0800, CMPL = 0x0100;
    public const int CIC = 0x0020, ATN = 0x0010, TACS = 0x0008, LACS = 0x0004;

    // iberr codes
    public const int EDVR = 0, ECIC = 1, ENOL = 2, EADR = 3, EARG = 4, ESAC = 5, EABO = 6, ENEB = 7, EDMA = 8, EOIP = 10,
        ECAP = 11, EFSO = 12, EBUS = 14, ESTB = 15, ESRQ = 16, ETAB = 20, ELCK = 21, EARM = 22, EHDL = 23, EWIP = 26,
        ERST = 27, EPWR = 28;

    // ibconfig / ibask options
    public const int IbcPAD = 0x1, IbcSAD = 0x2, IbcEOT = 0x4, IbcREADDR = 0x6, IbcSC = 0xA, IbcEOSrd = 0xC,
        IbcEOScmp = 0xE, IbcEOSchar = 0xF, IbcUnAddr = 0x1B;

    /// <summary>The secondary address argument of ibdev/ibln/ibconfig for "none"; addresses are 0x60 + n.</summary>
    public const int NoSad = 0, SadBase = 0x60;

    /// <summary>Milliseconds of each ibtmo code (T10us = 1 ... T1000s = 17); 0 is TNONE (no limit).</summary>
    private static readonly int[] TimeoutTable =
        [0, 1, 1, 1, 1, 1, 3, 10, 30, 100, 300, 1000, 3000, 10000, 30000, 100000, 300000, 1000000];

    /// <summary>
    /// The ibtmo code for a timeout: TNONE when negative; T10us, the shortest code, for 0 (NI-488.2 has no code for
    /// "don't wait": TNONE means no limit); otherwise the shortest code of at least that many milliseconds, from
    /// T10ms up to T1000s.
    /// </summary>
    public static int TimeoutCode(int milliseconds)
    {
        if (milliseconds < 0) return 0;
        if (milliseconds == 0) return 1;
        for (int code = 7; code < TimeoutTable.Length; code++)
            if (TimeoutTable[code] >= milliseconds) return code;
        return TimeoutTable.Length - 1;
    }

    /// <summary>The mnemonic and meaning of an iberr code, for messages.</summary>
    public static string Describe(int iberr) => iberr switch
    {
        EDVR => "EDVR (system error)",
        ECIC => "ECIC (the board is not controller in charge)",
        ENOL => "ENOL (no listeners on the bus)",
        EADR => "EADR (the board is not addressed correctly)",
        EARG => "EARG (invalid argument)",
        ESAC => "ESAC (the board is not system controller)",
        EABO => "EABO (the I/O was aborted or timed out)",
        ENEB => "ENEB (no such GPIB board)",
        EDMA => "EDMA (DMA error)",
        EOIP => "EOIP (asynchronous I/O in progress)",
        ECAP => "ECAP (not supported by the driver or board)",
        EFSO => "EFSO (file system error)",
        EBUS => "EBUS (bus error while sending command bytes)",
        ESTB => "ESTB (serial poll status byte queue overflow)",
        ESRQ => "ESRQ (SRQ stuck asserted)",
        ETAB => "ETAB (table problem)",
        ELCK => "ELCK (the interface is locked)",
        EARM => "EARM (ibnotify could not rearm)",
        EHDL => "EHDL (invalid descriptor)",
        EWIP => "EWIP (wait already in progress)",
        ERST => "ERST (the event was reset)",
        EPWR => "EPWR (the interface lost power)",
        _ => $"iberr {iberr}",
    };
}

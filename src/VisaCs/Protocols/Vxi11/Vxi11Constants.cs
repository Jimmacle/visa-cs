namespace VisaCs.Protocols.Vxi11;

/// <summary>VXI-11 (TCP/IP Instrument Protocol) constants.</summary>
internal static class Vxi11Constants
{
    public const uint CoreProgram = 0x0607AF;
    public const uint AbortProgram = 0x0607B0;
    public const uint InterruptProgram = 0x0607B1;
    public const uint Version = 1;

    public const uint ProcDeviceAbort = 1;
    public const uint ProcCreateLink = 10;
    public const uint ProcDeviceWrite = 11;
    public const uint ProcDeviceRead = 12;
    public const uint ProcDeviceReadStb = 13;
    public const uint ProcDeviceTrigger = 14;
    public const uint ProcDeviceClear = 15;
    public const uint ProcDeviceRemote = 16;
    public const uint ProcDeviceLocal = 17;
    public const uint ProcDeviceLock = 18;
    public const uint ProcDeviceUnlock = 19;
    public const uint ProcDeviceEnableSrq = 20;
    public const uint ProcDeviceDocmd = 22;
    public const uint ProcDestroyLink = 23;
    public const uint ProcCreateIntrChan = 25;
    public const uint ProcDestroyIntrChan = 26;
    public const uint ProcDeviceIntrSrq = 30;

    public const int FlagWaitLock = 0x01;
    public const int FlagEnd = 0x08;
    public const int FlagTermCharSet = 0x80;

    public const int ReasonRequestCount = 0x01;
    public const int ReasonChr = 0x02;
    public const int ReasonEnd = 0x04;

    public const uint AddrFamilyTcp = 0;

    public const int ErrNone = 0;
    public const int ErrSyntax = 1;
    public const int ErrDeviceNotAccessible = 3;
    public const int ErrInvalidLinkIdentifier = 4;
    public const int ErrParameter = 5;
    public const int ErrChannelNotEstablished = 6;
    public const int ErrOperationNotSupported = 8;
    public const int ErrOutOfResources = 9;
    public const int ErrDeviceLocked = 11;
    public const int ErrNoLockHeld = 12;
    public const int ErrIoTimeout = 15;
    public const int ErrIoError = 17;
    public const int ErrInvalidAddress = 21;
    public const int ErrAbort = 23;
    public const int ErrChannelAlreadyEstablished = 29;

    public static string Describe(int error) => error switch
    {
        ErrSyntax => "syntax error",
        ErrDeviceNotAccessible => "device not accessible",
        ErrInvalidLinkIdentifier => "invalid link identifier",
        ErrParameter => "parameter error",
        ErrChannelNotEstablished => "channel not established",
        ErrOperationNotSupported => "operation not supported",
        ErrOutOfResources => "out of resources",
        ErrDeviceLocked => "device locked by another link",
        ErrNoLockHeld => "no lock held by this link",
        ErrIoTimeout => "I/O timeout",
        ErrIoError => "I/O error",
        ErrInvalidAddress => "invalid address",
        ErrAbort => "abort",
        ErrChannelAlreadyEstablished => "channel already established",
        _ => $"error {error}",
    };

    public static VisaException ToException(int error, string operation)
    {
        string message = $"VXI-11 {operation} failed: {Describe(error)} ({error}).";
        return error switch
        {
            ErrIoTimeout => new VisaTimeoutException(message),
            ErrDeviceLocked => new VisaException(VisaStatus.ErrorResourceLocked, message),
            ErrNoLockHeld => new VisaException(VisaStatus.ErrorSessionNotLocked, message),
            ErrAbort => new VisaException(VisaStatus.ErrorAbort, message),
            ErrOperationNotSupported => new VisaException(VisaStatus.ErrorOperationNotSupported, message),
            ErrInvalidLinkIdentifier or ErrChannelNotEstablished => new VisaException(VisaStatus.ErrorConnectionLost, message),
            ErrDeviceNotAccessible or ErrInvalidAddress => new VisaException(VisaStatus.ErrorResourceNotFound, message),
            ErrParameter or ErrSyntax => new VisaException(VisaStatus.ErrorInvalidParameter, message),
            ErrOutOfResources => new VisaException(VisaStatus.ErrorAllocation, message),
            _ => new VisaException(VisaStatus.ErrorIO, message),
        };
    }
}

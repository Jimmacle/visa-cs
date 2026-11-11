using VisaCs.Internal;

namespace VisaCs.Lan;

/// <summary>VXI-11 (TCP/IP Instrument Protocol) RPC program, procedure and flag numbers, for servers and custom clients.</summary>
public static class Vxi11Protocol
{
    /// <summary>The core channel program (DEVICE_CORE).</summary>
    public const uint CoreProgram = 0x0607AF;
    /// <summary>The abort channel program (DEVICE_ASYNC).</summary>
    public const uint AbortProgram = 0x0607B0;
    /// <summary>The interrupt channel program (DEVICE_INTR), served by the client.</summary>
    public const uint InterruptProgram = 0x0607B1;
    /// <summary>The version of all three programs.</summary>
    public const uint Version = 1;

    /// <summary>device_abort (abort channel).</summary>
    public const uint ProcDeviceAbort = 1;
    /// <summary>create_link.</summary>
    public const uint ProcCreateLink = 10;
    /// <summary>device_write.</summary>
    public const uint ProcDeviceWrite = 11;
    /// <summary>device_read.</summary>
    public const uint ProcDeviceRead = 12;
    /// <summary>device_readstb.</summary>
    public const uint ProcDeviceReadStb = 13;
    /// <summary>device_trigger.</summary>
    public const uint ProcDeviceTrigger = 14;
    /// <summary>device_clear.</summary>
    public const uint ProcDeviceClear = 15;
    /// <summary>device_remote.</summary>
    public const uint ProcDeviceRemote = 16;
    /// <summary>device_local.</summary>
    public const uint ProcDeviceLocal = 17;
    /// <summary>device_lock.</summary>
    public const uint ProcDeviceLock = 18;
    /// <summary>device_unlock.</summary>
    public const uint ProcDeviceUnlock = 19;
    /// <summary>device_enable_srq.</summary>
    public const uint ProcDeviceEnableSrq = 20;
    /// <summary>device_docmd.</summary>
    public const uint ProcDeviceDocmd = 22;
    /// <summary>destroy_link.</summary>
    public const uint ProcDestroyLink = 23;
    /// <summary>create_intr_chan.</summary>
    public const uint ProcCreateIntrChan = 25;
    /// <summary>destroy_intr_chan.</summary>
    public const uint ProcDestroyIntrChan = 26;
    /// <summary>device_intr_srq (interrupt channel).</summary>
    public const uint ProcDeviceIntrSrq = 30;

    /// <summary>Operation flag: wait for the lock (waitlock).</summary>
    public const int FlagWaitLock = 0x01;
    /// <summary>Operation flag: the write ends with END.</summary>
    public const int FlagEnd = 0x08;
    /// <summary>Operation flag: the read ends on the termination character.</summary>
    public const int FlagTermCharSet = 0x80;

    /// <summary>Address family of create_intr_chan for TCP.</summary>
    public const uint AddrFamilyTcp = 0;
}

/// <summary>VXI-11 error codes (<c>Device_ErrorCode</c>).</summary>
public enum Vxi11Error
{
    /// <summary>No error.</summary>
    None = 0,
    /// <summary>Syntax error.</summary>
    Syntax = 1,
    /// <summary>Device not accessible.</summary>
    DeviceNotAccessible = 3,
    /// <summary>Invalid link identifier.</summary>
    InvalidLinkIdentifier = 4,
    /// <summary>Parameter error.</summary>
    Parameter = 5,
    /// <summary>Channel not established.</summary>
    ChannelNotEstablished = 6,
    /// <summary>Operation not supported.</summary>
    OperationNotSupported = 8,
    /// <summary>Out of resources.</summary>
    OutOfResources = 9,
    /// <summary>Device locked by another link.</summary>
    DeviceLocked = 11,
    /// <summary>No lock held by this link.</summary>
    NoLockHeld = 12,
    /// <summary>I/O timeout.</summary>
    IOTimeout = 15,
    /// <summary>I/O error.</summary>
    IOError = 17,
    /// <summary>Invalid address.</summary>
    InvalidAddress = 21,
    /// <summary>Abort.</summary>
    Abort = 23,
    /// <summary>Channel already established.</summary>
    ChannelAlreadyEstablished = 29,
}

/// <summary>Why a device_read ended.</summary>
[Flags]
public enum Vxi11ReadReasons
{
    /// <summary>None: the read returned data without reaching the end of a message.</summary>
    None = 0,
    /// <summary>The requested number of bytes was read.</summary>
    RequestCount = 0x01,
    /// <summary>The termination character was read.</summary>
    TerminationCharacter = 0x02,
    /// <summary>END was received: the message is complete.</summary>
    End = 0x04,
}

/// <summary>Result of <see cref="Vxi11Client.Write"/>.</summary>
/// <param name="Error">The device's error code.</param>
/// <param name="Size">Number of bytes the device accepted.</param>
public readonly record struct Vxi11WriteResult(Vxi11Error Error, int Size);

/// <summary>Result of <see cref="Vxi11Client.Read"/>.</summary>
/// <param name="Error">The device's error code.</param>
/// <param name="Reason">Why the read ended.</param>
/// <param name="Data">The data, a slice of the reply buffer owned by the caller.</param>
public readonly record struct Vxi11ReadResult(Vxi11Error Error, Vxi11ReadReasons Reason, ReadOnlyMemory<byte> Data);

/// <summary>Result of <see cref="Vxi11Client.ReadStatusByte"/>.</summary>
/// <param name="Error">The device's error code.</param>
/// <param name="StatusByte">The status byte.</param>
public readonly record struct Vxi11StatusByteResult(Vxi11Error Error, byte StatusByte);

/// <summary>Result of <see cref="Vxi11Client.DoCommand"/>.</summary>
/// <param name="Error">The device's error code.</param>
/// <param name="Data">The <c>data_out</c> bytes.</param>
public readonly record struct Vxi11DocmdResult(Vxi11Error Error, byte[] Data);

/// <summary>Descriptions of VXI-11 error codes and the exceptions that report them.</summary>
public static class Vxi11Errors
{
    /// <summary>A short description of <paramref name="error"/>, e.g. "device locked by another link".</summary>
    public static string Describe(Vxi11Error error) => error switch
    {
        Vxi11Error.Syntax => "syntax error",
        Vxi11Error.DeviceNotAccessible => "device not accessible",
        Vxi11Error.InvalidLinkIdentifier => "invalid link identifier",
        Vxi11Error.Parameter => "parameter error",
        Vxi11Error.ChannelNotEstablished => "channel not established",
        Vxi11Error.OperationNotSupported => "operation not supported",
        Vxi11Error.OutOfResources => "out of resources",
        Vxi11Error.DeviceLocked => "device locked by another link",
        Vxi11Error.NoLockHeld => "no lock held by this link",
        Vxi11Error.IOTimeout => "I/O timeout",
        Vxi11Error.IOError => "I/O error",
        Vxi11Error.InvalidAddress => "invalid address",
        Vxi11Error.Abort => "abort",
        Vxi11Error.ChannelAlreadyEstablished => "channel already established",
        _ => $"error {(int)error}",
    };

    /// <summary>
    /// The exception for a failed <paramref name="operation"/> (e.g. "device_write"): <see cref="VisaTimeoutException"/>
    /// for an I/O timeout, otherwise a <see cref="VisaException"/> with the matching VISA status.
    /// </summary>
    public static VisaException ToException(Vxi11Error error, string operation) =>
        LanErrors.Vxi11(error, $"VXI-11 {operation} failed: {Describe(error)} ({(int)error}).");
}

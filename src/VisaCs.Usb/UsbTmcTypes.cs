namespace VisaCs.Usb;

/// <summary>The result of one USBTMC Bulk-IN transfer.</summary>
/// <param name="Count">Number of bytes received.</param>
/// <param name="End">True when the transfer ends the message (EOM set; for vendor-specific transfers, a short transfer).</param>
public readonly record struct UsbTmcReadResult(int Count, bool End);

/// <summary>A packet from the interrupt-IN endpoint of a USBTMC interface.</summary>
/// <param name="data">The packet as received.</param>
public sealed class UsbTmcInterrupt(byte[] data)
{
    /// <summary>The packet as received. Every handler gets the same array; do not change it.</summary>
    public byte[] Data { get; } = data;

    /// <summary>True for a USB488 notification: at least two bytes, with bit 7 of the first byte set.</summary>
    public bool IsUsb488Notification => Data.Length >= 2 && (Data[0] & 0x80) != 0;

    /// <summary>True for a USB488 service request (SRQ) notification: [0x81, status byte].</summary>
    public bool IsServiceRequest => IsUsb488Notification && (Data[0] & 0x7F) == 1;

    /// <summary>The status byte of a USB488 notification; 0 for other packets.</summary>
    public byte StatusByte => IsUsb488Notification ? Data[1] : (byte)0;
}

/// <summary>The exceptions the USBTMC client creates itself (transfer errors come from the handle).</summary>
internal static class UsbTmcErrors
{
    /// <summary>The device answered with a failure status or an invalid message.</summary>
    public static VisaException Io(string message) => new(VisaStatus.ErrorIO, message);

    /// <summary>The operation ran out of time.</summary>
    public static VisaTimeoutException Timeout(string? message) => new(message);

    /// <summary>The interface lacks what the operation needs.</summary>
    public static VisaException NotSupported(string operation) =>
        new(VisaStatus.ErrorOperationNotSupported, $"{operation} is not supported by this device.");

    /// <summary>The device has no such interface, or it is no USBTMC interface.</summary>
    public static VisaException NotFound(string message) => new(VisaStatus.ErrorResourceNotFound, message);
}

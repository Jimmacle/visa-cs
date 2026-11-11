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

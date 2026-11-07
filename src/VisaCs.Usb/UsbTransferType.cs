namespace VisaCs.Usb;

/// <summary>USB endpoint transfer types (bits 1..0 of an endpoint descriptor's bmAttributes).</summary>
public enum UsbTransferType
{
    /// <summary>Control endpoint.</summary>
    Control = 0,
    /// <summary>Isochronous endpoint (not supported by the transfer methods).</summary>
    Isochronous = 1,
    /// <summary>Bulk endpoint.</summary>
    Bulk = 2,
    /// <summary>Interrupt endpoint.</summary>
    Interrupt = 3,
}

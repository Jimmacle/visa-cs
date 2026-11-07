namespace VisaCs;

/// <summary>Base class for VISA event data.</summary>
public class VisaEventArgs : EventArgs
{
    /// <summary>Creates event data for an event type, time-stamped now.</summary>
    public VisaEventArgs(EventType eventType)
    {
        EventType = eventType;
        Timestamp = DateTimeOffset.Now;
    }

    /// <summary>The event type (VI_ATTR_EVENT_TYPE).</summary>
    public EventType EventType { get; }

    /// <summary>When the library received the event (local time).</summary>
    public DateTimeOffset Timestamp { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{EventType} at {Timestamp:O}";
}

/// <summary>A service request (VI_EVENT_SERVICE_REQ).</summary>
public sealed class ServiceRequestEventArgs : VisaEventArgs
{
    /// <summary>Creates service request data, with the status byte when the protocol delivers it.</summary>
    public ServiceRequestEventArgs(byte? statusByte = null) : base(EventType.ServiceRequest) => StatusByte = statusByte;

    /// <summary>
    /// The status byte that came with the request, when the protocol provides it: HiSLIP and USB488 send it with the
    /// request, and GPIB INSTR sessions read it with the serial poll that answers the request. Null otherwise (VXI-11,
    /// VICP): call <see cref="MessageBasedSession.ReadStatusByte"/>.
    /// </summary>
    public byte? StatusByte { get; }
}

/// <summary>A vendor-specific USB interrupt-IN packet (VI_EVENT_USB_INTR).</summary>
public sealed class UsbInterruptEventArgs : VisaEventArgs
{
    /// <summary>Creates USB interrupt data.</summary>
    public UsbInterruptEventArgs(byte[] data, VisaStatus status = VisaStatus.Success) : base(EventType.UsbInterrupt)
    {
        Data = data;
        Status = status;
    }

    /// <summary>The packet's data (VI_ATTR_USB_RECV_INTR_DATA).</summary>
    public byte[] Data { get; }

    /// <summary>Completion status of the interrupt transfer.</summary>
    public VisaStatus Status { get; }
}

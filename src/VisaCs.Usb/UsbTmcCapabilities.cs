namespace VisaCs.Usb;

/// <summary>
/// Capabilities reported by a USBTMC device in its GET_CAPABILITIES response. All fields are false or 0 when the
/// device did not answer the request.
/// </summary>
/// <param name="UsbTmcVersion">bcdUSBTMC: the USBTMC specification release, in binary-coded decimal (0x0100 is 1.00).</param>
/// <param name="AcceptsIndicatorPulse">The interface accepts INDICATOR_PULSE (<see cref="UsbTmcDevice.IndicatorPulse"/>).</param>
/// <param name="TalkOnly">The interface is talk-only: it accepts no Bulk-OUT messages.</param>
/// <param name="ListenOnly">The interface is listen-only: it sends no Bulk-IN messages.</param>
/// <param name="SupportsTermChar">The device can end a Bulk-IN transfer at a termination character it is given.</param>
/// <param name="Usb488Version">bcdUSB488: the USB488 subclass release in binary-coded decimal; 0 for a plain USBTMC interface.</param>
/// <param name="IsUsb4882">The interface is a USB488.2 interface (IEEE 488.2 compliant).</param>
/// <param name="AcceptsRenControl">The interface accepts REN_CONTROL, GO_TO_LOCAL and LOCAL_LOCKOUT (<see cref="UsbTmcDevice.RenControl"/>).</param>
/// <param name="AcceptsTrigger">The interface accepts the TRIGGER message (<see cref="UsbTmcDevice.Trigger"/>).</param>
/// <param name="Scpi">The device understands all mandatory SCPI commands.</param>
/// <param name="SR1">The device has the SR1 (service request) capability: it can request service on the interrupt endpoint.</param>
/// <param name="RL1">The device has the RL1 (remote/local) capability.</param>
/// <param name="DT1">The device has the DT1 (device trigger) capability.</param>
public readonly record struct UsbTmcCapabilities(
    ushort UsbTmcVersion,
    bool AcceptsIndicatorPulse,
    bool TalkOnly,
    bool ListenOnly,
    bool SupportsTermChar,
    ushort Usb488Version,
    bool IsUsb4882,
    bool AcceptsRenControl,
    bool AcceptsTrigger,
    bool Scpi,
    bool SR1,
    bool RL1,
    bool DT1);

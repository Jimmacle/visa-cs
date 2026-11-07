# USB instruments

```csharp
using var s = rm.Open<UsbSession>("USB::0x0957::0x1796::MY12345678::INSTR");
Console.WriteLine($"{s.ManufacturerName} {s.ModelName}, {s.UsbProtocol}, backend {s.BackendName}");
```

VisaCs talks USBTMC and USB488 itself and reaches the device through a small backend layer
(`IUsbBackend`, in the standalone `VisaCs.Usb` library). No vendor USBTMC driver is involved.

| OS | Default backend | Driver the instrument must use |
|---|---|---|
| Windows | `WinUsbBackend` | WinUSB |
| Linux | `UsbFsBackend` | any (a kernel driver such as `usbtmc` is detached while the interface is open) |
| macOS | `LibUsbBackend` | none (libusb claims the interface) |

`UsbBackends.CreateDefault()` makes this choice. Set `ResourceManagerOptions.UsbBackend` to choose another
backend, for example `new LibUsbBackend()` on Linux, or your own `IUsbBackend` implementation. The
[support matrix](support-matrix.md#how-each-backend-was-verified) lists how far each backend has been tested.

## Setup per operating system

### Windows (WinUSB)

The instrument must be bound to Microsoft's WinUSB driver:

- Devices that publish Microsoft OS descriptors bind to WinUSB automatically.
- Otherwise bind it with [Zadig](https://zadig.akeo.ie/) (choose *WinUSB*) or an INF file that names WinUSB.

An instrument bound to a vendor's own USBTMC driver (installed by NI-VISA or Keysight IO Libraries) is not
visible to WinUSB. Binding it to WinUSB makes it invisible to that vendor's VISA until you switch back.

The backend finds devices through SetupAPI and opens them through the interface GUIDs WinUSB registers
(`DeviceInterfaceGUIDs`), falling back to the generic USB device interface. Enumeration opens each device briefly
to read its descriptors; a device that another program has open is listed with what Windows knows about it (no
endpoints, and the Windows device description as product name). Of a composite device, only the interfaces bound
to WinUSB are listed, and `VI_ATTR_USB_NUM_INTFCS` counts only those.

### Linux (usbfs)

The backend opens `/dev/bus/usb/BBB/DDD` directly. Give your user access with a udev rule, for example
`/etc/udev/rules.d/99-instruments.rules`:

```
SUBSYSTEM=="usb", ATTRS{idVendor}=="0957", MODE="0660", GROUP="plugdev"
```

then `sudo udevadm control --reload && sudo udevadm trigger` and replug the instrument. If a kernel driver (usually
`usbtmc`) is bound to the interface, opening detaches it and closing reattaches it. An interface that another
program has claimed (through usbfs or libusb) is never taken over: opening it fails with `VI_ERROR_RSRC_BUSY`.

### macOS (libusb)

Install libusb (`brew install libusb`). It is loaded at run time from `libusb-1.0.0.dylib`, the Homebrew
prefixes (`/opt/homebrew/lib`, `/usr/local/lib`) or `libusb-1.0.dylib`. The macOS path has not been tested.

### libusb elsewhere

`LibUsbBackend` also works on Linux (`libusb-1.0.so.0`) and Windows (`libusb-1.0.dll`, with the device bound
to WinUSB or libusbK). `LibUsbBackend.IsAvailable` tells whether the library can be loaded. Its enumeration opens
each device to read the string descriptors (ASCII only; empty for devices the process cannot open).

## USB INSTR (USBTMC and USB488)

The session picks the device's USBTMC interface (class 0xFE, subclass 0x03), or the interface number given in
the resource name (`USB::0x0957::0x1796::MY123::1::INSTR`).

- Reads use `REQUEST_DEV_DEP_MSG_IN`. If the device supports it (`Capabilities.SupportsTermChar`), the
  termination character is passed to the device; otherwise the host stops at it.
- Timeouts and cancellation run the USBTMC abort sequences (`INITIATE_ABORT_BULK_OUT/IN`); `Clear` runs
  `INITIATE_CLEAR`.
- `Capabilities` holds the `GET_CAPABILITIES` reply (all false when the device does not answer it), `UsbProtocol`
  says whether the interface is USB488, and `IndicatorPulse()` blinks the device's indicator.
- USB488 devices add `ReadStatusByte`, `AssertTrigger`, `ControlRen` (REN, GTL, LLO) and service requests
  through the interrupt endpoint (with the status byte). Plain USBTMC devices, and devices whose capabilities
  lack trigger or remote/local control, return `VI_ERROR_NSUP_OPER` for these.
- Setting `IOProtocol = IOProtocol.UsbTmcVendor` sends `VENDOR_SPECIFIC_OUT` and requests
  `REQUEST_VENDOR_SPECIFIC_IN`. These messages carry no end-of-message flag, so a transfer shorter than
  requested ends a read.
- Interrupt-IN packets that are not USB488 service requests arrive as `EventType.UsbInterrupt`
  (`UsbInterruptEventArgs.Data`).

## USB RAW

```csharp
using var raw = rm.Open<UsbRawSession>("USB::0x1AB1::0x04CE::DS1ZA1::RAW");
raw.Write(command);                       // bulk OUT
byte[] reply = raw.ReadBytes(64);         // bulk IN
byte[] status = raw.ControlIn(0xC0, 0x01, 0, 0, 8);    // viUsbControlIn
raw.ControlOut(0x40, 0x02, 1, 0, ReadOnlySpan<byte>.Empty);
```

RAW sessions use the first interface that has bulk endpoints and move bytes without any framing. A short
bulk packet (shorter than the endpoint's packet size) ends a read, like END. Every interrupt-IN packet arrives as
`EventType.UsbInterrupt`. `Clear` clears halt conditions on the bulk endpoints. `ControlIn`/`ControlOut` are
available on INSTR sessions too; like reads and writes, they are bounded by the session timeout.

## Sharing and attributes

Operating-system USB handles are exclusive, so all sessions to the same USB interface that go through the same
backend share one handle, with transfers serialized between them (as NI-VISA does). A session waits for another
session's transfer at most until its own timeout. Resource managers that use different backend instances do not
share handles: the second one to open the interface gets `VI_ERROR_RSRC_BUSY`, as does another program.

Read-only attributes: `VI_ATTR_MANF_ID`, `VI_ATTR_MODEL_CODE`, `VI_ATTR_MANF_NAME`, `VI_ATTR_MODEL_NAME`,
`VI_ATTR_USB_SERIAL_NUM`, `VI_ATTR_USB_INTFC_NUM`, `VI_ATTR_USB_PROTOCOL`, `VI_ATTR_USB_CLASS`,
`VI_ATTR_USB_SUBCLASS`, `VI_ATTR_USB_ALT_SETTING`, `VI_ATTR_USB_NUM_INTFCS`, `VI_ATTR_USB_NUM_PIPES`, the
pipe addresses (`VI_ATTR_USB_BULK_IN_PIPE`, `..._BULK_OUT_PIPE`, `..._INTR_IN_PIPE`, `..._CTRL_PIPE`) and
`VI_ATTR_USB_MAX_INTR_SIZE`. Pipes are chosen when the session opens and cannot be changed.

## Without VISA

The backends are in the `VisaCs.Usb` library and work on their own, for any USB device with a bulk or interrupt
interface, not only instruments. The driver setup above applies (WinUSB on Windows, usbfs access rights on
Linux, libusb elsewhere). The library's types are in the `VisaCs.Usb` namespace; the exceptions it throws
(`VisaException`, `VisaTimeoutException`) and `VisaStatus` come from `VisaCs.Primitives`, in the `VisaCs` namespace.

```csharp
using VisaCs;       // VisaException, VisaTimeoutException, VisaStatus
using VisaCs.Usb;

using var usb = UsbBackends.CreateDefault() ?? throw new PlatformNotSupportedException("No USB backend.");

var device = usb.EnumerateDevices().First(d => d.VendorId == 0x1234 && d.ProductId == 0x5678);
using var handle = usb.Open(device, interfaceNumber: 0);
Console.WriteLine($"{handle.Device.Manufacturer} {handle.Device.Product} {handle.Device.SerialNumber}");

var bulkOut = handle.Interface.FindEndpoint(UsbTransferType.Bulk, input: false)!;
var bulkIn = handle.Interface.FindEndpoint(UsbTransferType.Bulk, input: true)!;
byte[] request = "MEAS?\n"u8.ToArray();
var response = new byte[512];
handle.BulkTransfer(bulkOut.Address, request, timeoutMilliseconds: 1000);
int n = handle.BulkTransfer(bulkIn.Address, response, timeoutMilliseconds: 1000);

// Control transfers: the direction comes from bit 7 of the request type.
var deviceDescriptor = new byte[18];
handle.ControlTransfer(0x80, 6 /* GET_DESCRIPTOR */, 0x0100, 0, deviceDescriptor, timeoutMilliseconds: 1000);
string? product = UsbDescriptorParser.ReadString(handle, UsbDescriptorParser.ParseDevice(deviceDescriptor).ProductIndex);

// Asynchronous transfers hold no thread while pending; the token cancels them.
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var interruptIn = handle.Interface.FindEndpoint(UsbTransferType.Interrupt, input: true);
if (interruptIn is not null)
{
    var packet = new byte[interruptIn.MaxPacketSize];
    int length = await handle.InterruptTransferAsync(interruptIn.Address, packet, timeoutMilliseconds: 0, cts.Token);
}
```

- **Directions.** The direction of a transfer comes from bit 7 of the endpoint address (or of the request type,
  for control transfers). An IN transfer returns the number of bytes received, which can be fewer than the buffer
  holds. A control transfer carries at most 65535 bytes.
- **Timeouts.** `timeoutMilliseconds` bounds one transfer; 0 and `Timeout.Infinite` (-1) wait without limit. A
  transfer that times out is cancelled in the operating system and throws `VisaTimeoutException`; data that an IN
  transfer had already received is discarded, and an OUT transfer may have been partly sent, so resynchronize with
  the device afterwards (USBTMC devices: `INITIATE_CLEAR` or the abort requests).
- **Cancellation.** Cancelling the token of an asynchronous transfer cancels it in the operating system before
  the call returns, then throws `OperationCanceledException`; the device stays usable. With libusb on macOS this
  also aborts the other transfers pending on the same endpoint.
- **Errors.** `VisaException` with `VisaStatus.ErrorConnectionLost` when the device is unplugged and
  `ErrorIO` when an endpoint stalls (call `ClearHalt`) or a transfer fails otherwise. `Open` throws
  `ErrorResourceNotFound`, `ErrorResourceBusy` (the interface is claimed elsewhere), `ErrorNoPermission` or
  `ErrorInvalidObject` (a device enumerated by another backend).
- **Threads.** All handle members may be called from any thread, concurrently: synchronous and asynchronous
  transfers mix, on different endpoints (for example an interrupt-IN read while bulk transfers run) or on the same
  one, where they complete in order. WinUSB completes asynchronous transfers on the I/O completion port, usbfs
  submits URBs and collects them when the device file reports them, and libusb uses its asynchronous API with one
  event thread.
- **Disposal.** Disposing a handle cancels every transfer still running on it, synchronous or asynchronous; those
  throw `ObjectDisposedException`. Dispose the handles before the backend: disposing `LibUsbBackend` closes the
  handles it opened and releases libusb, while disposing the WinUSB and usbfs backends does nothing.
- **What enumeration knows.** usbfs reads sysfs and does no device I/O; WinUSB and libusb open each device briefly
  (see the setup sections). `handle.Device` and `handle.Interface` are complete once the device is open.

## Writing a backend

Implement `IUsbBackend` (`Name`, `EnumerateDevices`, `Open`, `Dispose`) and `IUsbDeviceHandle` (control, bulk and
interrupt transfers, synchronous and asynchronous, and `ClearHalt`); their XML documentation is the contract.
`UsbDescriptorParser` decodes standard descriptors. In particular:

- Throw `VisaTimeoutException` on timeouts, so that the USBTMC abort handling runs, and cancel the transfer in the
  operating system before throwing.
- Asynchronous methods throw `OperationCanceledException` when their token is cancelled, and every method throws
  `ObjectDisposedException` once the handle is disposed (a transfer pending at that moment too).
- Support concurrent transfers on different endpoints: the VISA layer keeps an interrupt-IN read pending while
  sessions run bulk and control transfers.

`SimulatedUsbBackend` in `VisaCs.Simulation` is a complete example.

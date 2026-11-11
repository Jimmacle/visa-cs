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
backend share one handle, one gate that serializes their transfers (as NI-VISA does) and one loop that reads the
interrupt endpoint for all of them. INSTR sessions (and every session on a USBTMC interface) also share one
`UsbTmcDevice` client, which takes the same gate, counts the USBTMC bTags for all of them and gets its status byte
responses from the shared interrupt loop; RAW sessions transfer on the handle directly. A session waits for another
session's transfer at most until its own timeout. Resource managers that use different backend instances do not
share handles: the second one to open the interface gets `VI_ERROR_RSRC_BUSY`, as does another program.

Read-only attributes: `VI_ATTR_MANF_ID`, `VI_ATTR_MODEL_CODE`, `VI_ATTR_MANF_NAME`, `VI_ATTR_MODEL_NAME`,
`VI_ATTR_USB_SERIAL_NUM`, `VI_ATTR_USB_INTFC_NUM`, `VI_ATTR_USB_PROTOCOL`, `VI_ATTR_USB_CLASS`,
`VI_ATTR_USB_SUBCLASS`, `VI_ATTR_USB_ALT_SETTING`, `VI_ATTR_USB_NUM_INTFCS`, `VI_ATTR_USB_NUM_PIPES`, the
pipe addresses (`VI_ATTR_USB_BULK_IN_PIPE`, `..._BULK_OUT_PIPE`, `..._INTR_IN_PIPE`, `..._CTRL_PIPE`) and
`VI_ATTR_USB_MAX_INTR_SIZE`. Pipes are chosen when the session opens and cannot be changed.

## Without VISA

The `VisaCs.Usb` library works on its own. It has the backends, for any USB device with a bulk or interrupt
interface, and `UsbTmcDevice`, the USBTMC/USB488 client that `UsbSession` is built on. The driver setup above
applies (WinUSB on Windows, usbfs access rights on Linux, libusb elsewhere). The library depends on no other
VisaCs package, and all its types are in the `VisaCs.Usb` namespace.

Errors follow one scheme throughout:

| Exception | When |
|---|---|
| `UsbException` (an `IOException`) | a device, driver, operating system or protocol failure. `Error` (`UsbError`) says which: `NotFound`, `Busy`, `AccessDenied`, `Disconnected`, `Stall`, `IOError`, `ProtocolError` (a USBTMC failure status or invalid message), `NotSupported` (the interface lacks the capability), `LibraryNotFound` (libusb), `OutOfMemory` (libusb), `Aborted`, `SystemError`. `NativeError` holds the Win32 error, errno or libusb error code when there is one. |
| `UsbTimeoutException` (a `TimeoutException`) | a transfer or call ran out of time; `BytesTransferred` is 0 for the built-in backends (a timed-out transfer's data is discarded) |
| `OperationCanceledException` | the caller cancelled an asynchronous call |
| `ArgumentException`, `ObjectDisposedException`, `InvalidOperationException` | programming errors: a device enumerated by another backend, a disposed handle or client, ... |

Every `timeoutMilliseconds` follows one rule: negative (`Timeout.Infinite`) waits without limit, 0 does not wait
(the operation fails with a timeout unless it can complete at once), and a positive value is the limit in
milliseconds.

### USBTMC instruments

```csharp
using System.Text;
using VisaCs.Usb;

using var usb = UsbBackends.CreateDefault() ?? throw new PlatformNotSupportedException("No USB backend.");
var device = usb.EnumerateDevices().First(d => d.VendorId == 0x1234 && d.ProductId == 0x5678);

// Opens the first USBTMC interface and reads GET_CAPABILITIES.
using var tmc = UsbTmcDevice.Open(usb, device);
string idn = Encoding.ASCII.GetString(tmc.Query("*IDN?\n"u8, timeoutMilliseconds: 2000));

tmc.Write("CONF:VOLT:DC\n"u8, end: true, timeoutMilliseconds: 2000);
await tmc.WriteAsync("READ?\n"u8.ToArray(), end: true, timeoutMilliseconds: 2000);
byte[] reading = await tmc.ReadMessageAsync(timeoutMilliseconds: 5000);  // transfers until EOM

var buffer = new byte[4096];
UsbTmcReadResult part = tmc.Read(buffer, timeoutMilliseconds: 2000);       // one transfer; part.End = EOM

tmc.Clear(timeoutMilliseconds: 2000);                                       // INITIATE_CLEAR sequence
if (tmc.IsUsb488)
{
    byte stb = tmc.ReadStatusByte(timeoutMilliseconds: 2000);
    if (tmc.Capabilities.AcceptsTrigger) tmc.Trigger(timeoutMilliseconds: 2000);
    if (tmc.Capabilities.AcceptsRenControl) tmc.RenControl(enable: true, timeoutMilliseconds: 2000);
}
if (tmc.HasInterruptEndpoint)
    tmc.AddInterruptHandler(p => { if (p.IsServiceRequest) Console.WriteLine($"SRQ, status byte 0x{p.StatusByte:X2}"); });
```

- **Messages.** `Write` sends DEV_DEP_MSG_OUT transfers (at most 1 MiB each), with EOM on the last one when `end`
  is true. `Read` asks for one transfer of up to the buffer's length and reports whether it carried EOM;
  `ReadMessage` reads until EOM, and `Query` writes and reads with no other caller's operation in between. With
  `termChar`, a device that reports `SupportsTermChar` ends the transfer after that byte. `WriteVendorSpecific` and
  `ReadVendorSpecific` send VENDOR_SPECIFIC_OUT and REQUEST_VENDOR_SPECIFIC_IN messages.
- **Timeouts and cancellation.** `timeoutMilliseconds` bounds the whole call, including the wait for another
  caller's operation: -1 waits without limit, 0 does not wait (the call succeeds only if the device is free and
  every transfer completes at once). A bulk transfer that times out or is cancelled is followed by the USBTMC abort
  sequence, so the next message works. Timeouts throw `UsbTimeoutException`, cancelling an asynchronous call throws
  `OperationCanceledException`. Class requests (clear, status byte, remote/local) wait at most 5 seconds when the
  timeout is infinite, and at least 1 millisecond.
- **Capabilities.** `UsbTmcDevice.Open` reads them; over a handle you opened yourself
  (`new UsbTmcDevice(handle)`), call `ReadCapabilities`. `Capabilities` may be read from any thread, also while
  another thread reads them from the device. Trigger, remote/local control and `IndicatorPulse` throw
  `UsbException` with `UsbError.NotSupported` unless the capabilities allow them; the status byte, trigger and
  remote/local control need a USB488 interface.
- **Status byte and SRQ.** `ReadStatusByte` matches the response on the interrupt endpoint to its request by bTag,
  also while interrupt handlers run. Handlers get every other interrupt packet (`IsServiceRequest`, `StatusByte`,
  `Data`) on a read loop that holds no thread while idle.
- **Sharing.** One instance may be used from several threads: each operation has the device to itself and bTags
  never repeat. `ControlTransfer` and `RunExclusive` run other transfers between the client's operations. Code that
  uses the same handle without the client can take turns with it through a shared gate
  (`new UsbTmcDevice(handle, leaveOpen: true, gate)`, a `SemaphoreSlim(1, 1)` that the other code also takes), and
  can own the interrupt-IN endpoint: set `ExternalInterruptReader` and pass every packet read there to
  `ProcessInterrupt`, which keeps the status byte responses (returns true) and leaves the rest to the caller. The VISA
  sessions share one interface between INSTR and RAW sessions this way.
- **Errors.** `UsbException` with `UsbError.ProtocolError` when the device reports a failure or sends an invalid
  header, `UsbError.NotSupported` as above, and the handle's own errors for failed transfers.

### Raw transfers

For devices that are not USBTMC instruments, use the handle directly:

```csharp
using VisaCs.Usb;

using var usb = UsbBackends.CreateDefault() ?? throw new PlatformNotSupportedException("No USB backend.");

var device = usb.EnumerateDevices().First(d => d.VendorId == 0x1234 && d.ProductId == 0x5678);
using var handle = usb.Open(device, interfaceNumber: 0);
Console.WriteLine($"{handle.Device.Manufacturer} {handle.Device.Product} {handle.Device.SerialNumber}");

var bulkOut = handle.Interface.FindEndpoint(UsbTransferType.Bulk, input: false)!;
var bulkIn = handle.Interface.FindEndpoint(UsbTransferType.Bulk, input: true)!;
byte[] request = [0x01, 0x02];
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
    int length = await handle.InterruptTransferAsync(interruptIn.Address, packet, Timeout.Infinite, cts.Token);
}
```

- **Directions.** The direction of a transfer comes from bit 7 of the endpoint address (or of the request type,
  for control transfers). An IN transfer returns the number of bytes received, which can be fewer than the buffer
  holds. A control transfer carries at most 65535 bytes.
- **Timeouts.** `timeoutMilliseconds` bounds one transfer: `Timeout.Infinite` (-1) waits without limit, 0 does not
  wait (the transfer fails unless it completes at once; libusb, which has no such mode, gets 1 ms). WinUSB and libusb
  themselves read 0 as "no limit"; the backends translate. A
  transfer that times out is cancelled in the operating system and throws `UsbTimeoutException`; data that an IN
  transfer had already received is discarded, and an OUT transfer may have been partly sent, so resynchronize with
  the device afterwards (`UsbTmcDevice` does that for USBTMC devices).
- **Cancellation.** Cancelling the token of an asynchronous transfer cancels it in the operating system before
  the call returns, then throws `OperationCanceledException`; the device stays usable. With libusb on macOS this
  also aborts the other transfers pending on the same endpoint.
- **Errors.** `UsbException` with `UsbError.Disconnected` when the device is unplugged, `Stall` when an endpoint
  stalls (call `ClearHalt`) and `IOError` when a transfer fails otherwise. `Open` throws `UsbException` with
  `NotFound`, `Busy` (the interface is claimed elsewhere) or `AccessDenied`, and `ArgumentException` for a device
  enumerated by another backend.
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

- Follow the timeout rule: negative waits without limit, 0 does not wait (fail with a timeout unless the transfer
  completes at once). Translate it when the driver stack reads 0 as "no limit".
- Throw `UsbTimeoutException` on timeouts, so that the USBTMC abort handling of `UsbTmcDevice` runs, and cancel
  the transfer in the operating system before throwing.
- Throw `UsbException` for device, driver and operating system failures, with the `UsbError` that fits (`NotFound`,
  `Busy` or `AccessDenied` from `Open`; `Disconnected`, `Stall` or `IOError` from transfers; ...) and the native
  error code in `NativeError`. The VISA sessions turn these into VISA status codes (`NotFound` into
  `VI_ERROR_RSRC_NFOUND`, `Busy` into `VI_ERROR_RSRC_BUSY`, `Disconnected` into `VI_ERROR_CONN_LOST`, ...), so a
  backend that throws anything else breaks their error reporting. Use `ArgumentException` for a device the backend
  did not enumerate.
- Asynchronous methods throw `OperationCanceledException` when their token is cancelled, and every method throws
  `ObjectDisposedException` once the handle is disposed (a transfer pending at that moment too).
- Support concurrent transfers on different endpoints: `UsbTmcDevice` keeps an interrupt-IN read pending while
  it runs bulk and control transfers.

`SimulatedUsbBackend` in `VisaCs.Simulation` is a complete example.

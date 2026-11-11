# VisaCs.Usb

USB host access without a vendor SDK: device enumeration, control, bulk and interrupt transfers behind one
`IUsbBackend` interface, for any device with a bulk or interrupt interface (not only instruments), and
`UsbTmcDevice`, a USBTMC/USB488 client for test and measurement instruments.

| Backend | OS | The device must be |
|---|---|---|
| `WinUsbBackend` | Windows | bound to WinUSB (automatic with Microsoft OS descriptors, otherwise Zadig or an INF) |
| `UsbFsBackend` | Linux | accessible in `/dev/bus/usb` (a udev rule); a bound kernel driver is detached while open |
| `LibUsbBackend` | macOS, Linux, Windows | reachable by libusb-1.0, loaded at run time (`brew install libusb` on macOS) |

`UsbBackends.CreateDefault()` picks the built-in backend for the current OS.

## Install

```
dotnet nuget add source https://nuget.pkg.github.com/Jimmacle/index.json --name visa-cs --username <github-user> --password <token with read:packages>
dotnet add package VisaCs.Usb --version 0.1.0
```

## Example

```csharp
using System.Text;
using VisaCs.Usb;

using IUsbBackend usb = UsbBackends.CreateDefault() ?? throw new PlatformNotSupportedException("No USB backend.");

foreach (UsbDeviceInfo d in usb.EnumerateDevices())
    Console.WriteLine($"{d.VendorId:X4}:{d.ProductId:X4} {d.Manufacturer} {d.Product} ({d.SerialNumber})");

UsbDeviceInfo device = usb.EnumerateDevices().First(d => d.VendorId == 0x1234 && d.ProductId == 0x5678);
using UsbTmcDevice tmc = UsbTmcDevice.Open(usb, device);   // first USBTMC interface; reads its capabilities

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
byte[] response = await tmc.QueryAsync("MEAS?\n"u8.ToArray(), timeoutMilliseconds: 2000, cts.Token);
Console.WriteLine(Encoding.ASCII.GetString(response));
```

- `UsbTmcDevice` speaks USBTMC/USB488: messages with END (`Write`, `Read`, `ReadMessage`, `Query`),
  vendor-specific messages, device clear, the status byte, trigger, remote/local control and service requests.
  A transfer that times out or is cancelled is followed by the USBTMC abort sequence. One instance can be shared
  between threads.
- For devices that are not instruments, transfer on the handle directly: `usb.Open(device, interfaceNumber)`, then
  `BulkTransfer`, `InterruptTransfer` or `ControlTransfer` (and their async forms). The direction comes from bit 7
  of the endpoint address (or the request type, for control transfers).
- `UsbTmcDevice` timeouts bound the whole call, a handle transfer's timeout bounds one transfer; for both, -1 means
  no limit and 0 means "don't wait" (fail unless it completes at once). Timeouts throw `UsbTimeoutException`;
  cancelling throws `OperationCanceledException` and cancels the transfer in the OS. Asynchronous calls hold no
  thread.
- Failures throw `UsbException` (an `IOException`) whose `Error` names what went wrong (`NotFound`, `Busy`,
  `AccessDenied`, `Disconnected`, `Stall`, `ProtocolError`, `NotSupported`, ...) and whose `NativeError` holds the
  OS or libusb error code. The library depends on no other package.
- Implement `IUsbBackend` and `IUsbDeviceHandle` to plug in another driver stack; `UsbDescriptorParser` decodes
  standard descriptors.

More in the [USB guide](https://github.com/Jimmacle/visa-cs/blob/main/docs/usb.md#without-visa), including
driver setup per OS. The WinUSB transfer path and macOS have not been tested yet.

## License

MIT

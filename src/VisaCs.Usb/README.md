# VisaCs.Usb

USB host access without a vendor SDK: device enumeration, control, bulk and interrupt transfers behind one
`IUsbBackend` interface, for any device with a bulk or interrupt interface (not only instruments).

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
using VisaCs.Usb;

using IUsbBackend usb = UsbBackends.CreateDefault() ?? throw new PlatformNotSupportedException("No USB backend.");

foreach (UsbDeviceInfo d in usb.EnumerateDevices())
    Console.WriteLine($"{d.VendorId:X4}:{d.ProductId:X4} {d.Manufacturer} {d.Product} ({d.SerialNumber})");

UsbDeviceInfo device = usb.EnumerateDevices().First(d => d.VendorId == 0x1234 && d.ProductId == 0x5678);
using IUsbDeviceHandle handle = usb.Open(device, interfaceNumber: 0);
UsbEndpointInfo bulkOut = handle.Interface.FindEndpoint(UsbTransferType.Bulk, input: false)!;
UsbEndpointInfo bulkIn = handle.Interface.FindEndpoint(UsbTransferType.Bulk, input: true)!;

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await handle.BulkTransferAsync(bulkOut.Address, "MEAS?\n"u8.ToArray(), timeoutMilliseconds: 1000, cts.Token);
var response = new byte[512];
int n = await handle.BulkTransferAsync(bulkIn.Address, response, timeoutMilliseconds: 1000, cts.Token);
```

- The direction comes from bit 7 of the endpoint address (or the request type, for control transfers).
- A timeout of 0 or less waits without limit; an expired one throws `VisaTimeoutException` (from
  `VisaCs.Primitives`); cancelling throws `OperationCanceledException` and cancels the transfer in the OS.
- Several transfers can be in flight on one handle. Asynchronous transfers hold no thread.
- Implement `IUsbBackend` and `IUsbDeviceHandle` to plug in another driver stack; `UsbDescriptorParser` decodes
  standard descriptors.

More in the [USB guide](https://github.com/Jimmacle/visa-cs/blob/main/docs/usb.md#without-visa), including
driver setup per OS. The WinUSB transfer path and macOS have not been tested yet.

## License

MIT

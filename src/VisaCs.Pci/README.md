# VisaCs.Pci

User-space register access to PCI and PCIe functions on Linux through sysfs, without a kernel driver of its own:
list functions, read and write configuration space, and memory and I/O BARs. Useful for FPGA cards, custom
PCIe hardware and PXI instruments.

Linux only (`/sys/bus/pci/devices`). The first 64 bytes of configuration space are readable by any user; the rest
of configuration space, writes and the BARs need root or a udev rule granting access to the function's `config`
and `resource*` files. Memory BARs must be enabled (memory decoding on in the command register).

## Install

```
dotnet nuget add source https://nuget.pkg.github.com/Jimmacle/index.json --name visa-cs --username <github-user> --password <token with read:packages>
dotnet add package VisaCs.Pci --version 0.1.0
```

## Example

```csharp
using VisaCs.Pci;   // PciDeviceInfo, PciRegion, PciException

foreach (PciDeviceInfo d in PciDeviceInfo.Enumerate())
    Console.WriteLine($"{d.Address} {d.VendorId:X4}:{d.DeviceId:X4} driver={d.Driver ?? "none"}");

PciDeviceInfo card = PciDeviceInfo.FromAddress("0000:03:12.0") ?? throw new InvalidOperationException("Not found.");

using (PciRegion config = card.OpenConfigurationSpace())
    Console.WriteLine($"Command register: 0x{config.Read16(4):X4}");

using PciRegion bar0 = card.OpenBar(0);
bar0.Write32(0x10, 0x1);
uint status = bar0.Read32(0x14);
```

- Memory BARs are mapped into the process and accessed with single loads and stores of the exact width;
  `PciRegion.Address` gives the mapped pointer for your own unsafe code (valid until the region is disposed).
- I/O BARs and configuration space are accessed with file reads and writes at the offset. I/O BARs take 8, 16
  and 32-bit accesses only, as Linux does. Configuration space is opened for writing only at the first write.
- Every access is checked for width, alignment and bounds and throws `ArgumentOutOfRangeException` when invalid.
- Device and operating-system failures throw `PciException` (an `IOException`) with a `PciError`:
  `AccessDenied`, `NotFound`, `MapFailed`, `BarNotImplemented`, `BusError` or `IOError`.
- Accesses may run on several threads. Disposing a region lets running accesses finish; later ones throw
  `ObjectDisposedException`.
- The package depends on no other VisaCs package.
- `OpenBar` does not check `IsAvailableToUserSpace`: writing to a function that a kernel driver owns can disturb
  that driver.

More in the [PXI guide](https://github.com/Jimmacle/visa-cs/blob/main/docs/pxi.md#without-visa).

## License

MIT

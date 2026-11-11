# PXI and PCI instruments (Linux)

PXI and PCI(e) instruments are register-based: you read and write their configuration space and memory or
I/O BARs instead of sending messages. VisaCs implements this on Linux through sysfs, without a vendor kernel
driver. Windows and macOS have no user-space access to PCI devices, so `PXI` resources are not available there.

```csharp
using var rm = new ResourceManager();
using var card = rm.Open<PxiSession>("PXI0::3-18.0::INSTR");
Console.WriteLine($"{card.ManufacturerId:X4}:{card.ModelCode:X4}");
uint id = card.In32(AddressSpace.PxiConfig, 0);
card.Out32(AddressSpace.PxiBar0, 0x10, 0x1);
```

`PxiSession` is a `VisaSession` but not a `MessageBasedSession`, so open it with `Open<PxiSession>` or
`Open<VisaSession>` (plain `Open` throws `VI_ERROR_INV_OBJECT`). `PxiSession.Device` describes the function
(`PciDeviceInfo`: addresses, IDs, bound driver, BARs); `PciDeviceInfo.Enumerate()` lists all functions in sysfs.
The sysfs access itself is in the `VisaCs.Pci` library, usable without VISA ([below](#without-visa)).

## Which devices can be opened

A PCI function is available when no kernel driver is bound to it, or when it is bound to a driver meant for
user-space access: `vfio-pci`, `uio_pci_generic` or `pci-stub`. Functions owned by any other driver are not
listed by `Find`, and opening them fails with `VI_ERROR_RSRC_BUSY`, so you cannot disturb a NIC or a GPU by
accident. To hand a card to user space:

```
echo 0000:03:12.0 | sudo tee /sys/bus/pci/devices/0000:03:12.0/driver/unbind
echo <vendor> <device> | sudo tee /sys/bus/pci/drivers/pci-stub/new_id
```

Reading the first 64 bytes of configuration space works for any user; the rest of configuration space, writes to
it and the BARs need root, or a udev rule that grants access to `/sys/bus/pci/devices/<address>/config` and
`resource*` (`VI_ERROR_NPERMISSION` otherwise). Memory BARs must be enabled (memory decoding on in the command
register) to be read.

## Resource names

| Form | Example |
|---|---|
| `PXI[interface]::bus-device[.function][::INSTR]` | `PXI0::3-18.0::INSTR` |
| `PXI[interface]::domain:bus-device[.function][::INSTR]` (domain in hex) | `PXI0::1a2b:5-0.0::INSTR` |
| `PXI[bus]::device[::function][::INSTR]` | `PXI3::18::INSTR` |
| `PXI[interface]::CHASSISn::SLOTm[::FUNCf][::INSTR]` | parsed, but cannot be opened (no chassis map) |

Bus, device and function are decimal, as in NI-VISA. `PXI0::3-18.0::INSTR` is the Linux function
`0000:03:12.0`. `Find("PXI?*")` lists available functions in the canonical form.

## Register access

| VISA | `PxiSession` |
|---|---|
| `viIn8/16/32/64`, `viOut8/16/32/64` | `In8` ... `In64`, `Out8` ... `Out64` |
| `viMoveIn8/16/32/64`, `viMoveOut8/16/32/64` | `MoveIn8` ... `MoveOut64` with spans |
| `viMapAddress`, `viUnmapAddress` | `MapAddress(space, offset, size)` returns a `PxiWindow`; `UnmapAddress()` |
| `viPeek8/16/32/64`, `viPoke8/16/32/64` | `PxiWindow.Peek8` ... `Poke64` |

Address spaces are `AddressSpace.PxiConfig` and `PxiBar0` to `PxiBar5`. Accesses are checked: the offset must
be aligned to the access width (`VI_ERROR_NSUP_ALIGN_OFFSET`), lie inside the space (`VI_ERROR_INV_OFFSET`), and the
BAR must exist (`VI_ERROR_INV_SPACE`). I/O BARs take 8, 16 and 32-bit accesses only (`VI_ERROR_NSUP_WIDTH` for
64 bits), as Linux does.

- Memory BARs are memory-mapped (`resourceN`) and accessed with single loads and stores of the exact width.
  `PxiWindow.Address` gives the mapped pointer for your own unsafe code.
- I/O BARs and configuration space are accessed through file reads and writes at the offset. Configuration space
  is opened for writing only at the first write, so reading it needs no privileges.
- `SourceIncrement` and `DestinationIncrement` (`VI_ATTR_SRC_INCREMENT`, `VI_ATTR_DEST_INCREMENT`) set the
  register step for block moves; 0 keeps accessing the same register (a FIFO).
- `SourceByteOrder`, `DestinationByteOrder` and `WindowByteOrder` default to little-endian (the PCI order);
  big-endian swaps each access.
- One window can be mapped per session (`VI_ERROR_WINDOW_MAPPED` otherwise), as in VISA. Its size must be
  positive (`VI_ERROR_INV_SIZE`). `PxiWindow.Address` is valid only until the window is unmapped or the session
  closes; dereferencing it afterwards crashes the process. It also bypasses the bounds checks and
  `WindowByteOrder`.
- Register accesses may run on several threads at once. Closing the session lets the accesses that are running
  finish before the BARs are unmapped; later ones throw `VI_ERROR_INV_OBJECT` (`VI_ERROR_WINDOW_NMAPPED` through a
  window).

## Attributes

`VI_ATTR_PXI_BUS_NUM`, `..._DEV_NUM`, `..._FUNC_NUM`, `VI_ATTR_MANF_ID` and `VI_ATTR_MODEL_CODE` (vendor and
device ID), `VI_ATTR_PXI_IS_EXPRESS`, `VI_ATTR_PXI_ACTUAL_LWIDTH` and `..._MAX_LWIDTH`, the type, base and size
of each implemented BAR (`VI_ATTR_PXI_MEM_TYPE_BARn`, `..._MEM_BASE_BARn[_32]`, `..._MEM_SIZE_BARn[_32]`),
the byte-order and increment attributes, and the window attributes (`VI_ATTR_WIN_ACCESS`, `..._BASE_ADDR`,
`..._SIZE`, `..._BYTE_ORDER`). `VI_ATTR_PXI_CHASSIS` and `VI_ATTR_SLOT` report -1 and `VI_ATTR_PXI_SLOTPATH` is
empty, because the chassis layout is unknown without a PXI resource manager. The full list is in [attributes.md](attributes.md).

## Without VISA

Device enumeration and register access are in the `VisaCs.Pci` library and work on their own, for any PCI(e)
function: FPGA cards, custom PCIe hardware, PXI modules. The setup above applies (root or a udev rule; bind the
function to `pci-stub`, `vfio-pci` or `uio_pci_generic`, or leave it without a driver). The library depends on no
other VisaCs package; all its types (`PciDeviceInfo`, `PciBar`, `PciBarType`, `PciRegion`, `PciException`,
`PciError`) are in the `VisaCs.Pci` namespace.

```csharp
using VisaCs.Pci;   // PciDeviceInfo, PciBar, PciRegion, PciException

foreach (var d in PciDeviceInfo.Enumerate().Where(d => d.IsAvailableToUserSpace))
    Console.WriteLine($"{d.Address} {d.VendorId:X4}:{d.DeviceId:X4} class {d.ClassCode:X6}");

var card = PciDeviceInfo.FromAddress("0000:03:12.0") ?? throw new InvalidOperationException("No such function.");
foreach (var bar in card.Bars.Where(b => b.Type != PciBarType.None))
    Console.WriteLine($"BAR{bar.Index}: {bar.Type} 0x{bar.Address:X} {bar.Size} bytes");

using (var config = card.OpenConfigurationSpace())
    Console.WriteLine($"Vendor 0x{config.Read16(0):X4}");

using var bar0 = card.OpenBar(0);          // memory BAR: mapped into the process
bar0.Write32(0x10, 0x1);
uint status = bar0.Read32(0x14);
ulong wide = bar0.Read(0x18, width: 8);   // or Read8/16/32/64, Write8/16/32/64
```

- **Regions.** `OpenConfigurationSpace()` and `OpenBar(index)` return a `PciRegion` with its `Size`, its widest
  access (`MaxWidth`: 8, or 4 for I/O BARs) and, for memory BARs, the mapped pointer (`Address`, otherwise 0). A
  BAR the device does not implement throws `PciException` with `PciError.BarNotImplemented`; an index outside 0 to
  5 throws `ArgumentOutOfRangeException`.
- **Checks.** Every access must be 1, 2, 4 or 8 bytes wide and at most `MaxWidth`, aligned to its width and inside
  the region; otherwise it throws `ArgumentOutOfRangeException` (`ParamName` `width` or `offset`). Values are in
  host order, which is the PCI order (little endian) on the hosts Linux runs on; there are no byte-order or
  increment settings and no block moves, as there are in `PxiSession`.
- **Errors.** Device and operating-system failures throw `PciException` (an `IOException`) whose `Error` says
  what failed: `AccessDenied`, `NotFound` (the sysfs file is missing), `MapFailed` (a memory BAR cannot be
  mapped), `BarNotImplemented`, `BusError` (a short read from an I/O BAR) or `IOError`. `PxiSession` reports them
  as `VI_ERROR_NPERMISSION`, `VI_ERROR_RSRC_NFOUND`, `VI_ERROR_RSRC_BUSY`, `VI_ERROR_INV_SPACE`, `VI_ERROR_BERR`
  and `VI_ERROR_IO`.
- **Permissions.** Opening needs read access to the file; configuration space is opened for writing only at the
  first write. Denied access throws `PciError.AccessDenied`; reading configuration space past the first 64 bytes
  without root does too.
- **Ownership.** `OpenBar` does not check `IsAvailableToUserSpace`; writing to a function that a kernel driver owns
  can disturb that driver.
- **Threads and disposal.** Accesses may run on several threads at once. `Dispose` lets the accesses that are
  running finish before a memory BAR is unmapped; later accesses throw `ObjectDisposedException`. `Address` must not
  be dereferenced after `Dispose`.

## Not supported

PXI triggers (`viMapTrigger`, trigger reservation), PXI interrupts (`VI_EVENT_PXI_INTR`), DMA, `MEMACC`
resources, and chassis/slot addressing.

## Testing

The tests build a fake sysfs tree (configuration file and BAR files, a read-only configuration file, a function
bound to a kernel driver) and run all register operations against it, including closing a session while other
threads access it. With `VISACS_TEST_PCI=1` (as root) they also check that a real function owned by a kernel
driver is refused and read the configuration space of the first real function available to user space, when the
machine has one. They have not been run against a PXI chassis.

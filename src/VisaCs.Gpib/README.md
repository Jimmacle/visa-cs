# VisaCs.Gpib

GPIB (IEEE 488) from C# through any NI-488.2 compatible driver library, bound at run time: device and board
I/O, serial poll, SRQ and bus control. There is no compile-time dependency on a vendor SDK, but the GPIB board
vendor's driver must be installed.

| OS | Libraries tried, in order |
|---|---|
| Windows | `ni4882.dll`, `gpib-32.dll` (NI-488.2, or Keysight IO Libraries with NI-488.2 compatibility) |
| Linux | `libgpib.so.0`, `libgpib.so` (linux-gpib) |
| macOS | `/Library/Frameworks/NI4882.framework/NI4882`, `libgpib.dylib` |

For another library, pass its path: `new Ni488Driver("/path/to/library")`. `LoadedLibrary` shows what was
loaded and `LoadError` why nothing was.

## Install

```
dotnet nuget add source https://nuget.pkg.github.com/Jimmacle/index.json --name visa-cs --username <github-user> --password <token with read:packages>
dotnet add package VisaCs.Gpib --version 0.1.0
```

## Example

```csharp
using VisaCs.Gpib;

var driver = new Ni488Driver();
if (!driver.IsAvailable) throw new InvalidOperationException(driver.LoadError);
Console.WriteLine($"Using {driver.LoadedLibrary}");

foreach (var (primary, secondary) in driver.FindListeners(board: 0))
    Console.WriteLine($"GPIB0::{primary}::INSTR");

using IGpibDevice dmm = driver.OpenDevice(board: 0, primaryAddress: 22, secondaryAddress: null);
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await dmm.WriteAsync("*IDN?\n"u8.ToArray(), end: true, timeoutMilliseconds: 2000, cts.Token);
var buffer = new byte[256];
GpibReadResult r = await dmm.ReadAsync(buffer, termChar: null, timeoutMilliseconds: 2000, cts.Token);   // null: stop on EOI only
Console.WriteLine($"{r.Count} bytes, EOI: {r.End}");
byte stb = dmm.SerialPoll(timeoutMilliseconds: 1000);
```

- `OpenBoard` returns an `IGpibBoard` for controller operations: commands with ATN (`GpibCommand`), IFC, REN,
  passing control and the bus lines (`GpibLines`).
- With NI-488.2's asynchronous calls (`ibrda`/`ibwrta` with `ibnotify`, or polled on linux-gpib) the
  asynchronous methods hold no thread; cancelling stops the transfer with `ibstop`.
- Driver errors are `VisaException`s (from `VisaCs.Primitives`) with the matching VISA status, timeouts
  `VisaTimeoutException`.
- Implement `IGpibDriver` for another driver or a simulation.

More in the [GPIB guide](https://github.com/Jimmacle/visa-cs/blob/main/docs/gpib.md#without-visa). No real GPIB
board has been tested yet; the binding is tested against a stand-in NI-488.2 library.

## License

MIT

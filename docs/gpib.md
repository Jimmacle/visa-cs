# GPIB instruments

GPIB needs the GPIB board vendor's kernel driver, so VisaCs talks to it through that vendor's NI-488.2
compatible C library (the `ib*` functions). Everything above that layer (sessions, events, attributes,
formatted I/O) is VisaCs.

```csharp
using var dmm = rm.Open<GpibSession>("GPIB0::22::INSTR");
Console.WriteLine(dmm.Query("*IDN?"));

using var board = rm.Open<GpibInterfaceSession>("GPIB0::INTFC");
board.SendInterfaceClear();
```

## Driver library

`Ni488Driver` loads the first library it finds, on first use, and binds the functions at run time (no
compile-time dependency):

| OS | Libraries tried, in order |
|---|---|
| Windows (64-bit process) | `ni4882.dll`, `gpib-32.dll` |
| Windows (32-bit process) | `gpib-32.dll`, `ni4882.dll` |
| Linux | `libgpib.so.0`, `libgpib.so` (linux-gpib) |
| macOS | `/Library/Frameworks/NI4882.framework/NI4882`, `libgpib.dylib` |

`ni4882.dll` comes with NI-488.2. `gpib-32.dll` is provided by NI and, for Keysight/Agilent boards, by the
Keysight IO Libraries (enable the option for NI-488.2 compatible programs). For another library, such as
NI-488.2 for Linux, pass its path: `new Ni488Driver("/path/to/library")`, and for VISA sessions set that driver
as `ResourceManagerOptions.GpibDriver`. `Ni488Driver.LoadedLibrary` shows what was loaded, and `LoadError` why
nothing was (each library tried and what was wrong with it).

The library must export `ibdev`, `ibonl`, `ibwrt`, `ibrd`, `ibcmd`, `ibclr`, `ibtrg`, `ibrsp`, `ibloc`, `ibsic`,
`ibsre`, `ibtmo`, `ibconfig`, `ibask`, `ibln`, `ibwait`, `ibgts`, `ibcac`, `ibpct`, `iblines`, `ThreadIberr` and
`ThreadIbcnt`. When it also has `ibrda`, `ibwrta`, `ibcmda` and `ibstop`, transfers run asynchronously; `ibnotify`
(NI-488.2, Keysight) reports their completion, otherwise (linux-gpib, which also exports `AsyncIbsta`,
`AsyncIberr` and `AsyncIbcnt`) one polling thread checks them.

In VISA sessions, driver errors become `VisaException`s with the matching VISA status and the driver's `iberr` code
in `NativeError`; the message names it (for example "GPIB write failed: ENOL (no listeners on the bus)"), and the
GPIB library's exception (see [Without VISA](#without-visa)) is the `InnerException`:

| `iberr` | Status |
|---|---|
| `EABO` (timeout or abort) | `VisaTimeoutException` (`VI_ERROR_TMO`) |
| `ENOL` | `VI_ERROR_NLISTENERS` |
| `ECIC` | `VI_ERROR_NCIC` |
| `ESAC` | `VI_ERROR_NSYS_CNTLR` |
| `EADR`, `EARG` | `VI_ERROR_INV_PARAMETER` |
| `ENEB` | `VI_ERROR_RSRC_NFOUND` |
| `EOIP`, `EWIP` | `VI_ERROR_IN_PROGRESS` |
| `ECAP` | `VI_ERROR_NSUP_OPER` |
| `EBUS` | `VI_ERROR_BERR` |
| `ELCK` | `VI_ERROR_RSRC_LOCKED` |
| `EHDL` | `VI_ERROR_INV_OBJECT` |
| `ESRQ` | `VI_ERROR_SRQ_NOCCURRED` |
| `EPWR` | `VI_ERROR_CONN_LOST` |
| `EDVR` | `VI_ERROR_SYSTEM_ERROR` |
| others | `VI_ERROR_IO` |

No loadable library gives `VI_ERROR_LIBRARY_NFOUND`, and a device descriptor the driver refuses to open
`VI_ERROR_RSRC_NFOUND`.

You can also implement `IGpibDriver` yourself; `SimulatedGpibDriver` in `VisaCs.Simulation` is an example.

## Without VISA

The binding is in the `VisaCs.Gpib` library and can be used directly, as a managed NI-488.2 wrapper. It depends on
no other VisaCs library, and all its types are in `VisaCs.Gpib`.

```csharp
using VisaCs.Gpib;

var driver = new Ni488Driver();
if (!driver.IsAvailable) throw new InvalidOperationException(driver.LoadError);

foreach (int boardIndex in driver.ListBoards())                  // boards 0 to 3
    foreach (var (pad, sad) in driver.FindListeners(boardIndex))   // primary addresses that answer
        Console.WriteLine($"GPIB{boardIndex}::{pad}");

using var dmm = driver.OpenDevice(board: 0, primaryAddress: 22, secondaryAddress: null);
dmm.Write("*IDN?\n"u8, end: true, timeoutMilliseconds: 2000);    // end: EOI with the last byte

var response = new List<byte>();
var buffer = new byte[256];
GpibReadResult result;
do
{
    result = dmm.Read(buffer, termChar: null, timeoutMilliseconds: 2000);   // null: stop on EOI only
    response.AddRange(buffer.AsSpan(0, result.Count));
} while (!result.End && result.Count == buffer.Length);
```

`OpenDevice` does not talk to the bus, so it succeeds even when no device answers; the first write then fails with
`GpibError.NoListeners` (ENOL). New descriptors have a 10 s timeout and send EOI with the last byte; readdressing,
unaddressing and automatic serial polling keep the driver's settings (`ReaddressingEnabled` and
`UnaddressingEnabled` change the first two). `FindListeners` needs the board to be controller in charge, asks each
primary address with `ibln` (about 30 ms each) and does not scan secondary addresses.

### Reads, writes and timeouts

- `Read` ends at a byte sent with EOI, at the termination character (`termChar`, compared in all 8 bits), or when
  the buffer is full. `GpibReadResult.End` tells whether EOI or the termination character ended it.
- Every call takes its own timeout (`timeoutMilliseconds`), which the driver sets with `ibtmo`. NI-488.2 timeouts
  go in steps:

  | `timeoutMilliseconds` | NI-488.2 timeout |
  |---|---|
  | negative (`Timeout.Infinite`) | `TNONE`: no limit |
  | 0 | `T10us`, the shortest: the call fails unless it completes at once (NI-488.2 has no "don't wait") |
  | 1 to 1000000 | the next step up: `T10ms`, `T30ms`, `T100ms`, `T300ms`, `T1s`, `T3s`, `T10s`, `T30s`, `T100s`, `T300s`, `T1000s` |
  | more | `T1000s` |

  The service request waits are timed by the library itself: there 0 checks once.
- A timeout (`EABO`) throws `GpibTimeoutException`, whose `BytesTransferred` tells how much of a write was sent. A
  read that times out after some bytes arrived returns them instead, with `End` false; the next read then times out.

### Serial poll and service requests

```csharp
byte stb = dmm.SerialPoll(timeoutMilliseconds: 1000);
byte? request = dmm.WaitForServiceRequest(timeoutMilliseconds: 5000);   // null: no request within 5 s
```

`WaitForServiceRequest` returns when this device requests service: either the driver's automatic serial polling has
seen its request (RQS), or SRQ is asserted and a serial poll of the device shows bit 6. While another device holds
SRQ, the device is polled at most every 100 ms. The bus is checked every 5 ms (on the caller's thread for the
synchronous method, on a shared polling thread for the asynchronous one). A timeout of 0 checks once.

`IGpibBoard.WaitForServiceRequest` waits for SRQ itself, which the driver reports (as SRQI) only while automatic
serial polling is off; NI-488.2 turns it on by default. Prefer the device wait, which works either way.

### The board

`OpenBoard` gives the interface operations: `SendCommand` (bytes with ATN; `GpibCommand` names them),
`SendInterfaceClear`, `SetRemoteEnable`, `ControlAtn`, `PassControl`, the board's addresses, `GetLines` (which
lines the board can sense and which are asserted) and `AddressState`. Board reads and writes need the talker and
listeners addressed first:

```csharp
using var board = driver.OpenBoard(0);
board.SendCommand([GpibCommand.Unlisten, GpibCommand.Talk(0), GpibCommand.Listen(22)], timeoutMilliseconds: 1000);
board.Write("*RST\n"u8, end: true, timeoutMilliseconds: 1000);
bool? ren = board.GetLines().IsAsserted(GpibLines.Ren);   // null when the board cannot sense REN
```

All `IGpibBoard` objects of one board share its descriptor and settings; disposing one does not take the board
offline.

### Asynchronous calls and cancellation

`WriteAsync`, `ReadAsync` and `SendCommandAsync` use the library's asynchronous calls (`ibwrta`, `ibrda`,
`ibcmda`) and hold no thread while the transfer runs; `ibnotify` callbacks or one shared polling thread report
completion (see [Driver library](#driver-library)). Cancelling the token stops the transfer with `ibstop` and
throws `OperationCanceledException`.

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await dmm.WriteAsync("MEAS:VOLT?\n"u8.ToArray(), end: true, timeoutMilliseconds: 1000, cts.Token);
var reply = new byte[64];
var read = await dmm.ReadAsync(reply, termChar: (byte)'\n', timeoutMilliseconds: Timeout.Infinite, cts.Token);
```

Serial poll, clear and trigger have no asynchronous form in NI-488.2: `SerialPollAsync`, `ClearAsync` and
`TriggerAsync` queue the call to one worker thread per board, which runs that board's blocking calls in order.
Cancelling removes a queued call; once started, a call ends when the driver returns (at its timeout at the
latest). Transfers also run on that thread when the library has no asynchronous calls.
`WaitForServiceRequestAsync` is checked by the shared polling thread.

### Threads, errors and disposal

- Driver and bus errors throw `GpibException` (an `IOException`). Its `Error` names the failure, and `NativeError`
  holds the driver's `iberr`; the message names it too (for example "GPIB write failed: ENOL (no listeners on the
  bus)"):

  | `GpibError` | `iberr` |
  |---|---|
  | `SystemError` | `EDVR` |
  | `NotControllerInCharge` | `ECIC` |
  | `NoListeners` | `ENOL` |
  | `NotAddressed` | `EADR` |
  | `InvalidArgument` | `EARG` |
  | `NotSystemController` | `ESAC` |
  | `NoBoard` | `ENEB` |
  | `DmaError` | `EDMA` |
  | `AsyncInProgress` | `EOIP`, or a second asynchronous transfer on a descriptor (no `iberr`) |
  | `NotSupported` | `ECAP` |
  | `FileSystemError` | `EFSO` |
  | `BusError` | `EBUS` |
  | `StatusByteOverflow` | `ESTB` |
  | `SrqStuck` | `ESRQ` |
  | `TableProblem` | `ETAB` |
  | `Locked` | `ELCK` |
  | `NotifyRearmFailed` | `EARM` |
  | `InvalidDescriptor` | `EHDL` |
  | `WaitInProgress` | `EWIP` |
  | `EventReset` | `ERST` |
  | `PowerLost` | `EPWR` |
  | `Unknown` | any other `iberr` |
  | `LibraryNotFound` | no compatible library was loaded (no `iberr`) |
  | `DeviceOpenFailed` | `ibdev` refused the device (with its `iberr`) |

  `EABO` (timeout or abort) throws `GpibTimeoutException` instead.
- One `Ni488Driver` per process is enough (a `ResourceManager` exposes its own as `GpibDriver`); it is thread safe,
  starts its helper threads on demand, stops them when idle, and needs no disposal.
- A device or board object is not safe for concurrent calls: run one operation at a time on it. Different devices
  can be used from different threads. A second asynchronous transfer on the same descriptor fails with
  `GpibError.AsyncInProgress`.
- Disposing a device takes its descriptor offline (`ibonl`). It ends queued and running asynchronous operations with
  `ObjectDisposedException` (a running transfer is stopped first), and any later call throws
  `ObjectDisposedException`. Do not dispose while a synchronous call on the same object runs on another thread.
- Invalid addresses (outside 0 to 30) throw `ArgumentOutOfRangeException`.

## GPIB INSTR

- `Read`/`Write` use `ibrd`/`ibwrt` (or their asynchronous forms); END is EOI, and the termination character is
  passed to the driver.
- Each driver call gets the time left of the session timeout, rounded up to the driver's next step (for example a
  2 s timeout lets the driver wait up to 3 s).
- `ReadStatusByte` serial-polls (`ibrsp`), `Clear` sends Selected Device Clear (`ibclr`), `AssertTrigger`
  sends Group Execute Trigger (`ibtrg`), `ControlRen` handles REN, GTL and LLO.
- `PrimaryAddress`, `SecondaryAddress`, `ReaddressingEnabled` (`VI_ATTR_GPIB_READDR_EN`),
  `UnaddressingEnabled` (`VI_ATTR_GPIB_UNADDR_EN`) and `RenState`.
- Service requests: while the event is enabled, the session waits for this device's request as described in
  [Serial poll and service requests](#serial-poll-and-service-requests), without holding a thread. The event
  carries the status byte. Requests of other devices on the bus do not raise it.

## GPIB INTFC

The interface session acts as the controller:

| Member | Operation |
|---|---|
| `SendCommand(bytes)` | `viGpibCommand` (`ibcmd`, bytes sent with ATN; `GpibCommand` names them) |
| `SendInterfaceClear()` | `viGpibSendIFC` (`ibsic`) |
| `ControlAtn(mode)` | `viGpibControlATN` (`ibgts`/`ibcac`) |
| `PassControl(pad, sad)` | `viGpibPassControl` (`ibpct`), within the session timeout |
| `ControlRen(mode)` | `viGpibControlREN` (`ibsre` plus GTL/LLO commands) |
| `Clear()` | sends DCL (Device Clear to all devices) |
| `AssertTrigger()` | sends GET to the addressed listeners |
| `Read`/`Write` | board-level `ibrd`/`ibwrt` (address devices with `SendCommand` first) |

Line states (`AtnState`, `SrqState`, `RenState`, `NdacState`) come from `iblines`; `IsControllerInCharge`,
`IsSystemController`, `AddressState` (a `GpibAddressState`) and the board's own addresses are also available.
`GpibAtnMode` and `GpibAddressState` are in the `VisaCs.Gpib` namespace.

Service request events fire when SRQ becomes asserted; serial-poll the requesting device to release it. While SRQ
stays asserted the event repeats once a second, because SRQ is sampled and a quick release followed by a new
request would otherwise go unnoticed. The board sees SRQ only while automatic serial polling is off (see above).
CIC, talk and listen events are not implemented.

## Discovery

`Find("GPIB?*")` checks boards 0 to 3 and, on each, asks every primary address (except the board's own) with
`ibln` whether a listener is present. Secondary addresses are not scanned. Each board also appears as
`GPIBn::INTFC`.

## Testing without hardware

`tests/Native/fake_ni4882.c` is a stand-in library with the NI-488.2 C API and two simulated instruments.
Build it with `tests/Native/build-fake-gpib.sh` (gcc/clang) or `build-fake-gpib.cmd` (MSVC) and run the tests
with `VISACS_TEST_GPIB_LIBRARY` set to the built library; the build makes three variants (NI-like, without
`ibnotify` like linux-gpib, and without asynchronous calls). Test hooks in the library hide SRQI as NI-488.2 does
while autopolling, lose completion notifications, and let a callback's return value undo a concurrent `ibnotify`.
That last case is how NI-488.2 might behave (it rearms a notification with the callback's return value); we could
not confirm it, and the driver copes with it either way. No real GPIB board has been tested yet.

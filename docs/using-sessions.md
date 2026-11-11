# Using sessions

This guide covers what every session type has in common: resource names, opening sessions, reading and
writing, device operations, events, locking, attributes, errors and async use. The interface guides
([TCP/IP](tcpip.md), [USB](usb.md), [serial](serial.md), [GPIB](gpib.md), [PXI](pxi.md)) cover the rest.

## Resource manager

```csharp
using var rm = new ResourceManager();                 // viOpenDefaultRM
using var rm2 = new ResourceManager(new ResourceManagerOptions
{
    DefaultTimeoutMilliseconds = 5000,
    EnableLanDiscovery = false,
});
```

Disposing a `ResourceManager` closes every session it opened. Several resource managers can exist in one
process; locks are shared between all of them (see [Locking](#locking)).

`ResourceManagerOptions` holds every setting: default timeouts, ports (VXI-11 portmapper, HiSLIP, VICP),
discovery switches, the USB backend, the GPIB driver, HiSLIP security, the PCI sysfs path, aliases and static
resources. Each property is documented in IntelliSense. The resource manager reads the options when it uses them;
the USB backend and GPIB driver are chosen once, on first use. A USB backend you set yourself stays yours to
dispose.

## Resource names

| Interface | Form | Examples |
|---|---|---|
| VXI-11 | `TCPIP[board]::host[::device][::INSTR]` | `TCPIP::192.168.1.5::INSTR`, `TCPIP0::scope.local::inst0::INSTR`, `TCPIP::10.0.0.2::gpib0,5::INSTR` |
| HiSLIP | `TCPIP[board]::host::hislipN[,port][::INSTR]` | `TCPIP::192.168.1.5::hislip0::INSTR`, `TCPIP::host::hislip0,4881::INSTR` |
| Raw socket | `TCPIP[board]::host::port::SOCKET` | `TCPIP::192.168.1.5::5025::SOCKET` |
| VICP | `VICP[board]::host[::INSTR]` | `VICP::192.168.1.9::INSTR` |
| USB | `USB[board]::vid::pid::serial[::interface][::INSTR\|RAW]` | `USB::0x0957::0x1796::MY12345678::INSTR`, `USB0::0x1AB1::0x04CE::DS1ZA1::0::RAW` |
| Serial | `ASRL[board\|device][::INSTR]` | `ASRL3::INSTR`, `ASRLCOM3::INSTR`, `ASRL/dev/ttyUSB0::INSTR` |
| GPIB | `GPIB[board]::primary[::secondary][::INSTR]`, `GPIB[board]::INTFC` | `GPIB0::22::INSTR`, `GPIB1::5::7::INSTR`, `GPIB0::INTFC` |
| PXI | `PXI[board]::bus-device[.function][::INSTR]` and variants, see [PXI](pxi.md) | `PXI0::3-18.0::INSTR` |

Names are case-insensitive. IPv6 hosts go in brackets (`TCPIP::[fe80::1%3]::INSTR`). `rm.Parse(name)`
(viParseRsrcEx) returns a typed `ResourceName` with the canonical name, interface type, board and class. Two
`ResourceName`s are equal when their canonical names are, so `TCPIP::host::INSTR` equals
`TCPIP0::HOST::inst0::INSTR`. On Windows `ASRLCOM3` is the same port as `ASRL3` and has its canonical name.

Aliases map a short name to a resource name and work everywhere a resource name is accepted:

```csharp
rm.Options.Aliases["dmm"] = "TCPIP::192.168.1.20::hislip0::INSTR";
using var dmm = rm.Open("dmm");
```

## Finding resources

```csharp
IReadOnlyList<string> names = rm.Find();                                         // viFindRsrc, "?*::INSTR"
IReadOnlyList<ResourceInfo> found = rm.FindResources("USB?*INSTR");             // with descriptions and attributes
rm.Find("USB?*INSTR{VI_ATTR_MANF_ID==0x0957 && VI_ATTR_MODEL_CODE!=0x1796}");    // attribute filter
var lan = await rm.FindAsync("TCPIP?*", token);                                  // LAN discovery holds no thread
```

Find expressions use the VISA syntax: `?` any character, `*` repeat, `[...]` sets, `|` alternatives, and an
optional `{...}` attribute expression with `==`, `!=`, `<`, `>`, `<=`, `>=`, `&&`, `||` and `!`.

Sources: LAN (VXI-11 portmapper broadcast and mDNS for `_hislip._tcp`, `_vxi-11._tcp` and `_scpi-raw._tcp`),
USB devices, the operating system's serial ports, GPIB listeners found with `ibln`, PXI functions available to
user space, and `ResourceManagerOptions.StaticResources`. Sources the expression cannot match are skipped, and a
source that fails is left out. LAN discovery listens for `LanDiscoveryTimeoutMilliseconds` (1 s by default) while
the local sources are enumerated; turn it off with `EnableLanDiscovery = false` and `EnableMdnsDiscovery = false`,
or add unicast targets to `LanDiscoveryTargets`.

## Opening sessions

```csharp
MessageBasedSession s = rm.Open("TCPIP::192.168.1.5::INSTR");          // any message-based resource
var hislip = rm.Open<TcpipHislipSession>("TCPIP::host::hislip0::INSTR"); // typed, throws if the type differs
VisaSession any = rm.Open<VisaSession>("PXI0::3-18.0::INSTR");         // any resource, incl. register-based
using var locked = rm.Open("GPIB0::22::INSTR", AccessModes.ExclusiveLock, openTimeoutMilliseconds: 5000);

await using var dmm = await rm.OpenAsync("TCPIP::192.168.1.20::hislip0::INSTR", token);   // asynchronous forms
await using var mine = await rm.OpenAsync("GPIB0::22::INSTR", AccessModes.ExclusiveLock, 5000, token);
```

Two timeouts apply when opening. `ResourceManagerOptions.ConnectTimeoutMilliseconds` (5 s by default) bounds
setting up the connection (TCP connect, protocol handshake). `openTimeoutMilliseconds` (2 s by default) only
bounds waiting for the lock requested with `AccessModes.ExclusiveLock` or `AccessModes.SharedLock`.

| Resource | Session class |
|---|---|
| TCPIP INSTR, VXI-11 | `TcpipVxi11Session` |
| TCPIP INSTR, HiSLIP | `TcpipHislipSession` |
| TCPIP SOCKET | `TcpipSocketSession` |
| VICP INSTR | `VicpSession` |
| USB INSTR | `UsbSession` |
| USB RAW | `UsbRawSession` |
| ASRL INSTR | `SerialSession` |
| GPIB INSTR | `GpibSession` |
| GPIB INTFC | `GpibInterfaceSession` |
| PXI INSTR | `PxiSession` (register-based, not a `MessageBasedSession`) |

Sessions are thread-safe. Operations that talk to the instrument run one at a time per session: a call made while
another is running waits for it to finish. Waiting calls are not guaranteed to run in the order they were made,
so a thread that needs a write and a read to stay together should use one call (`Query`) or a lock of its own.
Locking, event and attribute members do not wait for a running operation. Different sessions can be used in
parallel. Dispose a session (or call `Close`, or `await DisposeAsync()`) to release it; an operation still running
on another thread then fails with a `VisaException`, and later calls throw `VI_ERROR_INV_OBJECT`.

## Writing and reading

```csharp
s.WriteLine("CONF:VOLT:DC 10");            // appends WriteTermination ("\n" by default)
s.Write("*RST\n");                         // text as is
s.Write(bytes);                            // viWrite
string reply = s.Query("*IDN?");           // WriteLine + ReadLine
double v = s.Query<double>("MEAS:VOLT?");  // any ISpanParsable<T>: int, long, decimal, bool ("1"/"0"), ...
long n = s.Query<long>("TRIG:COUNT?");     // integers sent as "1.000E+00" are accepted (rounded)
s.QueryDelay = TimeSpan.FromMilliseconds(50);   // wait between a query's write and its read

byte[] all = s.ReadToEnd();                // until END or the termination character
string text = s.ReadString();              // ReadToEnd decoded with Encoding (Latin-1 by default)
string line = s.ReadLine();                // ReadString without trailing CR/LF
ReadResult r = s.Read(buffer);             // viRead: one call, up to buffer.Length bytes (r.Count, r.Status)
byte[] some = s.ReadBytes(100);            // up to 100 bytes
```

A read stops when one of these happens, as in VISA:

- the device signals END (VXI-11 and HiSLIP END, USBTMC EOM, GPIB EOI, VICP EOI, serial `EndInput`);
- the termination character is read and `TerminationCharacterEnabled` is true (`TerminationCharacter`,
  default `\n`);
- the requested count is reached.

`ReadResult.Status` reports which one (`EndReceived`, `TerminationCharacterEncountered`, `MaximumCountReached`).
`SendEndEnabled` controls whether writes end with END, and `SuppressEndEnabled` makes reads ignore END.

### Numbers, arrays and binary blocks

```csharp
double[] values = s.QueryAsciiValues("TRAC:DATA?");                  // "1.5,2.0,..." with any ISpanParsable<T>
int[] counts = s.QueryAsciiValues<int>("DATA:ASCII? 10");
s.WriteAsciiValues("SOUR:LIST:VOLT ", new[] { 1.0, 2.0, 3.0 });
s.AsciiSeparator = ';';                                              // ',' by default

byte[] block = s.QueryBinaryBlock("DISP:DATA?");                     // IEEE 488.2 #<n><len><data> or #0...
float[] wave = s.QueryBinaryValues<float>("CURV?");
s.BinaryByteOrder = ByteOrder.BigEndian;                             // to match FORM:BORD NORM; little-endian by default
short[] raw = s.QueryBinaryValues<short>("WAV:DATA?");
s.WriteBinaryValues("SOUR:ARB:DATA ", samples);                      // writes a definite-length block
```

A value that does not parse throws `VI_ERROR_INV_FMT`; a block whose message ends before its announced length
throws `VI_ERROR_IO`.

### Formatted I/O

`Printf`, `Scanf` and `Queryf` implement the VISA format language (viPrintf, viScanf, viQueryf):

```csharp
s.Printf("VOLT %.3f;:CURR %d\n", 1.5, 2);
string vendor = s.Queryf<string>("*IDN?\n", "%[^,]");
object?[] idn = s.Queryf("*IDN?\n", "%[^,],%[^,],%[^,],%t");
double[] list = s.Queryf<double[]>("DATA:ASCII? %d\n", "%,#f", 100);
double[] first = s.Queryf<double[]>("DATA:ASCII? %d\n", "%,*f", 100, 10);   // leftover arguments feed the read format
byte[] data = s.Queryf<byte[]>("DISP:DATA?\n", "%b");
await s.PrintfAsync("VOLT %.3f\n", 1.5, token);                     // a token as the last argument cancels
```

Supported: flags `- + space # 0`, `*` width/precision, array modifiers `,N`, `,*` and `,#`, IEEE 488.2 number
formats `@1 @2 @3`, size modifiers `h l L ll`, binary modifiers `z Z !ob !ol`, conversions
`d i u o x X f e E g G s c b B y %`, and for reading `[set]`, `t` and `T`. In the asynchronous forms the
`CancellationToken` comes before the format arguments (`PrintfAsync(format, token, args)`), or as the last format
argument.

### Without a session

The block, value-list and format helpers are in the standalone `VisaCs.Formatting` library (namespace
`VisaCs.Formatting`, which also holds `ByteOrder`; no other dependencies), for messages sent over a
`NativeSerialPort`, a GPIB device or any other transport:

```csharp
byte[] data = Ieee4882.ParseBlock(response);                        // skips text before '#', checks the length
float[] wave = Ieee4882.BytesToValues<float>(data, ByteOrder.BigEndian);
byte[] block = Ieee4882.BuildBlock(Ieee4882.ValuesToBytes<float>(wave, ByteOrder.LittleEndian));
double[] trace = Ieee4882.ParseValues<double>("1.5E+00,-2.0E-01\n");  // same parsing as QueryAsciiValues
byte[] command = VisaFormatter.Format("VOLT %.3f;:CURR %d\n", [1.5, 2]);
object?[] idn = VisaFormatter.Parse("%[^,],%[^,]", reply);
```

`Ieee4882.TryParseBlockHeader` reads just the header, for code that reads a block piece by piece.

Used on their own, these throw `FormatException` for malformed data, formats or arguments, and
`NotSupportedException` for an unknown conversion. Through a session the same errors become a `VisaException`
with `VI_ERROR_INV_FMT` or `VI_ERROR_NSUP_FMT`, with the original exception as `InnerException`.

## Device operations

```csharp
byte stb = s.ReadStatusByte();     // viReadSTB
s.Clear();                         // viClear
s.AssertTrigger();                 // viAssertTrigger
s.ControlRen(RemoteLocalMode.AssertAddressLocalLockout);   // viGpibControlREN
s.Flush(BufferTypes.ReadBuffer | BufferTypes.InBufferDiscard);   // viFlush
```

How each interface implements these, and which do not support them, is listed in the
[support matrix](support-matrix.md#operations-by-session-type). Unsupported operations throw a
`VisaException` with `VI_ERROR_NSUP_OPER`. `Flush` discards received input for the read flags, waits for written
data to leave the transport for `WriteBuffer`/`OutBuffer`, and drops unsent data for `OutBufferDiscard` where the
transport buffers it (serial ports).

## Timeouts and errors

`TimeoutMilliseconds` (`VI_ATTR_TMO_VALUE`) bounds each transfer, not a whole call: `Query` may wait up to the
timeout for its write and again for its read, and `ReadToEnd` up to the timeout for each `ReadChunkSize` chunk.
`-1` (or `Timeout.InfiniteTimeSpan` through `Timeout`) waits forever. The default comes from
`ResourceManagerOptions.DefaultTimeoutMilliseconds` (2000 ms).

Errors are thrown as `VisaException`. `Status` holds the VISA status code (`VisaStatus` values match
`visa.h`) and `StatusName` its name, e.g. `VI_ERROR_RSRC_LOCKED`:

| Exception | When |
|---|---|
| `VisaTimeoutException` | `VI_ERROR_TMO`. `BytesTransferred` tells how much of a read arrived. |
| `VisaSecurityException` | HiSLIP 2.0 TLS or authentication failures, `VI_ERROR_NPERMISSION`. `Failure` says which. |
| `VisaException` | everything else (`VI_ERROR_CONN_LOST`, `VI_ERROR_RSRC_NFOUND`, `VI_ERROR_RSRC_LOCKED`, `VI_ERROR_INV_OBJECT` after the session was closed, `VI_ERROR_INV_FMT` for responses that do not parse, ...). |
| `OperationCanceledException` | the `CancellationToken` of an asynchronous call was cancelled. |

`VisaStatusInfo.GetName` and `GetDescription` turn any status code into its `visa.h` name and description.
`VisaException.NativeError` holds the driver's or operating system's own error code when known (for example the
NI-488.2 `iberr` of a failed GPIB call).

After a timeout the instrument may still send the late reply. Call `Clear()` before the next command, as you
would with any VISA.

## Async and cancellation

Every operation that talks to an instrument has an asynchronous form. It takes a `CancellationToken` as its last
parameter, so `MethodAsync(arguments, token)` always works: `OpenAsync`, `FindAsync`, `FindResourcesAsync`,
`QueryAsync`, `QueryAsync<T>`, `WriteAsync`, `WriteLineAsync`, `ReadAsync`, `ReadLineAsync`, `ReadToEndAsync`,
`ReadBinaryValuesAsync<T>`, `QueryBinaryValuesAsync<T>`, `WriteBinaryValuesAsync<T>`, `QueryAsciiValuesAsync`,
`ClearAsync`, `ReadStatusByteAsync`, `AssertTriggerAsync`, `FlushAsync`, `LockExclusiveAsync`, `LockSharedAsync`,
`UnlockAsync`, `EnableEventAsync`, `DisableEventAsync`, `WaitOnEventAsync`, `TryWaitOnEventAsync` and the
session-specific ones (`DoCommandAsync`, `ControlInAsync`, `SendCommandAsync`, `SendBreakAsync`, ...).
`PrintfAsync`, `ScanfAsync` and `QueryfAsync` take the token before their format arguments, or as the last format
argument. `DisposeAsync` takes none.

```csharp
await using var rm = new ResourceManager();
await using var dmm = await rm.OpenAsync("TCPIP::192.168.1.20::hislip0::INSTR");
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
string idn = await dmm.QueryAsync("*IDN?", cts.Token);
float[] wave = await dmm.QueryBinaryValuesAsync<float>("CURV?", cts.Token);
```

They use asynchronous I/O all the way down and hold no thread while they wait, so a program can keep many
instruments busy at once without a thread per instrument:

| Interface | How an asynchronous call waits |
|---|---|
| VXI-11, HiSLIP, raw socket, VICP | `Socket.ReceiveAsync`/`SendAsync` (I/O completion ports on Windows, epoll on Linux). HiSLIP over TLS decrypts in an asynchronous reader loop; the VXI-11 service request channel is an asynchronous RPC server. |
| USB (USBTMC, RAW) | WinUSB overlapped transfers completed on the I/O completion port; Linux usbfs URBs, collected when the device file reports them (one shared poll thread); libusb asynchronous transfers (one libusb event thread). |
| Serial | Overlapped `ReadFile`/`WriteFile` completed on the I/O completion port (Windows); non-blocking termios I/O woken by one shared poll thread (Linux, macOS). |
| GPIB | NI-488.2 asynchronous transfers (`ibrda`, `ibwrta`, `ibcmda`) completed by `ibnotify` callbacks, or checked by one shared polling thread when the library has no `ibnotify` (linux-gpib). Serial poll, device clear and trigger have no asynchronous form in NI-488.2; they run on one worker thread per board. |
| Events, locks | `WaitOnEventAsync` and `LockExclusiveAsync` wait on signals, not threads. |
| Finding | LAN discovery waits on sockets; the local enumerations (USB, serial, GPIB, PXI) run on a thread-pool thread. |

The synchronous methods are synchronous I/O, not wrappers around the asynchronous ones, so they do not depend on
the thread pool, with a few exceptions: `Find` runs LAN discovery on thread-pool threads, and on HiSLIP the
asynchronous channel, TLS and connection setup complete on the thread pool (see
[Timeouts, cancellation and threads](tcpip.md#timeouts-cancellation-and-threads)). Both forms share one
implementation and behave the same. Synchronous calls cannot be cancelled; they end at their timeout.

The session timeout applies to every transfer of an asynchronous call too (`VisaTimeoutException`); the token adds
cancellation. Cancelling stops the transfer in progress and throws `OperationCanceledException`:

- VXI-11 also sends `device_abort` on the abort channel, so the instrument ends the pending call (lock requests too).
- USBTMC sessions run the USBTMC abort sequence (INITIATE_ABORT_BULK_IN/OUT), as after a timeout.
- GPIB stops the transfer with `ibstop`. Serial poll, clear and trigger, and every call on a driver library
  without `ibrda`, are blocking driver calls: once started they end at their own timeout.
- The other protocols stop the read or write where it is, as if the timeout had expired.

Treat the session as after a timeout: the instrument may still send the reply, so call `Clear()` before the next
command. A write cut off part-way on VXI-11 or HiSLIP closes the connection instead: the instrument would read the
next command as the rest of the cut-off message, so later calls throw `VI_ERROR_CONN_LOST`; reopen the session.
Cancelling a call that is still waiting for its turn on the session (another operation is running) just removes
it; the running operation is not disturbed.

## Events

Service requests (and USB interrupt packets) are delivered through the VISA queue and handler mechanisms:

```csharp
// Handler mechanism: .NET events
s.ServiceRequest += (_, e) => Console.WriteLine($"SRQ, status byte {e.StatusByte}");
s.EnableEvent(EventType.ServiceRequest, EventMechanism.Handler);

// Queue mechanism
s.EnableEvent(EventType.ServiceRequest, EventMechanism.Queue);
VisaEventArgs e = s.WaitOnEvent(EventType.ServiceRequest, 10_000);   // throws VisaTimeoutException
if (s.TryWaitOnEvent(EventType.ServiceRequest, 0, out var pending)) { /* ... */ }
var later = await s.WaitOnEventAsync(EventType.ServiceRequest, 10_000, token);   // holds no thread while waiting
VisaEventArgs? maybe = await s.TryWaitOnEventAsync(EventType.ServiceRequest, 100, token);   // null on timeout

s.DisableEvent(EventType.ServiceRequest);
s.DiscardEvents(EventType.ServiceRequest);
```

`EventMechanism.All` enables both. `SuspendHandler` holds events and delivers them to the handlers when
`Handler` is enabled. The queue holds `MaximumQueueLength` events (default 50); when it is full, new events are
dropped. `SupportedEventTypes` lists what a session can deliver; the per-session list is at the end of
[attributes.md](attributes.md).

Handlers run on a thread-pool thread, one at a time and in the order the events arrived. An exception thrown by a
handler is caught and discarded. A handler may call the session's methods; events already dispatched can still
arrive shortly after `DisableEvent` or `Dispose`.

`ServiceRequestEventArgs.StatusByte` is filled when the protocol delivers it with the request (HiSLIP, USB488)
and on GPIB INSTR sessions, which read it with the serial poll that answers the request. Otherwise (VXI-11, VICP)
call `ReadStatusByte()` in the handler.

## Locking

```csharp
s.LockExclusive(timeoutMilliseconds: 2000);        // viLock(VI_EXCLUSIVE_LOCK)
string key = s.LockShared(2000);                   // returns the access key
other.LockShared(2000, key);                       // another session joins the shared lock
s.Unlock();
await s.LockExclusiveAsync(2000, token);           // waits for the lock without holding a thread
await s.UnlockAsync(token);
```

Locks nest, like VISA's: each `Lock*` call needs a matching `Unlock`. While another session holds the lock, I/O
fails at once with `VI_ERROR_RSRC_LOCKED`; `Lock*` calls wait up to their timeout for the lock to be released and
then throw `VisaTimeoutException`.

Locks cover every session in the process (all resource managers) that opened the same canonical resource name.
On HiSLIP (exclusive and shared) and VXI-11 (exclusive only; VXI-11 has no shared locks) the session also takes
the instrument's own lock, so clients on other
computers, and sessions that name the instrument differently (host name instead of IP address), are excluded too.
`AccessModes.ExclusiveLock` and `AccessModes.SharedLock` lock at open time.

## Attributes

Each session supports the VISA attributes that apply to it, as typed properties and through
`GetAttribute`/`SetAttribute`:

```csharp
s.SetAttribute(VisaAttribute.TerminationCharacterEnabled, true);
int port = s.GetAttribute<int>(VisaAttribute.TcpipPort);
bool supported = s.IsAttributeSupported(VisaAttribute.UsbSerialNumber);
foreach (var a in s.SupportedAttributes) Console.WriteLine($"{VisaAttributeNames.GetName(a)} = {s.GetAttribute(a)}");
```

`GetAttribute` returns the value as the type documented on the `VisaAttribute` member and listed in
[attributes.md](attributes.md): VISA enumerations as their .NET enum (`HardwareInterfaceType`, `LineState`, ...),
flags as `bool`, strings as `string`, numbers as `int` or `long`. `GetAttribute<T>` converts to another numeric
type or enum, and `SetAttribute` converts numbers and enum names to the attribute's type.

Unsupported attributes throw `VI_ERROR_NSUP_ATTR`, read-only ones `VI_ERROR_ATTR_READONLY`, and invalid values
`VI_ERROR_NSUP_ATTR_STATE`. `VisaAttributeNames.GetName` and `TryParse` convert between enum members and
`VI_ATTR_*` names. The complete table of attributes per session type is in [attributes.md](attributes.md).

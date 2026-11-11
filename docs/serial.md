# Serial instruments

```csharp
using VisaCs.Serial;   // SerialParity, SerialStopBitsMode, SerialFlowControlModes

using var s = rm.Open<SerialSession>("ASRL3::INSTR");
s.BaudRate = 115200;
s.Parity = SerialParity.None;
s.FlowControl = SerialFlowControlModes.RtsCts;
Console.WriteLine(s.Query("*IDN?"));
```

Serial ports are driven directly through the Win32 communications API (overlapped I/O) on Windows and
termios on Linux and macOS. `System.IO.Ports` is not used. The port itself is the standalone `VisaCs.Serial`
library, which also works [without VISA](#without-visa).

## Port names

| Resource | Windows | Linux / macOS |
|---|---|---|
| `ASRLn::INSTR` | `COMn` | `/dev/ttyS(n-1)` |
| `ASRLCOMn::INSTR` | `COMn` | — |
| `ASRL/dev/...::INSTR` | — | that device, e.g. `ASRL/dev/ttyUSB0::INSTR`, `ASRL/dev/cu.usbserial-1410::INSTR` |

On macOS use the device path; `ASRLn` maps to `/dev/ttyS(n-1)`, which macOS does not have. `PortName` returns
the operating-system name. `Find` lists the ports in `HKLM\HARDWARE\DEVICEMAP\SERIALCOMM` on Windows, the
`/sys/class/tty` devices backed by hardware on Linux (without the placeholder `ttyS` ports that the 8250 driver
registers whether or not a UART exists), and `/dev/cu.*` on macOS.

Ports are opened exclusively: Windows always does this, and on Linux and macOS the port sets `TIOCEXCL` (which
root can still override). On Linux the user needs access to the device, usually by being in the `dialout` (or
`uucp`) group; otherwise opening fails with `VI_ERROR_NPERMISSION`.

## Settings

| Property | Attribute | Notes |
|---|---|---|
| `BaudRate` | `VI_ATTR_ASRL_BAUD` | Any rate the hardware accepts. Linux sets every rate through `termios2` (`BOTHER` for non-standard ones); macOS uses `IOSSIOSPEED` for non-standard rates. |
| `DataBits` | `VI_ATTR_ASRL_DATA_BITS` | 5 to 8 |
| `Parity` | `VI_ATTR_ASRL_PARITY` | None, odd, even, mark, space. macOS has no mark/space. |
| `StopBits` | `VI_ATTR_ASRL_STOP_BITS` | One, 1.5, two. 1.5 needs 5 data bits on Windows; termios has no 1.5 setting and uses two. |
| `FlowControl` | `VI_ATTR_ASRL_FLOW_CNTRL` | None, XON/XOFF, RTS/CTS, DTR/DSR (Windows only), combinations |
| `XOnCharacter`, `XOffCharacter` | `VI_ATTR_ASRL_XON_CHAR`, `..._XOFF_CHAR` | |
| `ReplacementCharacter` | `VI_ATTR_ASRL_REPLACE_CHAR` | Windows only; termios reads errored bytes as NUL |
| `DiscardNull` | `VI_ATTR_ASRL_DISCARD_NULL` | Drops received NUL bytes (in the driver on Windows, in VisaCs on Unix) |
| `EndInput` | `VI_ATTR_ASRL_END_IN` | How reads detect END: termination character (default), last bit, none |
| `EndOutput` | `VI_ATTR_ASRL_END_OUT` | What writes add as END: none (default), last bit, termination character, break |
| `BreakLength` | `VI_ATTR_ASRL_BREAK_LEN` | At least 1 ms; used by `SendBreak()`, `EndOutput = Break` and `Clear` |
| `AllowTransmit` | `VI_ATTR_ASRL_ALLOW_TRANSMIT` | false suspends transmission as if XOFF was received |

Opening the session applies the VISA defaults (9600 baud, 8 data bits, no parity, one stop bit, no flow control)
and asserts DTR and RTS. A setting takes effect on the open port at once; the setter waits for an I/O operation in
progress to finish. Values out of range, and settings the platform or the port rejects, throw
`VI_ERROR_NSUP_ATTR_STATE` and leave the previous value in place.

With `EndOutput = LastBit`, the highest data bit (bit 7 with 8 data bits) is cleared in every byte written and set
in the last byte of a write that sends END.

## Modem lines and break

`RtsState` and `DtrState` can be set (unless the matching hardware flow control owns the line: Windows then throws
`VI_ERROR_NSUP_ATTR_STATE`, Linux and macOS let the driver keep control). On Windows their getters return the last
value set, since the comm API cannot read the outputs back. `CtsState`, `DsrState`, `DcdState` and `RiState` read
the inputs. `BreakState` holds a break; `SendBreak()` sends one of `BreakLength` milliseconds. `BytesAvailable`
(`VI_ATTR_ASRL_AVAIL_NUM`) is the number of bytes waiting. The line properties act at once, even during a transfer.

## Reads, writes and device operations

With the default `EndInput`, a read ends at the termination character (`\n`), at the count, or on timeout.
Parity, framing and overrun errors are not reported as errors: on Windows a byte received with a parity error
arrives as received (or as `ReplacementCharacter`), and termios delivers it as NUL. A disconnected device (an
unplugged USB-serial adapter) ends reads and writes with `VI_ERROR_CONN_LOST`.

Serial has no status byte, trigger or clear on the wire, so:

- `ReadStatusByte` sends `*STB?` and parses the reply;
- `AssertTrigger` sends `*TRG\n`;
- `Clear` discards the output buffer, sends a break of `BreakLength` (or `*CLS\n` when `IOProtocol` is
  `Ieee4882Strings`), then discards the input buffer;
- `Flush` with `InBuffer`/`InBufferDiscard` discards the driver's input; with `OutBuffer` it waits, within the
  session timeout, until the driver has sent everything written; with `OutBufferDiscard` it drops data still
  waiting in the transmit buffer.

Asynchronous calls hold no thread while they wait: overlapped I/O completes on the I/O completion port on
Windows, and on Linux and macOS one shared poll thread wakes them. Cancelling one cancels the pending I/O at
once (`CancelIoEx` on Windows).

## Without VISA

The port is in the `VisaCs.Serial` library, which depends on no other VisaCs package and works without a resource
manager or sessions. All its types are in namespace `VisaCs.Serial`:

```csharp
using VisaCs.Serial;   // NativeSerialPort, SerialSettings, SerialParity, SerialPortException, SerialTimeoutException

foreach (var name in NativeSerialPort.GetPortNames())
    Console.WriteLine(name);

// Open applies the settings (SerialSettings.Default when none are given) and asserts DTR and RTS.
using var port = NativeSerialPort.Open("COM3",                       // or /dev/ttyUSB0, /dev/cu.usbserial-...
    SerialSettings.Default with { BaudRate = 115200, Parity = SerialParity.Even });
port.Write("*IDN?\n"u8, timeoutMilliseconds: 1000);

var buffer = new byte[256];
int n = port.Read(buffer, timeoutMilliseconds: 2000);        // returns once at least one byte has arrived
port.Drain(timeoutMilliseconds: 1000);                      // waits until everything written has been sent
```

`Read` returns whatever has arrived; termination characters, END handling and the VISA attributes are what
`SerialSession` adds on top. `Configure` changes the settings later, and `Settings` returns the ones in effect.

**Timeouts.** Every `Read`, `Write` and `Drain` takes its own timeout in milliseconds. `Timeout.Infinite` (-1, or
any negative value) waits without limit; 0 does only what is possible at once (a read returns the bytes that have
already arrived). Running out of time throws `SerialTimeoutException` (a `TimeoutException`); for a write,
`BytesTransferred` tells how much was sent.

**Asynchronous calls.** `ReadAsync`, `WriteAsync` and `DrainAsync` do the same without holding a thread.
Cancelling their token ends them with `OperationCanceledException`; a cancelled read takes no data from the port.

**Abort and Dispose.** `Abort` ends every read, write and drain in progress, on any thread, with
`SerialPortException` (`SerialPortError.Aborted`); calls that start afterwards are not affected. A blocked
synchronous call notices it at once on Windows and within 200 ms on Linux and macOS. `Dispose` does the same
(`SerialPortError.Closed`) and closes the port; afterwards every member except `Dispose`, `Abort`, `PortName` and
`Settings` throws `ObjectDisposedException`.

**Threads.** All members may be called from any thread. A read and a write can run at the same time (each
synchronous or asynchronous), and the line members, `Configure`, `Abort` and the discard methods may be used while
transfers are in progress. Several reads at once are allowed, but each gets an unpredictable part of the data.

**Errors.** Device and OS errors are `SerialPortException`s (derived from `IOException`). Their `Error` is one of
`NotFound` (no such port, or not a serial port), `InUse` (open elsewhere; on Windows also access denied),
`AccessDenied` (Linux and macOS), `UnsupportedSetting` (a setting the port rejects), `Disconnected` (device
unplugged), `Aborted`, `Closed`, `IOError` and `SystemError`, and `NativeError` is the errno or Win32 error when
known. Settings outside their range (baud rate below 1, data bits outside 5 to 8) throw
`ArgumentOutOfRangeException`, and `Open` throws `PlatformNotSupportedException` on other operating systems.
Through a `SerialSession` the same failures are `VisaException`s: `ErrorResourceNotFound`, `ErrorResourceBusy`,
`ErrorNoPermission`, `ErrorAttributeStateNotSupported`, `ErrorConnectionLost`, `ErrorAbort` (for both `Aborted`
and `Closed`), `ErrorIO`, `ErrorSystemError` and `ErrorOperationNotSupported`, with the serial exception as
`InnerException`.

**Platform differences.**

| | Windows | Linux | macOS |
|---|---|---|---|
| Non-standard baud rates | if the driver accepts them | yes (`termios2`) | `IOSSIOSPEED`, if the driver accepts them |
| Mark/space parity | yes | yes | no |
| 1.5 stop bits | with 5 data bits | sends two | sends two |
| DTR/DSR flow control | yes | no | no |
| `ReplacementCharacter` | yes | no (errored bytes read as NUL) | no |
| `DiscardOutput` during a write | ends the write with `VI_ERROR_ABORT` | the write goes on with its remaining data | as Linux |
| `Rts`/`Dtr` getters | last value set | the line state | the line state |

The macOS code has not been run on a Mac. Linux is configured through the kernel's `TCGETS2`/`TCSETS2` ioctls,
whose numbers are those of x64, Arm, Arm64, RISC-V, LoongArch and s390x (not PowerPC or MIPS).

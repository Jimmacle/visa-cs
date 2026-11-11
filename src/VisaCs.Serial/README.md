# VisaCs.Serial

Serial ports through the Win32 communications API (Windows) or termios (Linux, macOS), without
`System.IO.Ports`:

- a timeout on every read and write, and `Abort` to end a transfer blocked on another thread;
- asynchronous reads and writes that hold no thread (I/O completion port on Windows, one shared poll thread on
  Linux and macOS), cancelled with a token;
- any baud rate the hardware accepts (`termios2`/`BOTHER` on Linux, `IOSSIOSPEED` on macOS), modem lines and break.

## Install

```
dotnet nuget add source https://nuget.pkg.github.com/Jimmacle/index.json --name visa-cs --username <github-user> --password <token with read:packages>
dotnet add package VisaCs.Serial --version 0.1.0
```

## Example

```csharp
using System.Text;
using VisaCs.Serial;   // NativeSerialPort, SerialSettings, SerialFlowControlModes, SerialPortException

foreach (string name in NativeSerialPort.GetPortNames())
    Console.WriteLine(name);

using var port = NativeSerialPort.Open(OperatingSystem.IsWindows() ? "COM3" : "/dev/ttyUSB0",
    SerialSettings.Default with { BaudRate = 115200, FlowControl = SerialFlowControlModes.RtsCts });

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await port.WriteAsync("*IDN?\n"u8.ToArray(), timeoutMilliseconds: 1000, cts.Token);
var buffer = new byte[256];
int n = await port.ReadAsync(buffer, timeoutMilliseconds: 2000, cts.Token);   // returns once at least one byte has arrived
Console.WriteLine(Encoding.ASCII.GetString(buffer, 0, n));
```

- `Open` applies the settings (`SerialSettings.Default`, 9600 8N1, when none are given); `Configure` changes them later.
- One read and one write may be in progress at the same time. A negative timeout waits without limit; 0 does only
  what is possible at once.
- `Rts`, `Dtr` (settable), `Cts`, `Dsr`, `Dcd`, `Ri`, `Break`, `BytesAvailable`, `DiscardInput`, `Drain(timeout)`.
- Device and OS errors are `SerialPortException`s (an `IOException`) whose `Error` says what went wrong
  (`NotFound`, `InUse`, `AccessDenied`, `Disconnected`, `Aborted`, `Closed`, `UnsupportedSetting`, `IOError`,
  `SystemError`) and whose `NativeError` is the errno or Win32 error when known. Timeouts are
  `SerialTimeoutException`s (a `TimeoutException`) with `BytesTransferred`, cancellation
  `OperationCanceledException`, settings out of range `ArgumentOutOfRangeException`.
- The package depends on no other package.

`Read` returns whatever has arrived. Termination characters, END handling and VISA attributes are what the
`SerialSession` of the `VisaCs` package adds. More in the
[serial guide](https://github.com/Jimmacle/visa-cs/blob/main/docs/serial.md#without-visa).

## License

MIT

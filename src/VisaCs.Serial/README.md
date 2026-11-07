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
using VisaCs;          // SerialParity, SerialFlowControlModes, VisaException
using VisaCs.Serial;   // NativeSerialPort, SerialSettings

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
- One read and one write may be in progress at the same time. A negative timeout waits without limit.
- `Rts`, `Dtr` (settable), `Cts`, `Dsr`, `Dcd`, `Ri`, `Break`, `BytesAvailable`, `DiscardInput`, `Drain(timeout)`.
- Errors are `VisaException`s (from `VisaCs.Primitives`), timeouts `VisaTimeoutException`, cancellation
  `OperationCanceledException`.

`Read` returns whatever has arrived. Termination characters, END handling and VISA attributes are what the
`SerialSession` of the `VisaCs` package adds. More in the
[serial guide](https://github.com/Jimmacle/visa-cs/blob/main/docs/serial.md#without-visa).

## License

MIT

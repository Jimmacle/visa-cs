# VisaCs

A VISA (Virtual Instrument Software Architecture) implementation written entirely in C#. It talks to test and
measurement instruments directly over VXI-11, HiSLIP (incl. 2.0 TLS and SASL), raw TCP sockets, VICP,
USBTMC/USB488, serial ports, GPIB and PXI (Linux). No NI-VISA, Keysight IO Libraries or other vendor VISA is
needed, and there are no third-party NuGet dependencies.

## Install

The packages are published to GitHub Packages:

```
dotnet nuget add source https://nuget.pkg.github.com/Jimmacle/index.json --name visa-cs --username <github-user> --password <token with read:packages>
dotnet add package VisaCs --version 0.1.0
```

`VisaCs` brings in `VisaCs.Primitives`, `VisaCs.Serial`, `VisaCs.Usb`, `VisaCs.Gpib`, `VisaCs.Formatting`, `VisaCs.Pci` and `VisaCs.Lan`. Add `VisaCs.Simulation`
to test projects to run against simulated instruments.

## Quick start

```csharp
using VisaCs;

await using var rm = new ResourceManager();
foreach (string name in await rm.FindAsync("?*::INSTR"))
    Console.WriteLine(name);

await using var dmm = await rm.OpenAsync("TCPIP::192.168.1.20::hislip0::INSTR");
dmm.TimeoutMilliseconds = 5000;
Console.WriteLine(await dmm.QueryAsync("*IDN?"));

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
double volts = await dmm.QueryAsync<double>("MEAS:VOLT:DC?", cts.Token);
float[] trace = await dmm.QueryBinaryValuesAsync<float>("CURV?", cts.Token);
```

Every operation has a synchronous form (`Query`, `Open`, `Find`, ...) and an asynchronous one that holds no
thread while it waits. The session timeout throws `VisaTimeoutException`; cancelling the token throws
`OperationCanceledException`.

## Platform notes

| Resource | Windows | Linux | macOS |
|---|:-:|:-:|:-:|
| `TCPIP INSTR` (VXI-11, HiSLIP), `TCPIP SOCKET`, `VICP INSTR` | ✔ | ✔ | ✔ |
| `USB INSTR`, `USB RAW` | ✔ (device bound to WinUSB) | ✔ usbfs or libusb | ✔ libusb |
| `ASRL INSTR` | ✔ | ✔ | ✔ |
| `GPIB INSTR`, `GPIB INTFC` | ✔ | ✔ | ✔ |
| `PXI INSTR` | ✘ | ✔ sysfs | ✘ |

- GPIB needs the board vendor's NI-488.2 compatible library (NI, Keysight, linux-gpib), loaded at run time.
- macOS support is written but has never been run. Nothing has been run against a physical instrument yet; the
  [support matrix](https://github.com/Jimmacle/visa-cs/blob/main/docs/support-matrix.md) shows how each backend
  was verified.

## Documentation

- [Using sessions](https://github.com/Jimmacle/visa-cs/blob/main/docs/using-sessions.md): resource names, I/O,
  binary data, formatted I/O, events, locking, attributes, async and cancellation
- [TCP/IP](https://github.com/Jimmacle/visa-cs/blob/main/docs/tcpip.md),
  [USB](https://github.com/Jimmacle/visa-cs/blob/main/docs/usb.md),
  [Serial](https://github.com/Jimmacle/visa-cs/blob/main/docs/serial.md),
  [GPIB](https://github.com/Jimmacle/visa-cs/blob/main/docs/gpib.md),
  [PXI](https://github.com/Jimmacle/visa-cs/blob/main/docs/pxi.md)
- [Attributes](https://github.com/Jimmacle/visa-cs/blob/main/docs/attributes.md) per session type
- [Simulator and tests](https://github.com/Jimmacle/visa-cs/blob/main/docs/simulator-and-testing.md)

## License

MIT

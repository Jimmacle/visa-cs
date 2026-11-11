# VisaCs

A VISA (Virtual Instrument Software Architecture) implementation written entirely in C#. It talks to test
and measurement instruments directly over each protocol. You do not need NI-VISA, Keysight IO Libraries or
any other vendor VISA, and the libraries have no third-party NuGet dependencies.

The only native code it calls is operating-system APIs (sockets, TLS, Win32 comm, WinUSB/SetupAPI, Linux usbfs,
termios, sysfs) plus, when you use them, libusb on macOS and the GPIB board vendor's NI-488.2 library.

```csharp
using VisaCs;

using var rm = new ResourceManager();
foreach (var name in rm.Find("?*::INSTR"))
    Console.WriteLine(name);

using var dmm = rm.Open("TCPIP::192.168.1.20::hislip0::INSTR");
dmm.TimeoutMilliseconds = 5000;
Console.WriteLine(dmm.Query("*IDN?"));
double volts = dmm.Query<double>("MEAS:VOLT:DC?");

// Every operation also has an asynchronous form that holds no thread while it waits.
await using var scope = await rm.OpenAsync("TCPIP::192.168.1.21::hislip0::INSTR");
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
float[] trace = await scope.QueryBinaryValuesAsync<float>("CURV?", cts.Token);
```

## Support at a glance

| Resource | Windows | Linux | macOS¹ | Implementation |
|---|:-:|:-:|:-:|---|
| `TCPIP INSTR` (VXI-11) | ✔ | ✔ | ✔ | ONC RPC/XDR, portmapper, core/abort/interrupt channels |
| `TCPIP INSTR` (HiSLIP) | ✔ | ✔ | ✔ | HiSLIP 2.0 incl. TLS and SASL authentication; works with 1.0 servers |
| `TCPIP SOCKET` | ✔ | ✔ | ✔ | Raw TCP |
| `VICP INSTR` | ✔ | ✔ | ✔ | Teledyne LeCroy VICP |
| `USB INSTR`, `USB RAW` | ✔ WinUSB² | ✔ usbfs / libusb | ✔ libusb | USBTMC and USB488 |
| `ASRL INSTR` | ✔ | ✔ | ✔ | Win32 comm API, termios (no `System.IO.Ports`) |
| `GPIB INSTR`, `GPIB INTFC` | ✔ | ✔ | ✔ | Binds at run time to NI-488.2 compatible libraries (NI, Keysight, linux-gpib) |
| `PXI INSTR` | ✘ | ✔ | ✘ | Register access through sysfs |
| `VXI`, `GPIB-VXI`, `MEMACC` | ✘ | ✘ | ✘ | Names parse; not implemented |

¹ Written for macOS, never run there. ² Transfers through WinUSB have not been tested yet.

Every message-based session supports VISA attributes, events (queue and handler), exclusive and shared locks,
formatted I/O (`Printf`/`Scanf`/`Queryf`), IEEE 488.2 binary blocks, and asynchronous methods with cancellation
that use asynchronous I/O all the way down ([details](docs/using-sessions.md#async-and-cancellation)). PXI
sessions are register-based: attributes, locks and register access.

**Testing status.** Every protocol is tested end to end against the bundled simulators. The usbfs and libusb
backends are tested against the real Linux USB stack (a simulated device attached through USB/IP), the GPIB
binding against a stand-in NI-488.2 library, Win32 serial against a real UART, termios through a
pseudo-terminal, and the simulator servers against PyVISA-py. Nothing has been run against a physical
instrument yet. The [support matrix](docs/support-matrix.md) has the details for every backend and feature.

## Install

The packages are published to GitHub Packages. Add the feed once, then the package you need:

```
dotnet nuget add source https://nuget.pkg.github.com/Jimmacle/index.json --name visa-cs --username <github-user> --password <token with read:packages>
dotnet add package VisaCs --version 0.1.0
```

| You want | Package |
|---|---|
| VISA: resource manager, sessions, every interface | `VisaCs` (brings in the four component libraries) |
| Only a serial port, without VISA | `VisaCs.Serial` |
| Only USB host access (WinUSB, usbfs, libusb) or USBTMC instruments, without VISA | `VisaCs.Usb` |
| Only an NI-488.2 (GPIB) binding, without VISA | `VisaCs.Gpib` |
| Simulated instruments in test projects | `VisaCs.Simulation`, same version as `VisaCs` |

## Libraries

The hardware access layers are separate libraries, so they can be used without the VISA session model:

| Library | Contents | Depends on |
|---|---|---|
| `VisaCs` | VISA itself: `ResourceManager`, sessions, resource names, VXI-11, HiSLIP, raw socket, VICP, USBTMC, PXI, formatted I/O, discovery | the four below |
| `VisaCs.Serial` | `NativeSerialPort`: serial ports through the Win32 comm API or termios, without `System.IO.Ports` ([usage](docs/serial.md#without-visa)) | `VisaCs.Primitives` |
| `VisaCs.Usb` | `IUsbBackend` and its WinUSB, usbfs and libusb implementations: enumeration and control, bulk and interrupt transfers; `UsbTmcDevice`, a USBTMC/USB488 client ([usage](docs/usb.md#without-visa)) | `VisaCs.Primitives` |
| `VisaCs.Gpib` | `Ni488Driver`: GPIB through any NI-488.2 compatible library, bound at run time ([usage](docs/gpib.md#without-visa)) | `VisaCs.Primitives` |
| `VisaCs.Primitives` | `VisaStatus` and the `VisaException` types the other libraries report errors with | |
| `VisaCs.Simulation` | Simulated instruments and protocol servers for testing without hardware | `VisaCs` |

Referencing `VisaCs` brings in the others.

## Documentation

| Guide | Contents |
|---|---|
| [Support matrix](docs/support-matrix.md) | Resources per OS, verification per backend, operations per session type, protocol coverage, what is not implemented |
| [Using sessions](docs/using-sessions.md) | Resource names, finding and opening, reading and writing, binary data, formatted I/O, device operations, errors, async and cancellation, events, locking, attributes |
| [TCP/IP](docs/tcpip.md) | VXI-11, HiSLIP (incl. HiSLIP 2.0 security setup), raw sockets, VICP, LAN discovery |
| [USB](docs/usb.md) | Backends and driver setup per OS, USBTMC/USB488 features, RAW sessions, writing a backend |
| [Serial](docs/serial.md) | Port names per OS, settings, modem lines, END handling |
| [GPIB](docs/gpib.md) | Driver libraries, device and interface sessions, discovery |
| [PXI](docs/pxi.md) | Linux setup, resource names, register access, windows |
| [Attributes](docs/attributes.md) | Every supported attribute per session type (generated from the code) |
| [Simulator and tests](docs/simulator-and-testing.md) | Simulated instruments, the VisaShell tool, test suites and their environment variables |

Every public type and member also has XML documentation, so IntelliSense shows it.

## Building

```
dotnet build VisaCs.slnx
dotnet test tests/VisaCs.Tests
```

The libraries target .NET 10. The default test run needs no hardware; optional suites (real serial port, Linux
kernel USB, GPIB binding, real PCI) are described in [Simulator and tests](docs/simulator-and-testing.md).

## Main limitations

- Not yet run against physical instruments, WinUSB transfers, or macOS.
- VisaCs as a client has only been checked against its own VXI-11 and HiSLIP simulators, not against an
  independent server implementation.
- VXI, GPIB-VXI, PXI triggers and interrupts, and VISA's `viReadAsync`-style I/O completion events are not
  implemented (use the `*Async` methods instead).
- No persistent configuration store: aliases and static resources are set in `ResourceManagerOptions`.

## License

MIT, see [LICENSE](LICENSE).

## Layout

```
src/VisaCs/                 the VISA library
  Protocols/                ONC RPC + VXI-11, HiSLIP (incl. TLS and SASL)
  Sessions/                 VisaSession, MessageBasedSession, locking, events, formatted I/O
  Tcpip/                    VXI-11, HiSLIP (incl. security options), raw socket and VICP sessions
  Usb/                      USBTMC/USB488 and USB RAW sessions
  Serial/                   SerialSession
  Gpib/                     GPIB sessions
  Pxi/                      PCI sysfs access and PxiSession
  Discovery/                find expressions, VXI-11 broadcast, mDNS
  Formatting/               viPrintf/viScanf format language
src/VisaCs.Serial/          native serial ports (Win32, termios)
src/VisaCs.Usb/             USB backends: WinUSB, usbfs, libusb; USBTMC client
src/VisaCs.Gpib/            NI-488.2 binding
src/VisaCs.Primitives/      VisaStatus and the exception types
src/Shared/                 internal helpers compiled into several libraries (deadlines, libc, library loading)
src/VisaCs.Simulation/      simulated instrument, protocol servers, USB/IP exporter
samples/VisaShell/          command-line tool
tests/VisaCs.Tests/         xUnit tests
tests/Native/               stand-in NI-488.2 library for the GPIB binding tests
tests/Interop/              PyVISA-py interoperability script
docs/                       guides and support matrix
```

# Simulator, command line tool and tests

## Simulated instruments

`VisaCs.Simulation` lets you develop and test without hardware. It contains a scriptable SCPI instrument
and a server for every protocol VisaCs speaks.

```csharp
using VisaCs;
using VisaCs.Simulation;

using var sim = new InstrumentSimulator().Start();         // VXI-11, HiSLIP, raw socket and VICP on free ports
using var rm = new ResourceManager(sim.CreateOptions());   // options pointing at the simulator's ports
using var s = rm.Open(sim.HislipResource);
Console.WriteLine(s.Query("*IDN?"));
```

| Class | What it simulates |
|---|---|
| `SimulatedInstrument` | The instrument: `*IDN?`, `*RST`, `*CLS`, `*ESE[?]`, `*ESR?`, `*SRE[?]`, `*STB?`, `*OPC[?]`, `*TRG`, `*WAI`, `*TST?`, `ECHO? text`, `MEAS:VOLT?`, `SYST:ERR?`, `TRIG:COUNT?`, `SRQ`, `DATA:BIN? n`, `DATA:BIN:INDEF? n`, `DATA:FLOAT? n`, `DATA:ASCII? n`, `DATA:LARGE? n`, `DATA:STORE <block>`, `DATA:STORE?`, `DELAY ms`, `DELAY? ms`. Add commands with `CustomHandler`. |
| `Vxi11InstrumentServer` | Portmapper, core, abort and interrupt channels |
| `HislipInstrumentServer` | HiSLIP 1.0 and 2.0, including TLS (`Certificate`, `EncryptionMandatory`, `InitialEncryptionRequired`) and SASL (`RequireAuthentication`, `SaslMechanisms`, `Users`, `TrustedClientCertificates`); `CreateSelfSignedCertificate` makes a test certificate |
| `SocketInstrumentServer`, `VicpInstrumentServer` | Raw socket and VICP |
| `SimulatedUsbBackend`, `SimulatedUsbTmcDevice` | A USBTMC/USB488 device behind an in-process USB backend, with abort/clear sequences, interrupt-IN SRQ and vendor-specific message loopback |
| `UsbIpServer` | Exports a `SimulatedUsbTmcDevice` over USB/IP, so the real Linux USB stack can be tested |
| `SimulatedGpibDriver` | An `IGpibDriver` with instruments at chosen addresses |
| `MdnsTestResponder` | Answers mDNS queries for discovery tests |

Each server takes an optional bind address and port (0 picks a free port) and exposes counters
(`ConnectionCount`, `SessionCount`, `TriggerCount`, `ClearCount`, ...) for assertions.

## VisaShell

`samples/VisaShell` is a small command-line tool built on the library:

```
dotnet run --project samples/VisaShell -- list                        # find everything
dotnet run --project samples/VisaShell -- query TCPIP::192.168.1.20::INSTR "*IDN?"
dotnet run --project samples/VisaShell -- info "USB::0x0957::0x1796::MY123::INSTR"   # dump all attributes
dotnet run --project samples/VisaShell -- shell ASRL3::INSTR          # interactive session
dotnet run --project samples/VisaShell -- simulate                    # run the simulated instrument servers
```

Other commands: `find <expression>`, `parse <name>`, `write`, `read`. Options: `--timeout <ms>`,
`--portmapper-port <n>`, `--vicp-port <n>`, `--no-lan`, `--probe <ip>`, and for `simulate`: `--bind <ip>`,
`--portmapper <n>`, `--hislip <n>`, `--socket <n>`, `--vicp <n>`. Inside `shell`, lines are sent as commands
(queries are read back) and `:read`, `:stb`, `:clear`, `:trigger`, `:timeout`, `:termchar`, `:block`, `:attr`,
`:lock`, `:unlock`, `:srq`, `:local`, `:remote` and `:quit` control the session.

## Running the tests

```
dotnet test tests/VisaCs.Tests
```

The default run needs no hardware and no privileges. It runs every protocol end to end against the
simulators (VXI-11, HiSLIP incl. 2.0 security, socket, VICP, simulated USB, simulated GPIB), parsing, find
expressions, formatted I/O and the PXI register logic against a fake sysfs tree (Linux). On Linux it also
tests the termios serial code through a pseudo-terminal.

Optional suites are switched on with environment variables:

| Variable | Enables | Needs |
|---|---|---|
| `VISACS_TEST_SERIAL=ASRL1::INSTR` | Smoke test of `SerialSession` on a real serial port, synchronous and asynchronous: configuration, writes, timeouts, cancellation, modem lines, break | A serial port (nothing needs to be attached) |
| `VISACS_TEST_SERIAL_PORT=COM1` | `NativeSerialPort` on a real port (Windows): asynchronous and overlapping synchronous reads and writes, timeouts, cancellation, `Abort`, `Dispose` with transfers pending, `Drain`/`DrainAsync` with timeouts | A serial port (nothing needs to be attached) |
| `VISACS_TEST_USBIP=1` | The full protocol suite (synchronous and asynchronous) through the usbfs and libusb backends, plus the backends' own transfers (asynchronous, and synchronous ones on the same machinery), claiming (a second open is refused) and dispose, against the simulated USBTMC device attached to the Linux kernel through USB/IP | Linux, root, `modprobe vhci-hcd`, libusb-1.0 for the libusb part |
| `VISACS_TEST_GPIB_LIBRARY=<path>` | The real NI-488.2 binding (`Ni488Driver`), synchronous and asynchronous, against `tests/Native/fake_ni4882.c` | Build it with `tests/Native/build-fake-gpib.sh` or `.cmd`; run once with each of the three builds (NI-like, without `ibnotify`, without asynchronous calls). The library's test hooks also hide SRQI as NI-488.2 does while autopolling, lose completion notifications, and let a callback's return value undo a concurrent `ibnotify` |
| `VISACS_TEST_PCI=1` | Checks that a real PCI function owned by a kernel driver is refused, and reads the configuration space of the first function available to user space, if any | Linux, root |
| `VISACS_GENERATE_DOCS=<file>` | Regenerates [attributes.md](attributes.md) from live sessions | Linux for the serial and PXI columns |

Example for the kernel USB suite:

```
sudo modprobe vhci-hcd
sudo VISACS_TEST_USBIP=1 dotnet test tests/VisaCs.Tests --filter "FullyQualifiedName~KernelTests|FullyQualifiedName~UsbBackendAsync"
sudo rmmod vhci-hcd      # afterwards, if you like
```

## Interoperability check with PyVISA

`tests/Interop/pyvisa_client_interop.py` drives the VisaCs simulator servers with PyVISA-py, an independent
VISA implementation, to check that the servers (and so the simulator the tests rely on) follow the
protocols the same way another implementation does:

```
pip install pyvisa pyvisa-py pyvicp
dotnet run --project samples/VisaShell -- simulate --bind 127.0.0.1 --portmapper 111 --hislip 4880 --socket 5025 --vicp 1861
python tests/Interop/pyvisa_client_interop.py TCPIP::127.0.0.1::INSTR TCPIP::127.0.0.1::hislip0::INSTR TCPIP::127.0.0.1::5025::SOCKET VICP::127.0.0.1::INSTR
```

Port 111 needs root on Linux. Checks that PyVISA-py itself does not implement are reported as `SKIP`.

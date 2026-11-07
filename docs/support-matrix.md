# Support matrix

This page lists what VisaCs supports, on which operating systems, and how each part has been verified.
"Verified" means exercised by the automated tests in `tests/` or by the runs listed under
[How each backend was verified](#how-each-backend-was-verified). Nothing here has been run against a physical instrument
yet, so read the verification column before you depend on a backend.

Legend: **✔** supported · **◐** partial (see notes) · **✘** not supported · **—** not applicable

## Resource types by operating system

| Resource | Windows | Linux | macOS | Notes |
|---|:-:|:-:|:-:|---|
| `TCPIP INSTR` (VXI-11) | ✔ | ✔ | ✔¹ | Portmapper, core, abort and interrupt channels |
| `TCPIP INSTR` (HiSLIP 1.0 / 2.0) | ✔ | ✔ | ✔¹ | Includes HiSLIP 2.0 TLS and SASL authentication |
| `TCPIP SOCKET` | ✔ | ✔ | ✔¹ | Raw TCP (SCPI-RAW) |
| `VICP INSTR` | ✔ | ✔ | ✔¹ | Teledyne LeCroy VICP |
| `USB INSTR` (USBTMC / USB488) | ✔ WinUSB | ✔ usbfs or libusb | ✔¹ libusb | Windows: device must be bound to WinUSB |
| `USB RAW` | ✔ WinUSB | ✔ usbfs or libusb | ✔¹ libusb | Bulk, interrupt and control transfers |
| `ASRL INSTR` | ✔ Win32 comm API | ✔ termios | ✔¹ termios | No `System.IO.Ports` dependency |
| `GPIB INSTR`, `GPIB INTFC` | ✔ NI-488.2 / Keysight | ✔ linux-gpib or NI | ✔¹ NI-488.2 | Needs the vendor's GPIB driver installed |
| `PXI INSTR` | ✘ | ✔ sysfs | ✘ | Functions with no kernel driver, or bound to `vfio-pci`, `uio_pci_generic` or `pci-stub` |
| `VXI`, `GPIB-VXI` | ✘ | ✘ | ✘ | Names parse; opening returns `VI_ERROR_RSRC_NFOUND` |
| `PXI MEMACC` | ✘ | ✘ | ✘ | Name parses; opening returns `VI_ERROR_RSRC_NFOUND` |
| `FIREWIRE`, `RIO` | ✘ | ✘ | ✘ | Not recognized (`VI_ERROR_INV_RSRC_NAME`) |

¹ Written for macOS but never run there. The TCP/IP protocols are pure managed code shared with the other
platforms, so they are likely to work. The macOS-specific parts (libusb loading, `cu.*` port enumeration,
`IOSSIOSPEED`, the NI-488.2 framework path) are untested.

The libraries target .NET 10, have no third-party NuGet dependencies and need no vendor VISA. They have been run on x64
Windows 11 and x64 Arch Linux under WSL2 (kernel 6.18). The code avoids architecture-specific assumptions
(the usbfs ioctl numbers use the generic Linux encoding), but other architectures have not been tested.

## How each backend was verified

| Area | Backend | Verified against | Level |
|---|---|---|---|
| VXI-11 client | managed | VisaCs simulator (Windows, Linux) | Simulator |
| VXI-11 server (simulator) | managed | PyVISA-py 0.8.1 client | Independent implementation |
| HiSLIP client (2.0, and 1.0 servers) | managed | VisaCs simulator | Simulator |
| HiSLIP 2.0 security (TLS, SASL) | managed | VisaCs simulator (18 tests, SChannel on Windows and OpenSSL on Linux) | Simulator |
| HiSLIP server (simulator) | managed | PyVISA-py 0.8.1 client | Independent implementation |
| Raw socket | managed | VisaCs simulator, PyVISA-py | Simulator + independent |
| VICP | managed | VisaCs simulator, PyVISA-py | Simulator + independent |
| USBTMC / USB488 protocol | managed | Simulated USB backend (in-process) | Simulator |
| USB on Linux | usbfs | Real Linux USB stack, simulated device attached through USB/IP (`vhci-hcd`) | Kernel |
| USB on Linux | libusb-1.0 | Same USB/IP setup | Kernel |
| USB on Windows | WinUSB | Enumeration runs against the machine's device tree; the descriptor reads during enumeration and all transfers have not run (no WinUSB-bound device available) | Partly untested |
| USB on macOS | libusb-1.0 | — | Untested |
| Serial on Windows | Win32 comm API | Real UART (COM1, nothing attached): configuration, overlapped writes, timeouts, cancellation, modem lines, break | Hardware (no peer) |
| Serial on Linux | termios2 (TCGETS2/TCSETS2) | Pseudo-terminal with a simulated instrument, incl. kernel speed checks (standard, 250000 and 1000000 baud), NUL discarding and hang-up | OS driver |
| Serial on macOS | termios / IOSSIOSPEED | — | Untested |
| GPIB on Windows | `ni4882.dll` API | Stand-in `ni4882.dll` built with MSVC that implements the NI-488.2 C API | Fake driver |
| GPIB on Linux | `libgpib.so` API | Same stand-in built with gcc | Fake driver |
| GPIB protocol logic | `IGpibDriver` | Simulated GPIB driver | Simulator |
| PXI on Linux | sysfs | Fake sysfs tree (BAR files, a read-only config file, a function bound to a kernel driver) plus, as root, the real configuration space of a PCI function available to user space when the machine has one | Partly OS |

Levels, strongest first:

- **Independent implementation**: checked against code written by someone else (PyVISA-py). This is the only
  check that catches a misreading of a specification shared by the client and the simulator.
- **Kernel / OS driver / Hardware**: the real operating-system code path runs (Linux USB core and usbfs, a
  Linux pty, a Windows serial port driver).
- **Fake driver**: the real P/Invoke binding runs against a stand-in native library with the same C API.
- **Simulator**: VisaCs talks to its own simulator. Both sides follow the same reading of the specification.
- **Untested**: compiled, never executed.

Not done yet: VisaCs as a client against an independent VXI-11 or HiSLIP server (a real instrument or another
project's server), any physical USB, GPIB or PXI instrument, and anything on macOS.

## Operations by session type

| Operation (VISA function) | VXI-11 | HiSLIP | SOCKET | VICP | USB INSTR | USB RAW | ASRL | GPIB INSTR | GPIB INTFC | PXI |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| Read / Write (`viRead`, `viWrite`) | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | — |
| END / EOI detection | ✔ | ✔ | ✘² | ✔ | ✔ | ◐¹² | ◐³ | ✔ | ✔ | — |
| Termination character | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | — |
| Formatted I/O (`viPrintf`, `viScanf`, `viQueryf`) | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | — |
| Read status byte (`viReadSTB`) | ✔ | ✔ | ◐⁴ | ✔ | ✔⁵ | ✘ | ◐⁴ | ✔ | ✘ | — |
| Device clear (`viClear`) | ✔ | ✔ | ◐⁶ | ✔ | ✔ | ✔ | ◐⁶ | ✔ | ✔ | — |
| Trigger (`viAssertTrigger`) | ✔ | ✔ | ◐⁷ | ◐⁷ | ✔⁵ | ✘ | ◐⁷ | ✔ | ✔ | — |
| Remote/local (`viGpibControlREN`) | ✔ | ✔ | ✘ | ✔ | ✔⁵ | ✘ | ✘ | ✔ | ✔ | — |
| Service request event | ✔ | ✔ | ✘ | ◐⁸ | ✔⁵ | — | ✘ | ✔ | ✔ | ✘ |
| USB interrupt event | — | — | — | — | ✔ | ✔ | — | — | — | — |
| Exclusive / shared lock (`viLock`) | ✔⁹ | ✔⁹ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| Cancellation of `*Async` calls | ✔ `device_abort` | ✔¹⁰ | ✔¹⁰ | ✔¹⁰ | ✔ abort sequence | ✔¹⁰ | ✔ I/O cancel | ◐¹¹ | ◐¹¹ | — |
| Asynchronous I/O, no thread while waiting (`QueryAsync`, ...) | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔¹¹ | ✔¹¹ | — |
| Register access (`viIn*`, `viOut*`, `viMoveIn*`, `viMoveOut*`, `viMapAddress`, `viPeek*`, `viPoke*`) | — | — | — | — | — | — | — | — | — | ✔ |
| GPIB bus commands (`viGpibCommand`, `viGpibSendIFC`, `viGpibControlATN`, `viGpibPassControl`) | — | — | — | — | — | — | — | — | ✔ | — |
| USB control transfers (`viUsbControlIn`, `viUsbControlOut`) | — | — | — | — | ✔ | ✔ | — | — | — | — |
| Serial break / modem lines | — | — | — | — | — | — | ✔ | — | — | — |

Notes:

2. Raw sockets have no END indicator. Reads stop at the termination character (enabled by default for
   `SOCKET`), at the requested count, or on timeout.
3. Serial END is configurable with `EndInput` (`VI_ATTR_ASRL_END_IN`): termination character (default), last
   bit, or none.
4. Sent as `*STB?` and the reply is parsed. NI-VISA does this only when `VI_ATTR_IO_PROT` is
   `VI_PROT_4882_STRS`; VisaCs always does it, because these interfaces have no other way to read it.
5. USB488 interfaces only (interface protocol 1) and only if the device reports the capability. Plain USBTMC
   devices return `VI_ERROR_NSUP_OPER`.
6. Discards buffered data. Also sends `*CLS` when `IOProtocol` is `Ieee4882Strings`.
7. Sent as `*TRG`.
8. VICP carries SRQ in-band, so it is only noticed while the session is reading.
9. Also takes the instrument's own lock (VXI-11 `device_lock`, HiSLIP `AsyncLock`), so other hosts see it.
   On other interfaces locks are process-wide (shared by every `ResourceManager` in the process); they do
   not reach other processes.
10. The read or write ends early as if it had timed out, then the task is cancelled. Recover as after a
    timeout: `Clear()` (and, for sockets, let the late reply arrive before clearing).
11. Transfers use NI-488.2's asynchronous calls and are stopped with `ibstop`. Serial poll, device clear and
    trigger have no asynchronous form; they run on one worker thread per board and, once started, end at their
    timeout. Driver libraries without `ibrda` run every call that way.
12. A short bulk packet ends the read.

Every session supports attributes through `GetAttribute`/`SetAttribute` and typed properties. The full list
per session type, generated from live sessions, is in [attributes.md](attributes.md).

## Protocol feature coverage

### VXI-11 (VXIbus Consortium VXI-11 rev 1.0)

| Feature | Status |
|---|---|
| Portmapper `GETPORT` (TCP), configurable port | ✔ |
| `create_link`, `destroy_link`, `device_write`, `device_read` with END and termination character | ✔ |
| `device_readstb`, `device_trigger`, `device_clear`, `device_remote`, `device_local` | ✔ |
| `device_lock` / `device_unlock` with wait | ✔ |
| `device_abort` on the abort channel (used when an `*Async` call is cancelled) | ✔ |
| Interrupt channel (`create_intr_chan`, `device_enable_srq`, `device_intr_srq`) | ✔ (needs inbound TCP to the client) |
| `device_docmd` | ✔ through `TcpipVxi11Session.DoCommand` |
| GPIB gateways (`gpib0,5`) | ✔ |
| Broadcast discovery (portmapper `GETPORT` over UDP) | ✔ |
| UDP transport for the core channel | ✘ (not used by instruments) |

### HiSLIP (IVI-6.1 version 2.0, IVI-6.5)

| Feature | Status |
|---|---|
| Initialize / InitializeResponse, version negotiation (client offers 2.0, works with 1.0 servers) | ✔ |
| Synchronized and overlapped modes (`OverlapEnabled`) | ✔ |
| Data / DataEnd, message IDs, RMT-delivered | ✔ |
| `AsyncMaximumMessageSize` (`MaximumMessageKB`) | ✔ |
| Device clear (`AsyncDeviceClear`, `DeviceClearComplete`), feature negotiation | ✔ |
| Trigger, status query, remote/local control | ✔ |
| Exclusive and shared locks, `AsyncLockInfo` (`GetLockInfo`) | ✔ |
| Service request (`AsyncServiceRequest`) | ✔ |
| Interrupted / Error / FatalError handling | ✔ |
| 2.0 secure connection: `AsyncStartTLS` / `StartTLS`, `AsyncEndTLS` / `EndTLS` | ✔ (TLS 1.2 and 1.3) |
| 2.0 encryption policies: mandatory encryption, initial encryption | ✔ |
| 2.0 SASL: SCRAM-SHA-256-PLUS (`tls-server-end-point`), SCRAM-SHA-256, PLAIN, EXTERNAL, ANONYMOUS | ✔ |
| 2.0 `GetDescriptors` | ✔ |
| Server certificate checks: system trust, extra trust anchors or pinning, name check, custom callback | ✔ |
| Credential storage (IVI-6.5 credential store) | ◐ credentials come from `HislipSecurityOptions`; there is no persistent store |

### USBTMC 1.0 / USB488 1.0

| Feature | Status |
|---|---|
| `DEV_DEP_MSG_OUT`, `REQUEST_DEV_DEP_MSG_IN` with TermChar, multi-transfer messages | ✔ |
| `INITIATE_ABORT_BULK_OUT/IN` and `CHECK_ABORT_BULK_*_STATUS` (on timeout and cancellation) | ✔ |
| `INITIATE_CLEAR` / `CHECK_CLEAR_STATUS` | ✔ |
| `GET_CAPABILITIES` (`Capabilities`), `INDICATOR_PULSE` (`IndicatorPulse`) | ✔ |
| USB488 `READ_STATUS_BYTE` through the interrupt endpoint or the control response | ✔ |
| USB488 `TRIGGER`, `REN_CONTROL`, `GO_TO_LOCAL`, `LOCAL_LOCKOUT` | ✔ |
| SRQ through the interrupt-IN endpoint | ✔ |
| Vendor-specific interrupt packets (`VI_EVENT_USB_INTR`) | ✔ |
| Several sessions in one process sharing one device | ✔ |
| Kernel driver detach and reattach (Linux usbtmc) | ✔ |
| Vendor-specific messages (`IOProtocol.UsbTmcVendor`: `VENDOR_SPECIFIC_OUT`, `REQUEST_VENDOR_SPECIFIC_IN`) | ✔ (a short transfer ends a read, since these messages carry no EOM flag) |

## VISA features not implemented

| Feature | Alternative |
|---|---|
| `viReadAsync`, `viWriteAsync`, `viMoveAsync` and `VI_EVENT_IO_COMPLETION` | Task-based `*Async` methods with `CancellationToken` |
| Event handlers installed per event type (`viInstallHandler`) | .NET events `ServiceRequest` and `VisaEvent`, plus the queue mechanism |
| `viMapTrigger`, `viUnmapTrigger`, PXI trigger reservation | — |
| `viMemAlloc`, `viMemFree`, VXI servants, `viVxiCommandQuery`, `viAssertIntrSignal`, `viAssertUtilSignal` | — |
| PXI interrupts (`VI_EVENT_PXI_INTR`) | — |
| GPIB interface events (CIC, talk, listen) | Poll `GpibInterfaceSession` properties |
| Persistent configuration store (NI MAX / Keysight Connection Expert aliases and static resources) | `ResourceManagerOptions.Aliases` and `StaticResources` |
| `viSetBuf` buffer sizes | Read and write buffering is internal; `Flush` and `ReadChunkSize` are available |
| GPIB HS488 (`IOProtocol.HS488`), VXI fast data channel | Setting them throws `VI_ERROR_NSUP_ATTR_STATE` |

# VisaCs.Simulation

Simulated instruments and protocol servers for testing code built on VisaCs without hardware: a scriptable
SCPI instrument served over VXI-11, HiSLIP (incl. 2.0 TLS and SASL), raw socket and VICP, a simulated
USBTMC/USB488 backend, a simulated GPIB driver, and a USB/IP exporter for testing the real Linux USB stack.
Meant for test projects; the servers use a thread per connection.

## Install

Use **exactly the same version as `VisaCs`**: this package depends on VisaCs internals and is pinned to one
VisaCs version.

```
dotnet nuget add source https://nuget.pkg.github.com/Jimmacle/index.json --name visa-cs --username <github-user> --password <token with read:packages>
dotnet add package VisaCs --version 0.1.0
dotnet add package VisaCs.Simulation --version 0.1.0
```

## LAN instruments

```csharp
using VisaCs;
using VisaCs.Simulation;

var instrument = new SimulatedInstrument { Identity = "ACME,DMM-1,0001,1.0" };
instrument.CustomHandler = command => command.Equals("CONF?", StringComparison.OrdinalIgnoreCase) ? "VOLT:DC" : null;

using var sim = new InstrumentSimulator(instrument).Start();        // all four servers on free ports
await using var rm = new ResourceManager(sim.CreateOptions());      // points VisaCs at those ports
await using var dmm = await rm.OpenAsync(sim.HislipResource);       // also Vxi11Resource, SocketResource, VicpResource
Console.WriteLine(await dmm.QueryAsync("*IDN?"));
Console.WriteLine(await dmm.QueryAsync("CONF?"));
Console.WriteLine(string.Join(", ", instrument.CommandLog));
```

In xUnit, start one `InstrumentSimulator` in a class fixture and create a `ResourceManager` per test.
`SimulatedInstrument` answers common IEEE 488.2 commands, `MEAS:VOLT?` (`Voltage`), data blocks
(`DATA:BIN? n`, `DATA:FLOAT? n`, ...), `DELAY ms` for timeout tests and `SRQ`; `CustomHandler` adds your own
commands. `CommandLog`, `TriggerCount`, `ClearCount` and `IsRemote` support assertions, and
`RequestService()` raises a service request.

The VXI-11 and VICP resource names carry no port, so two simulators in one process share a resource name and
therefore a lock; bind them to different loopback addresses if tests lock them in parallel.

## USB and GPIB

```csharp
using VisaCs;
using VisaCs.Simulation;

var usb = new SimulatedUsbBackend();
usb.Devices.Add(new SimulatedUsbTmcDevice(new SimulatedInstrument(), vendorId: 0x1234, productId: 0x5678, serialNumber: "SIM1"));
var gpib = new SimulatedGpibDriver();
gpib.Instruments[22] = new SimulatedInstrument();

await using var rm = new ResourceManager(new ResourceManagerOptions
{
    UsbBackend = usb,
    GpibDriver = gpib,
    EnableLanDiscovery = false,
    EnableMdnsDiscovery = false,
    EnableSerial = false,
    EnablePxi = false,
});
await using var usbDmm = await rm.OpenAsync("USB::0x1234::0x5678::SIM1::INSTR");
await using var gpibDmm = await rm.OpenAsync("GPIB0::22::INSTR");
Console.WriteLine(await usbDmm.QueryAsync("*IDN?"));
Console.WriteLine(await gpibDmm.QueryAsync("*IDN?"));
```

`SimulatedUsbTmcDevice` implements the USBTMC abort and clear sequences, interrupt-IN service requests and
vendor-specific messages (`ClearCount`, `AbortCount`, `ResponseDelayMilliseconds`). `SimulatedGpibDriver`
records bus commands in `SentCommands`.

## More

- `HislipInstrumentServer` options for security tests: `Certificate` (`CreateSelfSignedCertificate()`),
  `EncryptionMandatory`, `RequireAuthentication`, `Users`, `SaslMechanisms`.
- `UsbIpServer` exports a `SimulatedUsbTmcDevice` over USB/IP so a virtual host controller (Linux `vhci-hcd`)
  sees it as a real device.
- `MdnsTestResponder` answers DNS-SD queries for discovery tests (point `ResourceManagerOptions.MdnsEndPoint` at it).

See [Simulator and tests](https://github.com/Jimmacle/visa-cs/blob/main/docs/simulator-and-testing.md).

## License

MIT

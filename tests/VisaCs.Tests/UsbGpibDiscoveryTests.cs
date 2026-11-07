using System.Net;
using VisaCs.Discovery;
using VisaCs.Simulation;
using VisaCs.Usb;

namespace VisaCs.Tests;

public sealed class UsbFixture : IDisposable
{
    public UsbFixture()
    {
        Backend.Devices.Add(new SimulatedUsbTmcDevice(Instrument, 0x1234, 0x5678, "SIM0001"));
    }

    public SimulatedInstrument Instrument { get; } = new();

    public SimulatedUsbBackend Backend { get; } = new();

    public ResourceManager CreateResourceManager(SimulatedUsbBackend? backend = null) => new(new ResourceManagerOptions
    {
        UsbBackend = backend ?? Backend,
        EnableLanDiscovery = false,
        EnableMdnsDiscovery = false,
        EnableSerial = false,
        EnableGpib = false,
    });

    public void Dispose()
    {
    }
}

[CollectionDefinition("usb", DisableParallelization = true)]
public sealed class UsbCollection : ICollectionFixture<UsbFixture>;

/// <summary>USBTMC/USB488 against a simulated device. The simulated device allows one open handle at a time.</summary>
[Collection("usb")]
public sealed class UsbTmcTests(UsbFixture fixture) : MessageBasedProtocolTests
{
    protected override ResourceManager CreateResourceManager() => fixture.CreateResourceManager();
    protected override string Resource => "USB::0x1234::0x5678::SIM0001::INSTR";
    protected override SimulatedInstrument Instrument => fixture.Instrument;

    [Fact]
    public void SendsVendorSpecificMessages()
    {
        using var rm = CreateResourceManager();
        using var session = rm.Open<UsbSession>(Resource);
        session.TimeoutMilliseconds = 2000;
        var device = fixture.Backend.Devices[0];
        int before = device.VendorMessageCount;

        session.SetAttribute(VisaAttribute.IOProtocol, IOProtocol.UsbTmcVendor);
        byte[] payload = [0x01, 0x02, 0x03, 0xFA, 0x0A];
        session.Write(payload);
        Assert.Equal(before + 1, device.VendorMessageCount);
        Assert.Equal(payload, session.ReadToEnd()); // the simulated device loops vendor data back

        session.IOProtocol = IOProtocol.Normal;
        Assert.Equal(Instrument.Identity, session.Query("*IDN?"));
    }

    // Locking tests need several sessions; the simulated device (like WinUSB) allows only one handle.
    [Fact]
    public void ReportsCapabilities()
    {
        using var rm = CreateResourceManager();
        using var session = rm.Open<UsbSession>(Resource);
        Assert.Equal(UsbProtocol.Usb488, session.UsbProtocol);
        Assert.True(session.Capabilities.AcceptsTrigger);
        Assert.True(session.Capabilities.SupportsTermChar);
        Assert.True(session.Is4882Compliant);
        Assert.Equal(0x1234, session.ManufacturerId);
        Assert.Equal("Simulated USBTMC Instrument", session.ModelName);
        Assert.Equal("USB0::0x1234::0x5678::SIM0001::0::INSTR", session.ResourceName);
        Assert.Equal(0x82, session.GetAttribute<int>(VisaAttribute.UsbBulkInPipe));
        session.IndicatorPulse();
        var caps = session.ControlIn(0xA1, 7, 0, 0, 0x18);
        Assert.Equal(1, caps[0]);
    }

    [Fact]
    public void ControlsRemoteLocal()
    {
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        var device = fixture.Backend.Devices[0];
        session.ControlRen(RemoteLocalMode.AssertLocalLockout);
        Assert.True(device.IsRemote);
        Assert.True(device.LocalLockout);
        session.ControlRen(RemoteLocalMode.DeassertGoToLocal);
        Assert.False(device.IsRemote);
    }

    [Fact]
    public void ServiceRequestCarriesStatusByte()
    {
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        session.EnableEvent(EventType.ServiceRequest, EventMechanism.Queue);
        try
        {
            Instrument.RequestService();
            var e = Assert.IsType<ServiceRequestEventArgs>(session.WaitOnEvent(EventType.ServiceRequest, 5000));
            Assert.Equal(0x40, e.StatusByte!.Value & 0x40);
            // The status byte still arrives through the interrupt endpoint while the reader thread runs.
            session.ReadStatusByte();
        }
        finally
        {
            session.DisableEvent(EventType.ServiceRequest);
            session.WriteLine("*CLS");
            session.Query("*OPC?");
        }
    }

    [Fact]
    public void FindsDevicesWithAttributeFilters()
    {
        using var rm = CreateResourceManager();
        Assert.Equal(["USB0::0x1234::0x5678::SIM0001::0::INSTR"], rm.Find("USB?*INSTR"));
        Assert.Single(rm.Find("?*{VI_ATTR_MANF_ID==0x1234 && VI_ATTR_USB_PROTOCOL==1}"));
        Assert.Empty(rm.Find("?*{VI_ATTR_MANF_ID==0x9999}"));
        var info = Assert.Single(rm.FindResources("USB?*"));
        Assert.Equal("VisaCs Simulated USBTMC Instrument", info.Description);
    }

    [Fact]
    public void ReportsMissingDevices()
    {
        using var rm = CreateResourceManager();
        var ex = Assert.Throws<VisaException>(() => rm.Open("USB::0x1234::0x5678::NOPE::INSTR"));
        Assert.Equal(VisaStatus.ErrorResourceNotFound, ex.Status);
    }

    [Fact]
    public void AbortsBulkInOnTimeout()
    {
        var device = fixture.Backend.Devices[0];
        int aborts = device.AbortCount;
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        session.TimeoutMilliseconds = 200;
        Assert.Throws<VisaTimeoutException>(() => session.Query("DELAY? 600"));
        Assert.True(device.AbortCount > aborts);
        session.TimeoutMilliseconds = 5000;
        session.Clear();
        Assert.Equal(Instrument.Identity, session.Query("*IDN?"));
    }

    [Theory]
    [InlineData(true, true, 512)]
    [InlineData(false, false, 64)]
    [InlineData(true, false, 8)]
    public void WorksWithDeviceVariants(bool interruptEndpoint, bool termCharSupport, int packetSize)
    {
        var instrument = new SimulatedInstrument();
        var backend = new SimulatedUsbBackend();
        backend.Devices.Add(new SimulatedUsbTmcDevice(instrument, 0x0957, 0x1796, "VAR1",
            interruptEndpoint: interruptEndpoint, supportsTermChar: termCharSupport, maxPacketSize: (ushort)packetSize));
        using var rm = fixture.CreateResourceManager(backend);
        using var session = rm.Open("USB::0x0957::0x1796::VAR1::INSTR");
        session.TimeoutMilliseconds = 5000;
        Assert.Equal(instrument.Identity, session.Query("*IDN?"));
        Assert.Equal(20_000, session.QueryBinaryBlock("DATA:BIN? 20000").Length);
        session.TerminationCharacter = (byte)',';
        session.TerminationCharacterEnabled = true;
        session.WriteLine("ECHO? x,y");
        Assert.Equal("x,", session.ReadString());
        session.TerminationCharacter = (byte)'\n';
        Assert.Equal("y", session.ReadLine());
        session.WriteLine("*SRE 1;SRQ");
        session.Query("*OPC?");
        Assert.Equal(0x41, session.ReadStatusByte() & 0x41);
    }

    [Fact]
    public void PlainUsbTmcHasNoStatusByte()
    {
        var instrument = new SimulatedInstrument();
        var backend = new SimulatedUsbBackend();
        backend.Devices.Add(new SimulatedUsbTmcDevice(instrument, 0x1111, 0x2222, "TMC", usb488: false, interruptEndpoint: false));
        using var rm = fixture.CreateResourceManager(backend);
        using var session = rm.Open<UsbSession>("USB::0x1111::0x2222::TMC::INSTR");
        Assert.Equal(UsbProtocol.UsbTmc, session.UsbProtocol);
        Assert.Equal(VisaStatus.ErrorOperationNotSupported, Assert.Throws<VisaException>(() => session.ReadStatusByte()).Status);
        Assert.Equal(VisaStatus.ErrorOperationNotSupported, Assert.Throws<VisaException>(session.AssertTrigger).Status);
        Assert.Equal(instrument.Identity, session.Query("*IDN?"));
    }
}

public sealed class GpibTests
{
    private static (ResourceManager Rm, SimulatedGpibDriver Driver, SimulatedInstrument Instrument) Create()
    {
        var instrument = new SimulatedInstrument { Identity = "VisaCs,GPIB Instrument,0,1" };
        var driver = new SimulatedGpibDriver();
        driver.Instruments[22] = instrument;
        var rm = new ResourceManager(new ResourceManagerOptions
        {
            GpibDriver = driver,
            EnableLanDiscovery = false,
            EnableMdnsDiscovery = false,
            EnableUsb = false,
            EnableSerial = false,
        });
        return (rm, driver, instrument);
    }

    [Fact]
    public void QueriesAndPolls()
    {
        var (rm, _, instrument) = Create();
        using (rm)
        {
            using var session = rm.Open<GpibSession>("GPIB0::22::INSTR");
            Assert.Equal(22, session.PrimaryAddress);
            Assert.Equal(instrument.Identity, session.Query("*IDN?"));
            Assert.Equal(1000, session.QueryBinaryBlock("DATA:BIN? 1000").Length);
            session.WriteLine("*SRE 1;SRQ");
            Assert.Equal(0x41, session.ReadStatusByte() & 0x41);
            int triggers = instrument.TriggerCount;
            session.AssertTrigger();
            Assert.Equal(triggers + 1, instrument.TriggerCount);
            session.Clear();
            Assert.Equal(instrument.Identity, session.Query("*IDN?"));
        }
    }

    [Fact]
    public void ControlsRenThroughTheBoard()
    {
        var (rm, driver, _) = Create();
        using (rm)
        {
            using var session = rm.Open<GpibSession>("GPIB0::22::INSTR");
            session.ControlRen(RemoteLocalMode.AssertAddressLocalLockout);
            Assert.True(driver.RemoteEnable);
            Assert.Equal(LineState.Asserted, session.RenState);
            Assert.Equal(new byte[] { 0x3F, 0x20 | 22, 0x11 }, driver.SentCommands[^1]);
            session.ControlRen(RemoteLocalMode.Deassert);
            Assert.False(driver.RemoteEnable);
        }
    }

    [Fact]
    public void DeliversServiceRequests()
    {
        var (rm, _, instrument) = Create();
        using (rm)
        {
            using var session = rm.Open("GPIB0::22::INSTR");
            session.EnableEvent(EventType.ServiceRequest);
            instrument.RequestService();
            var e = Assert.IsType<ServiceRequestEventArgs>(session.WaitOnEvent(EventType.ServiceRequest, 5000));
            Assert.Equal(0x40, e.StatusByte!.Value & 0x40);
        }
    }

    [Fact]
    public void FindsListenersAndBoards()
    {
        var (rm, _, _) = Create();
        using (rm)
        {
            Assert.Equal(["GPIB0::22::INSTR", "GPIB0::INTFC"], rm.Find("GPIB?*"));
            using var board = rm.Open<GpibInterfaceSession>("GPIB0::INTFC");
            board.SendCommand([0x3F]);
            Assert.True(board.IsControllerInCharge);
            Assert.Equal(VisaStatus.ErrorNoListeners, Assert.Throws<VisaException>(() => rm.Open("GPIB0::5::INSTR")).Status);
        }
    }
}

public sealed class DiscoveryTests(SimulatorFixture fixture) : IClassFixture<SimulatorFixture>
{
    [Fact]
    public void FindsVxi11InstrumentsByPortmapperProbe()
    {
        var options = fixture.Simulator.CreateOptions();
        options.LanDiscoveryTargets.Add(IPAddress.Loopback);
        options.LanDiscoveryUsesBroadcast = false;
        options.LanDiscoveryTimeoutMilliseconds = 500;
        using var rm = new ResourceManager(options);
        Assert.Contains("TCPIP0::127.0.0.1::inst0::INSTR", rm.Find("TCPIP?*"));
        Assert.Contains("TCPIP0::127.0.0.1::inst0::INSTR", rm.Find("?*{VI_ATTR_TCPIP_ADDR==\"127.0.0.1\"}"));
        Assert.Empty(rm.Find("TCPIP?*{VI_ATTR_TCPIP_ADDR==\"10.9.9.9\"}"));
    }

    [Fact]
    public void FindsServicesWithMdns()
    {
        using var responder = new MdnsTestResponder();
        responder.Services.Add(("_hislip._tcp", "Sim HiSLIP", "sim-host", 4880, IPAddress.Parse("192.0.2.10"), new() { ["Manufacturer"] = "VisaCs" }));
        responder.Services.Add(("_hislip._tcp", "Sim HiSLIP alt", "sim-host2", 5000, IPAddress.Parse("192.0.2.11"), new()));
        responder.Services.Add(("_scpi-raw._tcp", "Sim RAW", "sim-host", 5025, IPAddress.Parse("192.0.2.10"), new()));
        responder.Start();

        var instances = MdnsDiscovery.Browse(["_hislip._tcp.local"], 500, responder.EndPoint);
        var instance = Assert.Single(instances, i => i.InstanceName == "Sim HiSLIP");
        Assert.Equal("sim-host.local", instance.HostName);
        Assert.Equal(4880, instance.Port);
        Assert.Equal("VisaCs", instance.Text["Manufacturer"]);
        Assert.Equal(IPAddress.Parse("192.0.2.10"), Assert.Single(instance.Addresses));

        var options = new ResourceManagerOptions
        {
            EnableLanDiscovery = false,
            MdnsEndPoint = responder.EndPoint,
            LanDiscoveryTimeoutMilliseconds = 500,
            EnableUsb = false,
            EnableSerial = false,
            EnableGpib = false,
        };
        using var rm = new ResourceManager(options);
        var found = rm.Find("?*");
        Assert.Contains("TCPIP0::192.0.2.10::hislip0::INSTR", found);
        Assert.Contains("TCPIP0::192.0.2.11::hislip0,5000::INSTR", found);
        Assert.Contains("TCPIP0::192.0.2.10::5025::SOCKET", found);
        Assert.DoesNotContain("TCPIP0::192.0.2.10::5025::SOCKET", rm.Find("?*::INSTR"));
        var info = rm.FindResources("TCPIP0::192.0.2.10::hislip0::INSTR").Single();
        Assert.Equal("Sim HiSLIP", info.Description);
    }

    [Fact]
    public void IncludesStaticResourcesAndAliases()
    {
        var options = new ResourceManagerOptions
        {
            EnableLanDiscovery = false, EnableMdnsDiscovery = false, EnableUsb = false, EnableSerial = false, EnableGpib = false,
        };
        options.StaticResources.Add("TCPIP::10.0.0.5::INSTR");
        options.Aliases["scope"] = "TCPIP0::10.0.0.5::inst0::INSTR";
        using var rm = new ResourceManager(options);
        var info = Assert.Single(rm.FindResources("?*"));
        Assert.Equal("TCPIP0::10.0.0.5::inst0::INSTR", info.ResourceName);
        Assert.Equal("scope", info.Alias);
    }

    [Fact]
    public void SearchesRealHardwareWithoutThrowing()
    {
        // Exercises the platform USB backend, serial enumeration and the GPIB driver probe.
        var options = new ResourceManagerOptions { EnableLanDiscovery = false, EnableMdnsDiscovery = false };
        using var rm = new ResourceManager(options);
        var found = rm.Find("?*");
        Assert.NotNull(found);
        if (OperatingSystem.IsWindows()) Assert.IsType<WinUsbBackend>(rm.UsbBackend);
    }
}

public sealed class SerialTests
{
    [Fact]
    public void ReportsMissingPorts()
    {
        using var rm = new ResourceManager();
        string name = OperatingSystem.IsWindows() ? "ASRL250::INSTR" : "ASRL/dev/ttyVISACS_NONE::INSTR";
        var ex = Assert.Throws<VisaException>(() => rm.Open(name));
        Assert.Equal(VisaStatus.ErrorResourceNotFound, ex.Status);
    }

    [Fact]
    public void RejectsUnsupportedInterfaces()
    {
        using var rm = new ResourceManager();
        var ex = Assert.Throws<VisaException>(() => rm.Open("PXI0::3-18.0::INSTR"));
        Assert.Equal(VisaStatus.ErrorResourceNotFound, ex.Status);
        Assert.Equal(VisaStatus.ErrorInvalidAccessMode,
            Assert.Throws<VisaException>(() => rm.Open("ASRL1::INSTR", AccessModes.ExclusiveLock | AccessModes.SharedLock)).Status);
    }
}

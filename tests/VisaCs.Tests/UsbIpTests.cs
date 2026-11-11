using System.Buffers.Binary;
using System.Runtime.InteropServices;
using VisaCs.Simulation;
using VisaCs.Usb;

namespace VisaCs.Tests;

/// <summary>
/// Plugs the simulated USBTMC instrument into the real Linux USB stack through USB/IP (vhci_hcd), then runs the
/// shared protocol suite through the usbfs and libusb backends. Requires Linux, root, the vhci-hcd module and
/// VISACS_TEST_USBIP=1:  sudo modprobe vhci-hcd; sudo VISACS_TEST_USBIP=1 dotnet test --filter KernelTests
/// </summary>
public sealed unsafe partial class UsbIpFixture : IDisposable
{
    private const string Vhci = "/sys/devices/platform/vhci_hcd.0";
    public const string Serial = "USBIP0001";
    public const string Resource = "USB::0x1234::0x5678::USBIP0001::INSTR";

    private readonly UsbIpServer? _server;
    private readonly int _port = -1;

    public UsbIpFixture()
    {
        Enabled = OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("VISACS_TEST_USBIP") == "1" && Directory.Exists(Vhci);
        if (!Enabled) return;

        Device = new SimulatedUsbTmcDevice(Instrument, 0x1234, 0x5678, Serial);
        _server = new UsbIpServer(Device).Start();
        _port = Attach(_server.Port, _server.BusId);

        // Wait for the kernel to enumerate the device.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (new UsbFsBackend().EnumerateDevices().Any(d => d.SerialNumber == Serial)) return;
            Thread.Sleep(100);
        }
        throw new InvalidOperationException("The USB/IP device did not enumerate.");
    }

    public bool Enabled { get; }

    public SimulatedInstrument Instrument { get; } = new();

    public SimulatedUsbTmcDevice? Device { get; }

    public UsbIpServer? Server => _server;

    public ResourceManager CreateResourceManager(IUsbBackend backend) => new(new ResourceManagerOptions
    {
        UsbBackend = backend,
        EnableLanDiscovery = false,
        EnableMdnsDiscovery = false,
        EnableSerial = false,
        EnableGpib = false,
    });

    /// <summary>Does what "usbip attach" does: import the device, then hand the socket to vhci_hcd.</summary>
    private static int Attach(int serverPort, string busId)
    {
        int fd = socket(2 /* AF_INET */, 1 /* SOCK_STREAM */, 0);
        if (fd < 0) throw new IOException($"socket failed ({Marshal.GetLastPInvokeError()}).");
        try
        {
            Span<byte> address = stackalloc byte[16];
            address.Clear();
            BinaryPrimitives.WriteUInt16LittleEndian(address, 2);
            BinaryPrimitives.WriteUInt16BigEndian(address[2..], (ushort)serverPort);
            address[4] = 127;
            address[7] = 1;
            fixed (byte* a = address)
                if (connect(fd, a, 16) != 0) throw new IOException($"connect failed ({Marshal.GetLastPInvokeError()}).");

            var request = new byte[40];
            BinaryPrimitives.WriteUInt16BigEndian(request, 0x0111);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0x8003);
            System.Text.Encoding.ASCII.GetBytes(busId).CopyTo(request, 8);
            SendAll(fd, request);
            var reply = new byte[8 + 312];
            ReceiveAll(fd, reply);
            if (BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(4)) != 0) throw new IOException("USB/IP import refused.");
            uint busnum = BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(8 + 288));
            uint devnum = BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(8 + 292));
            uint speed = BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(8 + 296));

            int port = FreeHighSpeedPort();
            File.WriteAllText(Path.Combine(Vhci, "attach"), $"{port} {fd} {(busnum << 16) | devnum} {speed}");
            return port;
        }
        finally
        {
            close(fd); // the kernel keeps its own reference to the socket
        }
    }

    private static int FreeHighSpeedPort()
    {
        foreach (var line in File.ReadAllLines(Path.Combine(Vhci, "status")).Skip(1))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 3 && fields[0] == "hs" && fields[2] == "004") return int.Parse(fields[1]);
        }
        throw new IOException("No free high-speed vhci_hcd port.");
    }

    private static void SendAll(int fd, byte[] data)
    {
        fixed (byte* p = data)
            for (int sent = 0; sent < data.Length;)
            {
                nint n = send(fd, p + sent, data.Length - sent, 0);
                if (n <= 0) throw new IOException("send failed.");
                sent += (int)n;
            }
    }

    private static void ReceiveAll(int fd, byte[] data)
    {
        fixed (byte* p = data)
            for (int got = 0; got < data.Length;)
            {
                nint n = recv(fd, p + got, data.Length - got, 0);
                if (n <= 0) throw new IOException("recv failed.");
                got += (int)n;
            }
    }

    public void Dispose()
    {
        if (_port >= 0)
        {
            try { File.WriteAllText(Path.Combine(Vhci, "detach"), _port.ToString()); } catch { /* already gone */ }
        }
        _server?.Dispose();
    }

    [LibraryImport("libc.so.6", SetLastError = true)] private static partial int socket(int domain, int type, int protocol);
    [LibraryImport("libc.so.6", SetLastError = true)] private static partial int connect(int fd, byte* address, int length);
    [LibraryImport("libc.so.6", SetLastError = true)] private static partial nint send(int fd, byte* buffer, nint length, int flags);
    [LibraryImport("libc.so.6", SetLastError = true)] private static partial nint recv(int fd, byte* buffer, nint length, int flags);
    [LibraryImport("libc.so.6", SetLastError = true)] private static partial int close(int fd);
}

[CollectionDefinition("usbip", DisableParallelization = true)]
public sealed class UsbIpCollection : ICollectionFixture<UsbIpFixture>;

/// <summary>The shared protocol suite against the real kernel USB stack through usbfs.</summary>
[Collection("usbip")]
public sealed class UsbFsKernelTests(UsbIpFixture fixture) : MessageBasedProtocolTests
{

    protected override bool Enabled => fixture.Enabled;
    protected override ResourceManager CreateResourceManager() => fixture.CreateResourceManager(new UsbFsBackend());
    protected override string Resource => UsbIpFixture.Resource;
    protected override SimulatedInstrument Instrument => fixture.Instrument;

    [Fact]
    public void ActivatesWhenRequested()
    {
        if (Environment.GetEnvironmentVariable("VISACS_TEST_USBIP") != "1") return;
        Assert.True(fixture.Enabled, "VISACS_TEST_USBIP=1 but the USB/IP fixture is not active (Linux, root and vhci-hcd needed).");
        Assert.True(fixture.Server!.UrbCount > 0, "No URBs reached the simulated device.");
    }

    [Fact]
    public void EnumeratesThroughSysfs()
    {
        if (!fixture.Enabled) return;
        var device = Assert.Single(new UsbFsBackend().EnumerateDevices(), d => d.SerialNumber == UsbIpFixture.Serial);
        Assert.Equal("VisaCs", device.Manufacturer);
        var intf = Assert.Single(device.Interfaces);
        Assert.True(intf.IsUsbTmc);
        Assert.Equal(3, intf.Endpoints.Count);
        using var rm = CreateResourceManager();
        Assert.Contains("USB0::0x1234::0x5678::USBIP0001::0::INSTR", rm.Find("USB?*"));
        using var session = rm.Open<UsbSession>(Resource);
        Assert.Equal("usbfs", session.BackendName);
        Assert.True(session.Capabilities.AcceptsTrigger);
    }

    [Fact]
    public void ServiceRequestsArriveOnTheInterruptEndpoint()
    {
        if (!fixture.Enabled) return;
        using var rm = CreateResourceManager();
        using var session = rm.Open(Resource);
        session.EnableEvent(EventType.ServiceRequest, EventMechanism.Queue);
        Instrument.RequestService();
        var e = Assert.IsType<ServiceRequestEventArgs>(session.WaitOnEvent(EventType.ServiceRequest, 5000));
        Assert.Equal(0x40, e.StatusByte!.Value & 0x40);
        session.WriteLine("*CLS");
        session.Query("*OPC?");
    }

    [Fact]
    public void TimeoutsUnlinkUrbs()
    {
        if (!fixture.Enabled) return;
        int unlinks = fixture.Server!.UnlinkCount;
        using var rm = CreateResourceManager();
        using var session = rm.Open(Resource);
        session.TimeoutMilliseconds = 200;
        Assert.Throws<VisaTimeoutException>(() => session.Query($"DELAY? {SlowResponseMilliseconds}"));
        Assert.True(fixture.Server.UnlinkCount > unlinks, "The kernel did not cancel the pending bulk-in URB.");
        session.TimeoutMilliseconds = RecoveryTimeoutMilliseconds;
        session.Clear();
        Assert.Equal(Instrument.Identity, session.Query("*IDN?"));
    }
}

/// <summary>The shared protocol suite against the real kernel USB stack through libusb.</summary>
[Collection("usbip")]
public sealed class LibUsbKernelTests(UsbIpFixture fixture) : MessageBasedProtocolTests
{

    protected override bool Enabled => fixture.Enabled && LibUsbBackend.IsAvailable;
    protected override ResourceManager CreateResourceManager() => fixture.CreateResourceManager(new LibUsbBackend());
    protected override string Resource => UsbIpFixture.Resource;
    protected override SimulatedInstrument Instrument => fixture.Instrument;

    [Fact]
    public void EnumeratesThroughLibUsb()
    {
        if (!Enabled) return;
        using var backend = new LibUsbBackend();
        var device = Assert.Single(backend.EnumerateDevices(), d => d.SerialNumber == UsbIpFixture.Serial);
        Assert.Equal("Simulated USBTMC Instrument", device.Product);
        var intf = Assert.Single(device.Interfaces);
        Assert.Equal(0xFE, intf.Class);
        Assert.Contains(intf.Endpoints, e => e.Type == UsbTransferType.Interrupt && e.IsInput);
        Assert.Equal(512, intf.Endpoints.First(e => e.Type == UsbTransferType.Bulk).MaxPacketSize);
    }
}

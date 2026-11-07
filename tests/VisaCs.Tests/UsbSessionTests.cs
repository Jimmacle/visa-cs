using System.Diagnostics;
using VisaCs.Simulation;
using VisaCs.Usb;

namespace VisaCs.Tests;

/// <summary>USB sessions sharing a device, timeouts and interrupt packets, against simulated devices.</summary>
public sealed class UsbSessionTests
{
    private static ResourceManager CreateResourceManager(IUsbBackend backend) => new(new ResourceManagerOptions
    {
        UsbBackend = backend,
        EnableLanDiscovery = false,
        EnableMdnsDiscovery = false,
        EnableSerial = false,
        EnableGpib = false,
    });

    private static (SimulatedUsbBackend Backend, SimulatedUsbTmcDevice Device, SimulatedInstrument Instrument) Create(ushort productId, string serial,
        bool usb488 = true)
    {
        var instrument = new SimulatedInstrument();
        var device = new SimulatedUsbTmcDevice(instrument, 0x1234, productId, serial, usb488: usb488);
        var backend = new SimulatedUsbBackend();
        backend.Devices.Add(device);
        return (backend, device, instrument);
    }

    [Fact]
    public void ControlTransfersUseTheSessionTimeout()
    {
        var (sim, _, _) = Create(0x7001, "CTL1");
        var backend = new RecordingBackend(sim);
        using var rm = CreateResourceManager(backend);
        using var session = rm.Open<UsbSession>("USB::0x1234::0x7001::CTL1::INSTR");

        session.TimeoutMilliseconds = Timeout.Infinite;
        session.ControlIn(0xA1, 7, 0, 0, 0x18);
        session.ControlOut(0x02, 1, 0, 0x01, ReadOnlySpan<byte>.Empty);
        Assert.Equal(new[] { 0, 0 }, backend.ControlTimeouts.TakeLast(2)); // no limit

        session.TimeoutMilliseconds = 1500;
        session.ControlIn(0xA1, 7, 0, 0, 0x18);
        Assert.InRange(backend.ControlTimeouts[^1], 1000, 1500);
    }

    [Fact]
    public void SessionsSharingADeviceUseNewTags()
    {
        var (backend, device, instrument) = Create(0x7002, "TAG1");
        using var rm = CreateResourceManager(backend);
        using var a = rm.Open("USB::0x1234::0x7002::TAG1::INSTR");
        using var b = rm.Open("USB::0x1234::0x7002::TAG1::INSTR");
        a.WriteLine("*CLS");
        b.WriteLine("*CLS");
        Assert.Equal(instrument.Identity, a.Query("*IDN?"));
        Assert.Equal(instrument.Identity, b.Query("*IDN?"));
        Assert.Equal(0, device.RepeatedTagCount);
    }

    [Fact]
    public void WaitingForAnotherSessionEndsAtTheTimeout()
    {
        var (backend, _, _) = Create(0x7003, "GATE1");
        using var rm = CreateResourceManager(backend);
        using var a = rm.Open("USB::0x1234::0x7003::GATE1::INSTR");
        using var b = rm.Open("USB::0x1234::0x7003::GATE1::INSTR");
        a.TimeoutMilliseconds = 2000;
        b.TimeoutMilliseconds = 200;

        // Nothing to read: a's read holds the device until its own timeout.
        Exception? readError = null;
        var reader = new Thread(() =>
        {
            try { a.ReadBytes(16); } catch (Exception ex) { readError = ex; }
        });
        reader.Start();
        Thread.Sleep(200);
        var watch = Stopwatch.StartNew();
        Assert.Throws<VisaTimeoutException>(() => b.Query("*IDN?"));
        Assert.True(watch.ElapsedMilliseconds < 1500, $"b waited {watch.ElapsedMilliseconds} ms for a's read.");
        Assert.True(reader.Join(5000));
        Assert.IsType<VisaTimeoutException>(readError);
        b.TimeoutMilliseconds = 2000;
        b.Clear();
        Assert.NotEmpty(b.Query("*IDN?"));
    }

    [Fact]
    public void SessionsShareTheHandleOfTheirBackendOnly()
    {
        var (a, device, instrument) = Create(0x7004, "SHARE1");
        var b = new SimulatedUsbBackend();
        b.Devices.Add(device);
        const string resource = "USB::0x1234::0x7004::SHARE1::INSTR";
        using var rm1 = CreateResourceManager(a);
        using var rm2 = CreateResourceManager(a);
        using var rm3 = CreateResourceManager(b);
        using (var s1 = rm1.Open(resource))
        using (var s2 = rm2.Open(resource))
        {
            Assert.Equal(instrument.Identity, s1.Query("*IDN?"));
            Assert.Equal(instrument.Identity, s2.Query("*IDN?"));
            // Another backend instance cannot use that handle; the device allows one handle at a time.
            Assert.Equal(VisaStatus.ErrorResourceBusy, Assert.Throws<VisaException>(() => rm3.Open(resource)).Status);
        }
        using var s3 = rm3.Open(resource);
        Assert.Equal(instrument.Identity, s3.Query("*IDN?"));
    }

    [Fact]
    public void RawSessionsReceiveEveryInterruptPacket()
    {
        var (backend, device, _) = Create(0x7005, "RAW1", usb488: false);
        using var rm = CreateResourceManager(backend);
        using var session = rm.Open<UsbRawSession>("USB::0x1234::0x7005::RAW1::RAW");
        session.EnableEvent(EventType.UsbInterrupt, EventMechanism.Queue);
        // Looks like a USB488 status byte response, but this interface is no USB488 interface.
        device.SendInterrupt([0x85, 0x12]);
        var e = Assert.IsType<UsbInterruptEventArgs>(session.WaitOnEvent(EventType.UsbInterrupt, 5000));
        Assert.Equal([0x85, 0x12], e.Data);
    }

    [Fact]
    public async Task DisposingTheSimulatedHandleEndsPendingTransfers()
    {
        var (backend, _, _) = Create(0x7006, "DISP1");
        var handle = backend.Open(backend.EnumerateDevices()[0], 0);
        var interrupt = handle.InterruptTransferAsync(0x83, new byte[8], 0).AsTask();
        var bulk = Task.Run(() => handle.BulkTransfer(0x82, new byte[512], 0));
        await Task.Delay(100);
        Assert.False(interrupt.IsCompleted || bulk.IsCompleted);
        handle.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => interrupt.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => bulk.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Throws<ObjectDisposedException>(() => handle.ControlTransfer(0x80, 6, 0x0100, 0, new byte[18], 1000));
        backend.Open(backend.EnumerateDevices()[0], 0).Dispose(); // the device can be opened again
    }

    [Fact]
    public void ParsesDescriptors()
    {
        Assert.Equal("", UsbDescriptorParser.ParseString([1, 3]));
        Assert.Equal("", UsbDescriptorParser.ParseString([0, 3, 0x41, 0]));
        Assert.Equal("AB", UsbDescriptorParser.ParseString([6, 3, 0x41, 0, 0x42, 0]));
        byte[] configuration =
        [
            9, 2, 41, 0, 2, 1, 0, 0x80, 50,
            9, 4, 1, 0, 1, 0xFF, 0, 0, 0,
            7, 5, 0x81, 2, 0x00, 0x02, 0,
            9, 4, 0, 0, 1, 0xFE, 3, 1, 0,
            7, 5, 0x02, 2, 0x40, 0x00, 0,
        ];
        var interfaces = UsbDescriptorParser.ParseConfiguration(configuration);
        Assert.Equal([0, 1], interfaces.Select(i => (int)i.Number));
        Assert.True(interfaces[0].IsUsbTmc);
        Assert.Equal(512, interfaces[1].FindEndpoint(UsbTransferType.Bulk, input: true)!.MaxPacketSize);
    }

    [Fact]
    public void CreatesTheBackendOfTheOperatingSystem()
    {
        using var backend = UsbBackends.CreateDefault();
        if (OperatingSystem.IsWindows()) Assert.IsType<WinUsbBackend>(backend);
        else if (OperatingSystem.IsLinux() && UsbFsBackend.IsAvailable) Assert.IsType<UsbFsBackend>(backend);
        else Assert.Equal(LibUsbBackend.IsAvailable, backend is LibUsbBackend);
    }

    /// <summary>Forwards to the simulated backend and records the timeouts of control transfers.</summary>
    private sealed class RecordingBackend(SimulatedUsbBackend inner) : IUsbBackend
    {
        public List<int> ControlTimeouts { get; } = new();

        public string Name => inner.Name;

        public IReadOnlyList<UsbDeviceInfo> EnumerateDevices() => inner.EnumerateDevices();

        public IUsbDeviceHandle Open(UsbDeviceInfo device, int interfaceNumber) => new Handle(this, inner.Open(device, interfaceNumber));

        public void Dispose() => inner.Dispose();

        private sealed class Handle(RecordingBackend owner, IUsbDeviceHandle inner) : IUsbDeviceHandle
        {
            public UsbDeviceInfo Device => inner.Device;

            public UsbInterfaceInfo Interface => inner.Interface;

            private void Record(int timeoutMilliseconds)
            {
                lock (owner.ControlTimeouts) owner.ControlTimeouts.Add(timeoutMilliseconds);
            }

            public int ControlTransfer(byte requestType, byte request, ushort value, ushort index, Span<byte> buffer, int timeoutMilliseconds)
            {
                Record(timeoutMilliseconds);
                return inner.ControlTransfer(requestType, request, value, index, buffer, timeoutMilliseconds);
            }

            public ValueTask<int> ControlTransferAsync(byte requestType, byte request, ushort value, ushort index, Memory<byte> buffer,
                int timeoutMilliseconds, CancellationToken cancellationToken = default)
            {
                Record(timeoutMilliseconds);
                return inner.ControlTransferAsync(requestType, request, value, index, buffer, timeoutMilliseconds, cancellationToken);
            }

            public int BulkTransfer(byte endpoint, Span<byte> buffer, int timeoutMilliseconds) => inner.BulkTransfer(endpoint, buffer, timeoutMilliseconds);

            public ValueTask<int> BulkTransferAsync(byte endpoint, Memory<byte> buffer, int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
                inner.BulkTransferAsync(endpoint, buffer, timeoutMilliseconds, cancellationToken);

            public int InterruptTransfer(byte endpoint, Span<byte> buffer, int timeoutMilliseconds) =>
                inner.InterruptTransfer(endpoint, buffer, timeoutMilliseconds);

            public ValueTask<int> InterruptTransferAsync(byte endpoint, Memory<byte> buffer, int timeoutMilliseconds,
                CancellationToken cancellationToken = default) =>
                inner.InterruptTransferAsync(endpoint, buffer, timeoutMilliseconds, cancellationToken);

            public void ClearHalt(byte endpoint) => inner.ClearHalt(endpoint);

            public void Dispose() => inner.Dispose();
        }
    }
}

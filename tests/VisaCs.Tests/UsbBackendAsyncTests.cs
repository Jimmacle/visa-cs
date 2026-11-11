using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using VisaCs.Usb;

namespace VisaCs.Tests;

/// <summary>
/// Transfers of a USB backend (asynchronous ones, and synchronous ones on the same machinery) against the real Linux
/// USB stack: the simulated USBTMC instrument is attached through USB/IP (see <see cref="UsbIpFixture"/>). Needs
/// Linux, root, vhci-hcd and VISACS_TEST_USBIP=1.
/// </summary>
public abstract class UsbBackendAsyncTests(UsbIpFixture fixture)
{
    private const int HeaderSize = 12;
    private const int Timeout = 5000;
    protected const int NoLimit = System.Threading.Timeout.Infinite;
    private static int _tag;

    protected UsbIpFixture Fixture { get; } = fixture;

    protected abstract bool Enabled { get; }

    protected abstract IUsbBackend CreateBackend();

    /// <summary>A backend and the opened simulated instrument; disposing closes both.</summary>
    protected sealed class Connection(IUsbBackend backend, IUsbDeviceHandle handle) : IDisposable
    {
        public IUsbBackend Backend { get; } = backend;
        public IUsbDeviceHandle Handle { get; } = handle;
        public byte BulkOut => Handle.Interface.FindEndpoint(UsbTransferType.Bulk, input: false)!.Address;
        public byte BulkIn => Handle.Interface.FindEndpoint(UsbTransferType.Bulk, input: true)!.Address;
        public byte InterruptIn => Handle.Interface.FindEndpoint(UsbTransferType.Interrupt, input: true)!.Address;

        public void Dispose()
        {
            Handle.Dispose();
            Backend.Dispose();
        }
    }

    protected Connection Connect()
    {
        var backend = CreateBackend();
        try
        {
            var device = Assert.Single(backend.EnumerateDevices(), d => d.SerialNumber == UsbIpFixture.Serial);
            var connection = new Connection(backend, backend.Open(device, 0));
            // Start from a clean device: a response to a transfer an earlier test cancelled may still be waiting.
            Task.Run(() => DeviceClearAsync(connection)).GetAwaiter().GetResult();
            return connection;
        }
        catch
        {
            backend.Dispose();
            throw;
        }
    }

    private static byte NextTag() => (byte)(Interlocked.Increment(ref _tag) % 254 + 1);

    private static int RoundUp(int value, int multiple) => (value + multiple - 1) / multiple * multiple;

    private static byte[] DevDepMsgOut(byte tag, string message)
    {
        var payload = Encoding.ASCII.GetBytes(message);
        var packet = new byte[RoundUp(HeaderSize + payload.Length, 4)];
        packet[0] = 1; // DEV_DEP_MSG_OUT
        packet[1] = tag;
        packet[2] = (byte)~tag;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)payload.Length);
        packet[8] = 1; // EOM
        payload.CopyTo(packet, HeaderSize);
        return packet;
    }

    private static byte[] RequestDevDepMsgIn(byte tag, int size)
    {
        var packet = new byte[HeaderSize];
        packet[0] = 2; // REQUEST_DEV_DEP_MSG_IN
        packet[1] = tag;
        packet[2] = (byte)~tag;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)size);
        return packet;
    }

    private static async Task SendAsync(Connection c, string message)
    {
        var packet = DevDepMsgOut(NextTag(), message);
        Assert.Equal(packet.Length, await c.Handle.BulkTransferAsync(c.BulkOut, packet, Timeout));
    }

    /// <summary>Requests a response and reads it with one bulk-IN transfer.</summary>
    private static async Task<string> ReadResponseAsync(Connection c, int maxSize = 4096, int timeoutMilliseconds = Timeout)
    {
        byte tag = NextTag();
        Assert.Equal(HeaderSize, await c.Handle.BulkTransferAsync(c.BulkOut, RequestDevDepMsgIn(tag, maxSize), Timeout));
        var buffer = new byte[RoundUp(HeaderSize + maxSize + 3, 512)];
        int n = await c.Handle.BulkTransferAsync(c.BulkIn, buffer, timeoutMilliseconds);
        Assert.True(n >= HeaderSize, $"Short bulk-IN transfer ({n} bytes).");
        Assert.Equal(2, buffer[0]);
        Assert.Equal(tag, buffer[1]);
        int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4));
        Assert.Equal(RoundUp(HeaderSize + size, 4), n);
        Assert.Equal(1, buffer[8] & 1); // EOM
        return Encoding.ASCII.GetString(buffer, HeaderSize, size).TrimEnd('\n');
    }

    protected static async Task<string> QueryAsync(Connection c, string query)
    {
        await SendAsync(c, query + "\n");
        return await ReadResponseAsync(c);
    }

    /// <summary>USBTMC INITIATE_CLEAR and CHECK_CLEAR_STATUS, then CLEAR_FEATURE(ENDPOINT_HALT) on bulk-OUT.</summary>
    private static async Task DeviceClearAsync(Connection c)
    {
        var status = new byte[2];
        Assert.Equal(1, await c.Handle.ControlTransferAsync(0xA1, 5, 0, c.Handle.Interface.Number, status.AsMemory(0, 1), Timeout));
        Assert.Equal(1, status[0]);
        for (int i = 0; ; i++)
        {
            Assert.True(await c.Handle.ControlTransferAsync(0xA1, 6, 0, c.Handle.Interface.Number, status, Timeout) >= 1);
            if (status[0] != 2) break; // STATUS_PENDING
            Assert.True(i < 100, "CHECK_CLEAR_STATUS stays pending.");
            await Task.Delay(10);
        }
        Assert.Equal(1, status[0]);
        Assert.Equal(0, await c.Handle.ControlTransferAsync(0x02, 1, 0, c.BulkOut, Memory<byte>.Empty, Timeout));
    }

    /// <summary>Reads and discards interrupt-IN packets left by earlier tests.</summary>
    private static async Task DrainInterruptsAsync(Connection c)
    {
        var packet = new byte[8];
        try
        {
            while (true) await c.Handle.InterruptTransferAsync(c.InterruptIn, packet, 100);
        }
        catch (UsbTimeoutException)
        {
        }
    }

    [Fact]
    public async Task ControlTransfers()
    {
        if (!Enabled) return;
        using var c = Connect();

        var descriptor = new byte[18];
        Assert.Equal(18, await c.Handle.ControlTransferAsync(0x80, 6, 0x0100, 0, descriptor, Timeout));
        var parsed = UsbDescriptorParser.ParseDevice(descriptor);
        Assert.Equal(0x1234, parsed.VendorId);
        Assert.Equal(0x5678, parsed.ProductId);

        // A buffer larger than the descriptor: the transfer returns the actual length.
        var configuration = new byte[255];
        int n = await c.Handle.ControlTransferAsync(0x80, 6, 0x0200, 0, configuration, Timeout);
        Assert.Equal(BinaryPrimitives.ReadUInt16LittleEndian(configuration.AsSpan(2)), n);
        Assert.True(UsbDescriptorParser.ParseConfiguration(configuration.AsSpan(0, n)).Single().IsUsbTmc);

        var serial = new byte[255];
        n = await c.Handle.ControlTransferAsync(0x80, 6, 0x0303, 0x0409, serial, Timeout);
        Assert.Equal(UsbIpFixture.Serial, UsbDescriptorParser.ParseString(serial.AsSpan(0, n)));

        // USBTMC GET_CAPABILITIES (class request to the interface).
        var capabilities = new byte[0x18];
        Assert.Equal(0x18, await c.Handle.ControlTransferAsync(0xA1, 7, 0, c.Handle.Interface.Number, capabilities, Timeout));
        Assert.Equal(1, capabilities[0]);
        Assert.Equal(0x0100, BinaryPrimitives.ReadUInt16LittleEndian(capabilities.AsSpan(2)));

        // A control OUT transfer without data stage.
        Assert.Equal(0, await c.Handle.ControlTransferAsync(0x02, 1, 0, c.BulkOut, Memory<byte>.Empty, Timeout));
    }

    [Fact]
    public async Task StallsMapLikeTheSynchronousPath()
    {
        if (!Enabled) return;
        using var c = Connect();
        var buffer = new byte[64];
        // GET_DESCRIPTOR of a type the device does not have: the device stalls the request.
        var sync = Assert.Throws<UsbException>(() => c.Handle.ControlTransfer(0x80, 6, 0x0F00, 0, buffer, Timeout));
        var async = await Assert.ThrowsAsync<UsbException>(async () => await c.Handle.ControlTransferAsync(0x80, 6, 0x0F00, 0, buffer, Timeout));
        Assert.Equal(sync.Error, async.Error);
        Assert.Equal(UsbError.Stall, async.Error);
        Assert.Equal(sync.Message, async.Message);
        // The default pipe works after the stall.
        Assert.Equal(18, await c.Handle.ControlTransferAsync(0x80, 6, 0x0100, 0, buffer.AsMemory(0, 18), Timeout));
    }

    [Fact]
    public async Task BulkQuery()
    {
        if (!Enabled) return;
        using var c = Connect();
        Assert.Equal(Fixture.Instrument.Identity, await QueryAsync(c, "*IDN?"));
        // Synchronous and asynchronous transfers mix on one handle.
        var request = DevDepMsgOut(NextTag(), "ECHO? mixed\n");
        Assert.Equal(request.Length, c.Handle.BulkTransfer(c.BulkOut, request, Timeout));
        Assert.Equal("mixed", await ReadResponseAsync(c));
    }

    [Fact]
    public async Task LargeBulkInTransfer()
    {
        if (!Enabled) return;
        using var c = Connect();
        const int size = 200_000;
        await SendAsync(c, $"DATA:LARGE? {size}\n");
        string response = await ReadResponseAsync(c, size + 1);
        Assert.Equal(size, response.Length);
        Assert.True(response.All(ch => ch == 'A'));
    }

    [Fact]
    public async Task BulkInTimeoutDiscardsTheTransfer()
    {
        if (!Enabled) return;
        using var c = Connect();
        int unlinks = Fixture.Server!.UnlinkCount;

        // The device answers after 800 ms; the read gives up after 200 ms.
        await SendAsync(c, "DELAY? 800\n");
        byte tag = NextTag();
        await c.Handle.BulkTransferAsync(c.BulkOut, RequestDevDepMsgIn(tag, 256), Timeout);
        var buffer = new byte[512];
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<UsbTimeoutException>(async () => await c.Handle.BulkTransferAsync(c.BulkIn, buffer, 200));
        Assert.InRange(watch.ElapsedMilliseconds, 150, 3000);
        Assert.True(Fixture.Server.UnlinkCount > unlinks, "The kernel did not cancel the bulk-IN URB.");

        // Recover the way the USBTMC layer does after a timeout, then the device works again.
        await DeviceClearAsync(c);
        Assert.Equal(Fixture.Instrument.Identity, await QueryAsync(c, "*IDN?"));
    }

    [Fact]
    public async Task CancellationDiscardsTheTransfer()
    {
        if (!Enabled) return;
        using var c = Connect();
        int unlinks = Fixture.Server!.UnlinkCount;

        // A read without a pending request: the device NAKs until the caller cancels.
        var buffer = new byte[512];
        using var cts = new CancellationTokenSource(200);
        var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await c.Handle.BulkTransferAsync(c.BulkIn, buffer, NoLimit, cts.Token));
        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.InRange(watch.ElapsedMilliseconds, 150, 3000);
        Assert.True(Fixture.Server.UnlinkCount > unlinks, "The kernel did not cancel the bulk-IN URB.");
        Assert.Equal(Fixture.Instrument.Identity, await QueryAsync(c, "*IDN?"));

        // Cancelled while a response is on its way; a device clear discards it.
        await SendAsync(c, "DELAY? 300\n");
        await c.Handle.BulkTransferAsync(c.BulkOut, RequestDevDepMsgIn(NextTag(), 256), Timeout);
        using var cts2 = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await c.Handle.BulkTransferAsync(c.BulkIn, buffer, Timeout, cts2.Token));
        await DeviceClearAsync(c);
        Assert.Equal(Fixture.Instrument.Identity, await QueryAsync(c, "*IDN?"));

        // An already cancelled token does not start a transfer.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await c.Handle.ControlTransferAsync(0x80, 6, 0x0100, 0, buffer.AsMemory(0, 18), Timeout, new CancellationToken(true)));
    }

    [Fact]
    public async Task InterruptReadPendsDuringBulkTransfers()
    {
        if (!Enabled) return;
        using var c = Connect();
        await DrainInterruptsAsync(c);

        var packet = new byte[8];
        var interrupt = c.Handle.InterruptTransferAsync(c.InterruptIn, packet, 10_000).AsTask();
        Assert.Equal(Fixture.Instrument.Identity, await QueryAsync(c, "*IDN?"));
        Assert.Equal("1", await QueryAsync(c, "*OPC?"));
        await Task.Delay(100);
        Assert.False(interrupt.IsCompleted, "The interrupt read completed without a packet.");

        // READ_STATUS_BYTE: the device answers with a status packet on the interrupt endpoint.
        byte tag = 0x42;
        var response = new byte[3];
        Assert.Equal(3, await c.Handle.ControlTransferAsync(0xA1, 128, tag, c.Handle.Interface.Number, response, Timeout));
        Assert.Equal(1, response[0]);
        Assert.Equal(2, await interrupt.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0x80 | tag, packet[0]);
    }

    [Fact]
    public async Task DisposeEndsPendingTransfers()
    {
        if (!Enabled) return;
        var c = Connect();
        try
        {
            await DrainInterruptsAsync(c);
            var interrupt = c.Handle.InterruptTransferAsync(c.InterruptIn, new byte[8], NoLimit).AsTask();
            var bulk = c.Handle.BulkTransferAsync(c.BulkIn, new byte[512], NoLimit).AsTask();
            await Task.Delay(100);
            Assert.False(interrupt.IsCompleted || bulk.IsCompleted);

            var watch = Stopwatch.StartNew();
            c.Handle.Dispose();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => interrupt.WaitAsync(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => bulk.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(watch.ElapsedMilliseconds < 5000);
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await c.Handle.BulkTransferAsync(c.BulkIn, new byte[512], 100));
        }
        finally
        {
            c.Dispose();
        }

        using var again = Connect();
        Assert.Equal(Fixture.Instrument.Identity, await QueryAsync(again, "*IDN?"));
    }

    [Fact]
    public async Task PendingTransfersHoldNoThreads()
    {
        if (!Enabled) return;
        using var c = Connect();
        await DrainInterruptsAsync(c); // also starts the backend's completion thread

        // The simulated device blocks one thread-pool thread for each IN transfer the host has pending. Let the
        // pool create threads on demand without throttling, so that the device gets its threads right away, and so
        // would an implementation that blocked a pool thread per transfer.
        const int count = 100;
        ThreadPool.GetMinThreads(out int workers, out int io);
        ThreadPool.SetMinThreads(Math.Max(workers, 2 * count + 32), io);
        using var cts = new CancellationTokenSource();
        var transfers = new List<Task<int>>();
        try
        {
            int before = ThreadCount();
            int urbs = Fixture.Server!.UrbCount;
            for (int i = 0; i < count; i++)
                transfers.Add(c.Handle.InterruptTransferAsync(c.InterruptIn, new byte[8], NoLimit, cts.Token).AsTask());
            for (int i = 0; i < 100 && Fixture.Server.UrbCount < urbs + count; i++) await Task.Delay(50);
            Assert.True(Fixture.Server.UrbCount >= urbs + count, "The transfers did not reach the device.");
            await Task.Delay(500);
            int during = ThreadCount();
            Assert.All(transfers, t => Assert.False(t.IsCompleted));
            // The device's threads account for 'count'; a thread per pending transfer would double it.
            Assert.True(during - before <= count + count / 2, $"{during - before} more threads with {count} pending transfers.");

            cts.Cancel();
            foreach (var t in transfers)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            cts.Cancel();
            ThreadPool.SetMinThreads(workers, io);
        }
        Assert.Equal(Fixture.Instrument.Identity, await QueryAsync(c, "*IDN?"));
    }

    private static int ThreadCount() => Directory.GetDirectories("/proc/self/task").Length;

    [Fact]
    public async Task SynchronousTimeoutDiscardsTheTransfer()
    {
        if (!Enabled) return;
        using var c = Connect();
        int unlinks = Fixture.Server!.UnlinkCount;

        // No request pending: the device NAKs until the timeout.
        var watch = Stopwatch.StartNew();
        Assert.Throws<UsbTimeoutException>(() => c.Handle.BulkTransfer(c.BulkIn, new byte[512], 200));
        Assert.InRange(watch.ElapsedMilliseconds, 150, 3000);
        Assert.True(Fixture.Server.UnlinkCount > unlinks, "The kernel did not cancel the bulk-IN URB.");
        Assert.Equal(Fixture.Instrument.Identity, await QueryAsync(c, "*IDN?"));
    }

    [Fact]
    public async Task ZeroTimeoutDoesNotWait()
    {
        if (!Enabled) return;
        using var c = Connect();

        // No request pending, so nothing to read: a transfer with timeout 0 fails at once instead of waiting.
        var watch = Stopwatch.StartNew();
        Assert.Throws<UsbTimeoutException>(() => c.Handle.BulkTransfer(c.BulkIn, new byte[512], 0));
        await Assert.ThrowsAsync<UsbTimeoutException>(async () => await c.Handle.BulkTransferAsync(c.BulkIn, new byte[512], 0));
        Assert.True(watch.ElapsedMilliseconds < 1000, $"Transfers with timeout 0 took {watch.ElapsedMilliseconds} ms.");
        Assert.Equal(Fixture.Instrument.Identity, await QueryAsync(c, "*IDN?"));
    }

    [Fact]
    public async Task DisposeEndsSynchronousTransfers()
    {
        if (!Enabled) return;
        var c = Connect();
        try
        {
            await DrainInterruptsAsync(c);
            var bulk = Task.Factory.StartNew(() => c.Handle.BulkTransfer(c.BulkIn, new byte[512], NoLimit), TaskCreationOptions.LongRunning);
            var interrupt = Task.Factory.StartNew(() => c.Handle.InterruptTransfer(c.InterruptIn, new byte[8], NoLimit), TaskCreationOptions.LongRunning);
            await Task.Delay(200);
            Assert.False(bulk.IsCompleted || interrupt.IsCompleted);

            c.Handle.Dispose();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => bulk.WaitAsync(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => interrupt.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Throws<ObjectDisposedException>(() => c.Handle.ClearHalt(c.BulkOut));
            Assert.Throws<ObjectDisposedException>(() => c.Handle.BulkTransfer(c.BulkIn, new byte[512], 100));
        }
        finally
        {
            c.Dispose();
        }

        using var again = Connect();
        Assert.Equal(Fixture.Instrument.Identity, await QueryAsync(again, "*IDN?"));
    }

    [Fact]
    public async Task ASecondOpenOfTheInterfaceIsRefused()
    {
        if (!Enabled) return;
        using var c = Connect();
        var others = new List<IUsbBackend> { CreateBackend(), new UsbFsBackend() };
        if (LibUsbBackend.IsAvailable) others.Add(new LibUsbBackend());
        foreach (var other in others)
        {
            using (other)
            {
                var device = Assert.Single(other.EnumerateDevices(), d => d.SerialNumber == UsbIpFixture.Serial);
                var error = Assert.Throws<UsbException>(() => other.Open(device, 0));
                Assert.Equal(UsbError.Busy, error.Error);
            }
        }
        // Not taken over: the first handle still works.
        Assert.Equal(Fixture.Instrument.Identity, await QueryAsync(c, "*IDN?"));
    }

    [Fact]
    public async Task ClearsHaltsAndChecksControlLengths()
    {
        if (!Enabled) return;
        using var c = Connect();
        c.Handle.ClearHalt(c.BulkOut);
        c.Handle.ClearHalt(c.BulkIn);
        Assert.Equal(Fixture.Instrument.Identity, await QueryAsync(c, "*IDN?"));

        var tooLong = new byte[65536];
        Assert.Throws<ArgumentOutOfRangeException>(() => c.Handle.ControlTransfer(0x80, 6, 0x0100, 0, tooLong, Timeout));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await c.Handle.ControlTransferAsync(0x80, 6, 0x0100, 0, tooLong, Timeout));

        // Synchronous control transfers in both directions.
        var descriptor = new byte[18];
        Assert.Equal(18, c.Handle.ControlTransfer(0x80, 6, 0x0100, 0, descriptor, Timeout));
        Assert.Equal(0x1234, UsbDescriptorParser.ParseDevice(descriptor).VendorId);
        Assert.Equal(UsbIpFixture.Serial, UsbDescriptorParser.ReadString(c.Handle, 3));
        Assert.Equal(0, c.Handle.ControlTransfer(0x02, 1, 0, c.BulkOut, Span<byte>.Empty, Timeout));
    }
}

/// <summary>Asynchronous transfers through usbfs (USB request blocks reaped on the shared poll thread).</summary>
[Collection("usbip")]
public sealed class UsbBackendAsyncUsbFsTests(UsbIpFixture fixture) : UsbBackendAsyncTests(fixture)
{
    protected override bool Enabled => Fixture.Enabled;

    protected override IUsbBackend CreateBackend() => new UsbFsBackend();
}

/// <summary>Asynchronous transfers through libusb's asynchronous API and its event thread.</summary>
[Collection("usbip")]
public sealed class UsbBackendAsyncLibUsbTests(UsbIpFixture fixture) : UsbBackendAsyncTests(fixture)
{
    protected override bool Enabled => Fixture.Enabled && LibUsbBackend.IsAvailable;

    protected override IUsbBackend CreateBackend() => new LibUsbBackend();

    [Fact]
    public async Task DisposingTheBackendEndsPendingTransfers()
    {
        if (!Enabled) return;
        var c = Connect();
        try
        {
            var pending = c.Handle.BulkTransferAsync(c.BulkIn, new byte[512], NoLimit).AsTask();
            await Task.Delay(100);
            c.Backend.Dispose(); // closes the handle, stops the event thread, releases libusb
            await Assert.ThrowsAsync<ObjectDisposedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            c.Dispose();
        }
        using var again = Connect();
        Assert.Equal(Fixture.Instrument.Identity, await QueryAsync(again, "*IDN?"));
    }
}

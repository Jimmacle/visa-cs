using System.Diagnostics;
using System.Text;
using VisaCs.Simulation;
using VisaCs.Usb;

namespace VisaCs.Tests;

/// <summary>The USBTMC client used on its own, without a VISA session, against simulated devices.</summary>
public sealed class UsbTmcDeviceTests
{
    private static (UsbTmcDevice Tmc, SimulatedUsbTmcDevice Device, SimulatedInstrument Instrument) Open(bool usb488 = true,
        bool interruptEndpoint = true)
    {
        var instrument = new SimulatedInstrument();
        var device = new SimulatedUsbTmcDevice(instrument, usb488: usb488, interruptEndpoint: interruptEndpoint);
        var backend = new SimulatedUsbBackend();
        backend.Devices.Add(device);
        return (UsbTmcDevice.Open(backend, backend.EnumerateDevices()[0]), device, instrument);
    }

    /// <summary>Opens a simulated device with a gate the test can watch, to know when another caller holds the device.</summary>
    private static (UsbTmcDevice Tmc, SemaphoreSlim Gate, SimulatedInstrument Instrument) OpenWithGate()
    {
        var instrument = new SimulatedInstrument();
        var backend = new SimulatedUsbBackend();
        backend.Devices.Add(new SimulatedUsbTmcDevice(instrument));
        var gate = new SemaphoreSlim(1, 1);
        var tmc = new UsbTmcDevice(backend.Open(backend.EnumerateDevices()[0], 0), gate: gate);
        tmc.ReadCapabilities(2000);
        return (tmc, gate, instrument);
    }

    private static string Text(byte[] data) => Encoding.ASCII.GetString(data);

    /// <summary>Polls until <paramref name="condition"/> holds, failing after a deadline generous enough for a loaded machine.</summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Timed out waiting for {what}.");
            await Task.Delay(10);
        }
    }

    private static async Task<bool> MessageAvailableAsync(UsbTmcDevice tmc) => (await tmc.ReadStatusByteAsync(2000) & 0x10) != 0;

    [Fact]
    public async Task QueriesAndReadsCapabilities()
    {
        var (tmc, _, instrument) = Open();
        using var disposeTmc = tmc;
        Assert.True(tmc.IsUsb488);
        Assert.True(tmc.Capabilities.SupportsTermChar);
        Assert.True(tmc.Capabilities.IsUsb4882);
        Assert.Equal(instrument.Identity + "\n", Text(tmc.Query("*IDN?\n"u8, 2000)));
        Assert.Equal(instrument.Identity + "\n", Text(await tmc.QueryAsync("*IDN?\n"u8.ToArray(), 2000)));
    }

    [Fact]
    public async Task ReadsAMessageInTransfersUntilEnd()
    {
        var (tmc, _, instrument) = Open();
        using var disposeTmc = tmc;
        tmc.Write("*IDN?"u8, end: false, 2000);
        tmc.Write("\n"u8, end: true, 2000);
        var message = new List<byte>();
        var buffer = new byte[8];
        UsbTmcReadResult result;
        do
        {
            result = tmc.Read(buffer, 2000);
            message.AddRange(buffer.AsSpan(0, result.Count).ToArray());
        } while (!result.End);
        Assert.Equal(instrument.Identity + "\n", Text([.. message]));

        // The device ends a transfer at the termination character, without EOM.
        await tmc.WriteAsync("*IDN?\n"u8.ToArray(), end: true, 2000);
        var large = new byte[256];
        result = await tmc.ReadAsync(large, 2000, termChar: (byte)',');
        Assert.False(result.End);
        Assert.Equal(instrument.Identity[..(instrument.Identity.IndexOf(',') + 1)], Encoding.ASCII.GetString(large, 0, result.Count));
        Assert.Equal(instrument.Identity[(instrument.Identity.IndexOf(',') + 1)..] + "\n", Text(await tmc.ReadMessageAsync(2000)));
    }

    [Fact]
    public async Task TimeoutAndCancellationAbortTheTransfer()
    {
        var (tmc, device, instrument) = Open();
        using var disposeTmc = tmc;
        Assert.Throws<UsbTimeoutException>(() => tmc.Read(new byte[64], 200));
        Assert.Equal(1, device.AbortCount);

        using var cts = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await tmc.ReadAsync(new byte[64], Timeout.Infinite, null, cts.Token));
        Assert.Equal(2, device.AbortCount);

        Assert.Equal(instrument.Identity + "\n", Text(tmc.Query("*IDN?\n"u8, 2000)));
    }

    [Fact]
    public async Task ClearDiscardsTheResponse()
    {
        var (tmc, device, instrument) = Open();
        using var disposeTmc = tmc;
        tmc.Write("*IDN?\n"u8, end: true, 2000);
        tmc.Clear(2000);
        await tmc.ClearAsync(2000);
        Assert.Equal(2, device.ClearCount);
        Assert.Throws<UsbTimeoutException>(() => tmc.ReadMessage(300));
        Assert.Equal(instrument.Identity + "\n", Text(tmc.Query("*IDN?\n"u8, 2000)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadsTheStatusByte(bool interruptEndpoint)
    {
        var (tmc, _, _) = Open(interruptEndpoint: interruptEndpoint);
        using var disposeTmc = tmc;
        Assert.Equal(0, tmc.ReadStatusByte(2000) & 0x10);
        tmc.Write("*IDN?\n"u8, end: true, 2000);
        // The simulated instrument answers in the background; then the status byte shows the message available.
        await WaitUntilAsync(() => MessageAvailableAsync(tmc), "the message available bit");
    }

    [Fact]
    public async Task DeliversServiceRequestsAndKeepsStatusResponses()
    {
        var (tmc, _, instrument) = Open();
        using var disposeTmc = tmc;
        var received = new List<UsbTmcInterrupt>();
        var srq = new TaskCompletionSource<UsbTmcInterrupt>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<UsbTmcInterrupt> handler = p =>
        {
            lock (received) received.Add(p);
            if (p.IsServiceRequest) srq.TrySetResult(p);
        };
        tmc.AddInterruptHandler(handler);

        // With the read loop running, the status byte response still reaches the request (matched by bTag).
        tmc.ReadStatusByte(2000);
        instrument.RequestService();
        var e = await srq.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(0, e.StatusByte & 0x40);
        lock (received) Assert.Single(received);

        await tmc.RemoveInterruptHandlerAsync(handler);
    }

    [Fact]
    public void TriggersAndControlsRemoteLocal()
    {
        var (tmc, device, instrument) = Open();
        using var disposeTmc = tmc;
        tmc.Trigger(2000);
        Assert.Equal(1, instrument.TriggerCount);
        tmc.RenControl(true, 2000);
        Assert.True(device.IsRemote);
        tmc.LocalLockout(2000);
        Assert.True(device.LocalLockout);
        tmc.GoToLocal(2000);
        Assert.False(device.IsRemote);
        tmc.IndicatorPulse(2000);
    }

    [Fact]
    public void PlainUsbTmcHasNoUsb488Operations()
    {
        var (tmc, _, instrument) = Open(usb488: false);
        using var disposeTmc = tmc;
        Assert.False(tmc.IsUsb488);
        Assert.Equal(UsbError.NotSupported, Assert.Throws<UsbException>(() => tmc.ReadStatusByte(1000)).Error);
        Assert.Equal(UsbError.NotSupported, Assert.Throws<UsbException>(() => tmc.Trigger(1000)).Error);
        Assert.Equal(instrument.Identity + "\n", Text(tmc.Query("*IDN?\n"u8, 2000)));
    }

    [Fact]
    public async Task VendorSpecificMessagesLoopBack()
    {
        var (tmc, device, _) = Open();
        using var disposeTmc = tmc;
        tmc.WriteVendorSpecific([1, 2, 3], 2000);
        await tmc.WriteVendorSpecificAsync(new byte[] { 4, 5 }, 2000);
        Assert.Equal(2, device.VendorMessageCount);
        var buffer = new byte[16];
        var result = tmc.ReadVendorSpecific(buffer, 2000);
        Assert.True(result.End); // shorter than asked for
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, buffer[..result.Count]);
    }

    [Fact]
    public async Task ConcurrentCallersNeverRepeatATag()
    {
        var (tmc, device, instrument) = Open();
        using var disposeTmc = tmc;
        var queries = Enumerable.Range(0, 8).Select(i => i % 2 == 0
            ? tmc.QueryAsync("*IDN?\n"u8.ToArray(), 5000).AsTask()
            : Task.Run(() => tmc.Query("*IDN?\n"u8, 5000)));
        foreach (var response in await Task.WhenAll(queries)) Assert.Equal(instrument.Identity + "\n", Text(response));
        Assert.Equal(0, device.RepeatedTagCount);
    }

    [Fact]
    public async Task WaitingForAnotherCallerEndsAtTheTimeout()
    {
        var (tmc, gate, _) = OpenWithGate();
        using var disposeTmc = tmc;
        // The other caller reads until it is cancelled, long after the bound below.
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var reader = tmc.ReadAsync(new byte[16], Timeout.Infinite, null, stop.Token).AsTask();
        await WaitUntilAsync(() => Task.FromResult(gate.CurrentCount == 0), "the other caller to hold the device");
        var watch = Stopwatch.StartNew();
        Assert.Throws<UsbTimeoutException>(() => tmc.Query("*IDN?\n"u8, 200));
        Assert.True(watch.ElapsedMilliseconds < 5000, $"Waited {watch.ElapsedMilliseconds} ms for the other caller.");
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader);
    }

    [Fact]
    public async Task ZeroTimeoutDoesNotWait()
    {
        var (tmc, gate, instrument) = OpenWithGate();
        using var disposeTmc = tmc;

        // What can complete at once does: the simulated device takes bulk-OUT at once, and answers a read at once
        // when the response is ready.
        tmc.Write("*IDN?\n"u8, end: true, 0);
        await WaitUntilAsync(() => MessageAvailableAsync(tmc), "the response"); // the instrument answers in the background
        var buffer = new byte[256];
        var result = tmc.Read(buffer, 0);
        Assert.True(result.End);
        Assert.Equal(instrument.Identity + "\n", Encoding.ASCII.GetString(buffer, 0, result.Count));

        // Another caller holds the device: fails at once instead of waiting for it (until it is cancelled, after 30 s).
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            var reader = tmc.ReadAsync(new byte[16], Timeout.Infinite, null, stop.Token).AsTask();
            await WaitUntilAsync(() => Task.FromResult(gate.CurrentCount == 0), "the other caller to hold the device");
            var watch = Stopwatch.StartNew();
            Assert.Throws<UsbTimeoutException>(() => tmc.Query("*IDN?\n"u8, 0));
            Assert.True(watch.ElapsedMilliseconds < 5000, $"Waited {watch.ElapsedMilliseconds} ms for the other caller.");
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader);
        }

        // The response is not ready (the instrument answers after 20 s): a read fails without waiting for it. A read
        // that times out still runs the USBTMC abort sequence, which takes some time, more on a loaded machine.
        tmc.Write("DELAY? 20000\n"u8, end: true, 0);
        var read = Stopwatch.StartNew();
        Assert.Throws<UsbTimeoutException>(() => tmc.Read(new byte[64], 0));
        Assert.True(read.ElapsedMilliseconds < 5000, $"A read with timeout 0 took {read.ElapsedMilliseconds} ms.");
        read.Restart();
        await Assert.ThrowsAsync<UsbTimeoutException>(async () => await tmc.ReadAsync(new byte[64], 0));
        Assert.True(read.ElapsedMilliseconds < 5000, $"A read with timeout 0 took {read.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task ZeroTimeoutDoesNotWaitOnTheSimulatedHandle()
    {
        var backend = new SimulatedUsbBackend();
        backend.Devices.Add(new SimulatedUsbTmcDevice(new SimulatedInstrument()));
        using var handle = backend.Open(backend.EnumerateDevices()[0], 0);
        byte bulkIn = handle.Interface.FindEndpoint(UsbTransferType.Bulk, input: true)!.Address;
        byte interruptIn = handle.Interface.FindEndpoint(UsbTransferType.Interrupt, input: true)!.Address;
        // Nothing will arrive, so waiting would never end; the bound is generous for a loaded machine.
        var watch = Stopwatch.StartNew();
        Assert.Throws<UsbTimeoutException>(() => handle.BulkTransfer(bulkIn, new byte[512], 0));
        await Assert.ThrowsAsync<UsbTimeoutException>(async () => await handle.InterruptTransferAsync(interruptIn, new byte[8], 0));
        Assert.True(watch.ElapsedMilliseconds < 5000, $"Transfers with timeout 0 took {watch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task SharesAGateAndTheInterruptEndpointWithOtherCode()
    {
        var backend = new SimulatedUsbBackend();
        var instrument = new SimulatedInstrument();
        backend.Devices.Add(new SimulatedUsbTmcDevice(instrument));
        using var handle = backend.Open(backend.EnumerateDevices()[0], 0);
        var gate = new SemaphoreSlim(1, 1);
        await using var tmc = new UsbTmcDevice(handle, leaveOpen: true, gate);
        tmc.ReadCapabilities(2000);

        // While other code holds the gate, the client waits for it until its timeout.
        gate.Wait();
        Assert.Throws<UsbTimeoutException>(() => tmc.Query("*IDN?\n"u8, 200));
        gate.Release();
        Assert.Equal(instrument.Identity + "\n", Text(tmc.Query("*IDN?\n"u8, 2000)));

        // Other code reads the interrupt endpoint and hands the client its status byte responses.
        tmc.ExternalInterruptReader = true;
        Assert.Throws<InvalidOperationException>(() => tmc.AddInterruptHandler(_ => { }));
        using var stop = new CancellationTokenSource();
        var others = new List<byte[]>();
        byte interruptIn = handle.Interface.FindEndpoint(UsbTransferType.Interrupt, input: true)!.Address;
        var loop = Task.Run(async () =>
        {
            var packet = new byte[8];
            while (true)
            {
                int n;
                try { n = await handle.InterruptTransferAsync(interruptIn, packet, Timeout.Infinite, stop.Token); }
                catch (OperationCanceledException) { return; }
                if (!tmc.ProcessInterrupt(packet.AsSpan(0, n))) lock (others) others.Add(packet[..n]);
            }
        });
        tmc.ReadStatusByte(2000);
        await tmc.ReadStatusByteAsync(2000);
        Assert.False(tmc.ProcessInterrupt([0x81, 0x40])); // a service request is no status byte response
        stop.Cancel();
        await loop;
        lock (others) Assert.Empty(others);
    }

    [Fact]
    public async Task CapabilitiesAreReadWhole()
    {
        var backend = new SimulatedUsbBackend();
        backend.Devices.Add(new SimulatedUsbTmcDevice(new SimulatedInstrument()));
        using var handle = backend.Open(backend.EnumerateDevices()[0], 0);
        using var tmc = new UsbTmcDevice(handle, leaveOpen: true);
        Assert.Equal(default, tmc.Capabilities);
        var seen = new HashSet<UsbTmcCapabilities>();
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested) seen.Add(tmc.Capabilities);
        });
        var expected = tmc.ReadCapabilities(2000);
        for (int i = 0; i < 50; i++) await tmc.ReadCapabilitiesAsync(2000);
        stop.Cancel();
        await reader;
        // Only the value before the first read and the complete value read, never a mix of the two.
        Assert.Subset(new HashSet<UsbTmcCapabilities> { default, expected }, seen);
        Assert.Equal(expected, tmc.Capabilities);
    }
}

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

    private static string Text(byte[] data) => Encoding.ASCII.GetString(data);

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
        Assert.Throws<VisaTimeoutException>(() => tmc.Read(new byte[64], 200));
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
        Assert.Throws<VisaTimeoutException>(() => tmc.ReadMessage(300));
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
        await Task.Delay(100); // the simulated instrument answers in the background
        Assert.NotEqual(0, await tmc.ReadStatusByteAsync(2000) & 0x10); // message available
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
        Assert.Equal(VisaStatus.ErrorOperationNotSupported, Assert.Throws<VisaException>(() => tmc.ReadStatusByte(1000)).Status);
        Assert.Equal(VisaStatus.ErrorOperationNotSupported, Assert.Throws<VisaException>(() => tmc.Trigger(1000)).Status);
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
        var (tmc, _, _) = Open();
        using var disposeTmc = tmc;
        var reader = Task.Run(() => Assert.Throws<VisaTimeoutException>(() => tmc.Read(new byte[16], 1500)));
        await Task.Delay(200);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<VisaTimeoutException>(() => tmc.Query("*IDN?\n"u8, 200));
        Assert.True(watch.ElapsedMilliseconds < 1200);
        await reader;
    }
}

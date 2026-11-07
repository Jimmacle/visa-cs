using System.Text;

namespace VisaCs.Tests;

/// <summary>The asynchronous API, run against every message-based protocol (each derived test class).</summary>
public abstract partial class MessageBasedProtocolTests
{
    private async Task<MessageBasedSession> OpenAsync(ResourceManager rm)
    {
        var session = await rm.OpenAsync(Resource);
        session.TimeoutMilliseconds = 5000;
        return session;
    }

    [Fact]
    public async Task ReadsAndWritesAsynchronously()
    {
        if (!Enabled) return;
        await using var rm = CreateResourceManager();
        await using var session = await OpenAsync(rm);

        Assert.Equal(Instrument.Identity, await session.QueryAsync("*IDN?"));
        await session.WriteLineAsync("ECHO? hello");
        Assert.Equal("hello", await session.ReadLineAsync());

        await session.WriteLineAsync("ECHO? 0123456789");
        var buffer = new byte[4];
        Assert.Equal(new ReadResult(4, ReadStatus.MaximumCountReached), await session.ReadAsync(buffer));
        Assert.Equal("0123", Encoding.ASCII.GetString(buffer));
        Assert.Equal("456789\n", Encoding.ASCII.GetString(await session.ReadToEndAsync()));

        await session.WriteAsync("ECHO? bytes\n"u8.ToArray());
        Assert.Equal("bytes", (await session.ReadStringAsync()).TrimEnd());
        Assert.Equal(1.5, await session.QueryAsync<double>("ECHO? 1.5"));
        Assert.Equal(42, await session.QueryAsync<long>("ECHO? 42"));
        Assert.Equal(new[] { 0.0, 0.5, 1.0 }, await session.QueryAsciiValuesAsync("DATA:ASCII? 3"));
    }

    [Fact]
    public async Task TransfersBinaryBlocksAsynchronously()
    {
        if (!Enabled) return;
        await using var rm = CreateResourceManager();
        await using var session = await OpenAsync(rm);

        var floats = await session.QueryBinaryValuesAsync<float>("DATA:FLOAT? 10");
        Assert.Equal(4.5f, floats[9]);

        var data = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        await session.WriteBinaryBlockAsync("DATA:STORE ", data);
        Assert.Equal(data, await session.QueryBinaryBlockAsync("DATA:STORE?"));

        int[] values = [1, -2, 300_000];
        session.BinaryByteOrder = ByteOrder.BigEndian;
        await session.WriteBinaryValuesAsync<int>("DATA:STORE ", values);
        Assert.Equal(values, await session.QueryBinaryValuesAsync<int>("DATA:STORE?"));
    }

    [Fact]
    public async Task FormatsAndScansAsynchronously()
    {
        if (!Enabled) return;
        await using var rm = CreateResourceManager();
        await using var session = await OpenAsync(rm);

        await session.PrintfAsync("ECHO? %d,%.2f,%s\n", 5, 2.5, "text");
        Assert.Equal(new object?[] { 5, 2.5, "text" }, await session.ScanfAsync("%d,%f,%s"));
        Assert.Equal(1.2345, await session.QueryfAsync<double>("MEAS:VOLT?\n", "%f"), 6);
        Assert.Equal(new[] { 0.0, 0.5, 1.0, 1.5 }, await session.QueryfAsync<double[]>("DATA:ASCII? %d\n", "%,#f", 4));
    }

    [Fact]
    public async Task TimesOutAndRecoversAsynchronously()
    {
        if (!Enabled) return;
        await using var rm = CreateResourceManager();
        await using var session = await OpenAsync(rm);
        session.TimeoutMilliseconds = 300;
        var ex = await Assert.ThrowsAsync<VisaTimeoutException>(() => session.QueryAsync("DELAY? 800"));
        Assert.Equal(VisaStatus.ErrorTimeout, ex.Status);
        // Raw sockets cannot clear the device: wait for the late response, which Clear then discards.
        if (!HasEndIndicator) await Task.Delay(900);
        session.TimeoutMilliseconds = 5000;
        await session.ClearAsync();
        Assert.Equal(Instrument.Identity, await session.QueryAsync("*IDN?"));
    }

    [Fact]
    public async Task RunsDeviceOperationsAsynchronously()
    {
        if (!Enabled) return;
        await using var rm = CreateResourceManager();
        await using var session = await OpenAsync(rm);

        await session.WriteLineAsync("*CLS;*SRE 1;SRQ");
        Assert.Equal("1", await session.QueryAsync("*OPC?"));
        Assert.Equal(0x41, await session.ReadStatusByteAsync() & 0x41);
        await session.WriteLineAsync("*CLS;*SRE 0");
        Assert.Equal("1", await session.QueryAsync("*OPC?"));

        int triggers = Instrument.TriggerCount;
        await session.AssertTriggerAsync();
        Assert.Equal("1", await session.QueryAsync("*OPC?"));
        Assert.True(SpinWait.SpinUntil(() => Instrument.TriggerCount > triggers, 2000));

        await session.WriteLineAsync("ECHO? never read");
        if (!HasEndIndicator) await Task.Delay(300);
        await session.ClearAsync();
        await session.FlushAsync(BufferTypes.ReadBufferDiscard | BufferTypes.WriteBuffer);
        Assert.Equal(Instrument.Identity, await session.QueryAsync("*IDN?"));
    }

    [Fact]
    public async Task KeepsConcurrentAsyncQueriesApart()
    {
        if (!Enabled) return;
        await using var rm = CreateResourceManager();
        await using var session = await OpenAsync(rm);
        // Operations on one session run one at a time, so each query gets its own response.
        var answers = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => session.QueryAsync($"ECHO? value {i}")));
        for (int i = 0; i < answers.Length; i++) Assert.Equal($"value {i}", answers[i]);
    }

    [Fact]
    public async Task CancelsAQueuedOperation()
    {
        if (!Enabled) return;
        await using var rm = CreateResourceManager();
        await using var session = await OpenAsync(rm);
        // The second query waits for the first; cancelling it while it waits must not disturb the first.
        var slow = session.QueryAsync("DELAY? 500");
        using var cts = new CancellationTokenSource();
        var queued = session.QueryAsync("*IDN?", cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal("DONE", await slow);
        Assert.Equal(Instrument.Identity, await session.QueryAsync("*IDN?"));
    }

    [Fact]
    public async Task LocksAsynchronously()
    {
        if (!Enabled) return;
        await using var rm = CreateResourceManager();
        await using var a = await OpenAsync(rm);
        await using var b = await OpenAsync(rm);
        await a.LockExclusiveAsync(1000);
        var ex = await Assert.ThrowsAsync<VisaException>(() => b.QueryAsync("*IDN?"));
        Assert.Equal(VisaStatus.ErrorResourceLocked, ex.Status);
        await Assert.ThrowsAsync<VisaTimeoutException>(() => b.LockExclusiveAsync(100));

        // A waiting lock request is granted when the holder unlocks, and can be cancelled while it waits.
        var waiting = b.LockExclusiveAsync(5000);
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);
        await a.UnlockAsync();
        await waiting;
        Assert.Equal(Instrument.Identity, await b.QueryAsync("*IDN?"));

        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.LockExclusiveAsync(-1, cts.Token));
        await b.UnlockAsync();
        Assert.Equal(ResourceLockState.NoLock, a.ResourceLockState);
    }

    [Fact]
    public async Task WaitsForServiceRequestsAsynchronously()
    {
        if (!Enabled || !SupportsServiceRequest) return;
        await using var rm = CreateResourceManager();
        await using var session = await OpenAsync(rm);
        await session.WriteLineAsync("*CLS;*SRE 0");
        Assert.Equal("1", await session.QueryAsync("*OPC?"));
        await session.EnableEventAsync(EventType.ServiceRequest, EventMechanism.Queue);
        try
        {
            // Nothing pending: a cancelled wait ends with OperationCanceledException.
            using (var cts = new CancellationTokenSource(100))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.WaitOnEventAsync(EventType.ServiceRequest, -1, cts.Token));

            var wait = session.WaitOnEventAsync(EventType.ServiceRequest, 5000);
            await session.WriteLineAsync("*SRE 1;SRQ");
            TriggerServiceRequestDelivery(session);
            var e = await wait;
            Assert.Equal(EventType.ServiceRequest, e.EventType);
        }
        finally
        {
            await session.DisableEventAsync(EventType.ServiceRequest);
            await session.WriteLineAsync("*CLS;*SRE 0");
            Assert.Equal("1", await session.QueryAsync("*OPC?"));
        }
    }

    [Fact]
    public async Task ClosesAsynchronously()
    {
        if (!Enabled) return;
        await using var rm = CreateResourceManager();
        var session = await OpenAsync(rm);
        Assert.Equal(Instrument.Identity, await session.QueryAsync("*IDN?"));
        await session.DisposeAsync();
        Assert.True(session.IsDisposed);
        Assert.DoesNotContain(session, rm.Sessions);
        var ex = await Assert.ThrowsAsync<VisaException>(() => session.QueryAsync("*IDN?"));
        Assert.Equal(VisaStatus.ErrorInvalidObject, ex.Status);
    }
}

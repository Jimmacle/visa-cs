namespace VisaCs.Tests;

// The asynchronous tests live in this part: 'await' is not allowed in the unsafe part of the class.
public sealed partial class SerialPtyTests
{
    [Fact]
    public async Task TalksAsynchronouslyOverTermios()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var rm = new ResourceManager();
        await using var session = await rm.OpenAsync<SerialSession>($"ASRL{_slave}::INSTR");
        session.TimeoutMilliseconds = 3000;
        session.BaudRate = 115200;

        Assert.Equal(_instrument.Identity, await session.QueryAsync("*IDN?"));
        Assert.Equal(1.2345, await session.QueryAsync<double>("MEAS:VOLT?"), 6);
        Assert.Equal(0, await session.ReadStatusByteAsync() & 0x41); // emulated with *STB?
        var block = await session.QueryBinaryBlockAsync("DATA:BIN? 300");
        Assert.Equal(300, block.Length);
        var answers = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => session.QueryAsync($"ECHO? {i}")));
        Assert.Equal(Enumerable.Range(0, 20).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), answers);

        session.TimeoutMilliseconds = 200;
        await Assert.ThrowsAsync<VisaTimeoutException>(() => session.ReadLineAsync());
        session.TimeoutMilliseconds = -1;
        using (var cts = new CancellationTokenSource(200))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadLineAsync(cts.Token));
        session.TimeoutMilliseconds = 3000;
        Assert.Equal(_instrument.Identity, await session.QueryAsync("*IDN?"));
        await session.FlushAsync(BufferTypes.InBufferDiscard | BufferTypes.OutBuffer);
    }
}

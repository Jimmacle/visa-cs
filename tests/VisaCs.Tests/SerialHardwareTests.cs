using System.Diagnostics;
using VisaCs.Serial;

namespace VisaCs.Tests;

/// <summary>
/// Smoke test against a real serial port with nothing (or anything) attached. Runs only when
/// VISACS_TEST_SERIAL names a resource, e.g. <c>ASRL1::INSTR</c>. It writes a few harmless bytes.
/// </summary>
public sealed class SerialHardwareTests
{
    private static string? Port => Environment.GetEnvironmentVariable("VISACS_TEST_SERIAL");

    [Fact]
    public async Task ConfiguresTimesOutAndCancels()
    {
        if (Port is not { Length: > 0 } port) return;
        using var rm = new ResourceManager();
        using var session = rm.Open<SerialSession>(port);

        session.BaudRate = 19200;
        session.DataBits = 7;
        session.Parity = SerialParity.Even;
        session.StopBits = SerialStopBitsMode.Two;
        session.FlowControl = SerialFlowControlModes.XOnXOff;
        session.BaudRate = 9600;
        session.DataBits = 8;
        session.Parity = SerialParity.None;
        session.StopBits = SerialStopBitsMode.One;
        session.FlowControl = SerialFlowControlModes.None;
        Assert.Equal(9600, session.GetAttribute<int>(VisaAttribute.SerialBaudRate));

        session.Flush(BufferTypes.InBufferDiscard);
        session.TimeoutMilliseconds = 300;
        var watch = Stopwatch.StartNew();
        Assert.Throws<VisaTimeoutException>(() => session.ReadBytes(16));
        Assert.InRange(watch.ElapsedMilliseconds, 250, 2000);

        session.Write("*IDN?\n");
        session.Flush(BufferTypes.OutBuffer);

        session.DtrState = LineState.Asserted;
        Assert.Equal(LineState.Asserted, session.DtrState);
        session.RtsState = LineState.Unasserted;
        Assert.Equal(LineState.Unasserted, session.RtsState);
        _ = session.CtsState;
        _ = session.DsrState;
        _ = session.DcdState;
        _ = session.RiState;
        session.SendBreak(10);
        Assert.Equal(LineState.Unasserted, session.BreakState);

        session.TimeoutMilliseconds = 10_000;
        using var cts = new CancellationTokenSource(250);
        watch.Restart();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadLineAsync(cts.Token));
        Assert.True(watch.ElapsedMilliseconds < 3000, $"Cancellation took {watch.ElapsedMilliseconds} ms.");

        // The port is still usable after a cancelled read.
        session.TimeoutMilliseconds = 100;
        Assert.Throws<VisaTimeoutException>(() => session.ReadBytes(1));
    }

    [Fact]
    public async Task WorksAsynchronously()
    {
        if (Port is not { Length: > 0 } port) return;
        await using var rm = new ResourceManager();
        await using var session = await rm.OpenAsync<SerialSession>(port);

        await session.FlushAsync(BufferTypes.InBufferDiscard);
        session.TimeoutMilliseconds = 300;
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<VisaTimeoutException>(() => session.ReadBytesAsync(16));
        Assert.InRange(watch.ElapsedMilliseconds, 250, 2000);

        await session.WriteLineAsync("*IDN?");
        await session.FlushAsync(BufferTypes.OutBuffer);
        await session.SendBreakAsync(10);
        Assert.Equal(LineState.Unasserted, session.BreakState);

        session.TimeoutMilliseconds = -1;
        using var cts = new CancellationTokenSource(250);
        watch.Restart();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadLineAsync(cts.Token));
        Assert.True(watch.ElapsedMilliseconds < 1000, $"Cancellation took {watch.ElapsedMilliseconds} ms.");

        session.TimeoutMilliseconds = 100;
        await Assert.ThrowsAsync<VisaTimeoutException>(() => session.ReadBytesAsync(1));
    }
}

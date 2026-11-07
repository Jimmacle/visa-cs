using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using VisaCs.Serial;
using Xunit.Abstractions;

namespace VisaCs.Tests;

/// <summary>Runs <see cref="SerialPortAsyncTests"/> alone: they count threads and hold ports exclusively.</summary>
[CollectionDefinition(nameof(SerialPortAsyncTests), DisableParallelization = true)]
public sealed class SerialPortAsyncCollection
{
}

/// <summary>
/// Asynchronous I/O of <see cref="NativeSerialPort"/>. On Linux the port is the slave side of a pseudo-terminal (a
/// real termios device) whose master side the test drives. On Windows the tests need a UART named by
/// VISACS_TEST_SERIAL_PORT (for example COM1) with nothing attached, so that reads time out and writes complete.
/// </summary>
[Collection(nameof(SerialPortAsyncTests))]
public sealed partial class SerialPortAsyncTests(ITestOutputHelper output) : IDisposable
{
    private readonly List<NativeSerialPort> _ports = new();
    private readonly List<int> _masters = new();

    private static string? Uart =>
        OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("VISACS_TEST_SERIAL_PORT") is { Length: > 0 } name ? name : null;

    private static bool HasPort => OperatingSystem.IsLinux() || Uart is not null;

    /// <summary>A port nobody sends to: a pseudo-terminal on Linux, the UART on Windows.</summary>
    private (NativeSerialPort Port, string Name) OpenQuietPort()
    {
        if (OperatingSystem.IsLinux())
        {
            var pty = OpenPty();
            return (pty.Port, pty.Slave);
        }
        return (OpenPort(Uart!), Uart!);
    }

    private NativeSerialPort OpenPort(string name, int baudRate = 115200)
    {
        var port = NativeSerialPort.Open(name, SerialSettings.Default with { BaudRate = baudRate });
        _ports.Add(port);
        port.DiscardInput();
        return port;
    }

    // ------------------------------------------------------------------ any platform with a port

    [Fact]
    public async Task ReadAsyncTimesOut()
    {
        if (!HasPort) return;
        var (port, _) = OpenQuietPort();
        var buffer = new byte[16];

        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<VisaTimeoutException>(async () => await port.ReadAsync(buffer, 300));
        Assert.InRange(watch.ElapsedMilliseconds, 280, 1500);

        watch.Restart();
        await Assert.ThrowsAsync<VisaTimeoutException>(async () => await port.ReadAsync(buffer, 0));
        Assert.True(watch.ElapsedMilliseconds < 500, $"A read with timeout 0 took {watch.ElapsedMilliseconds} ms.");

        Assert.Equal(0, await port.ReadAsync(Memory<byte>.Empty, -1));
        Assert.Equal(0, port.Read(Span<byte>.Empty, -1));

        // The synchronous path still works alongside the asynchronous one.
        watch.Restart();
        Assert.Throws<VisaTimeoutException>(() => port.Read(buffer, 200));
        Assert.InRange(watch.ElapsedMilliseconds, 180, 1500);
    }

    [Fact]
    public void OpenAppliesDefaultsAndConfigureValidates()
    {
        if (!HasPort) return;
        var (port, name) = OpenQuietPort();
        Assert.Equal(name, port.PortName);
        Assert.Equal(SerialSettings.Default with { BaudRate = 115200 }, port.Settings);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(port.Dtr);
            Assert.True(port.Rts);
        }

        var before = port.Settings;
        Assert.Throws<ArgumentOutOfRangeException>(() => port.Configure(before with { BaudRate = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => port.Configure(before with { DataBits = 9 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => port.Configure(before with { DataBits = 4 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => port.Configure(before with { Parity = (SerialParity)7 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => port.Configure(before with { StopBits = (SerialStopBitsMode)12 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => port.Configure(before with { FlowControl = (SerialFlowControlModes)8 }));
        Assert.Equal(before, port.Settings);

        var other = before with { BaudRate = 19200, DataBits = 7, Parity = SerialParity.Even, StopBits = SerialStopBitsMode.Two };
        port.Configure(other);
        Assert.Equal(other, port.Settings);
    }

    [Fact]
    public async Task CancellationEndsAPendingReadAsync()
    {
        if (!HasPort) return;
        var (port, _) = OpenQuietPort();
        var buffer = new byte[16];

        foreach (int timeout in new[] { -1, 10_000 })
        {
            using var cts = new CancellationTokenSource();
            var read = port.ReadAsync(buffer, timeout, cts.Token).AsTask();
            await Task.Delay(150);
            Assert.False(read.IsCompleted);
            var watch = Stopwatch.StartNew();
            cts.Cancel();
            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            Assert.Equal(cts.Token, ex.CancellationToken);
            Assert.True(watch.ElapsedMilliseconds < 1000, $"Cancellation took {watch.ElapsedMilliseconds} ms.");
        }

        var canceled = port.ReadAsync(buffer, -1, new CancellationToken(true));
        Assert.True(canceled.IsCanceled);

        // The port still works: reads time out normally, nothing is left aborted.
        Assert.Throws<VisaTimeoutException>(() => port.Read(buffer, 100));
        await Assert.ThrowsAsync<VisaTimeoutException>(async () => await port.ReadAsync(buffer, 100));
        await port.WriteAsync("x"u8.ToArray(), 2000);
    }

    [Fact]
    public async Task AbortEndsAPendingReadAsync()
    {
        if (!HasPort) return;
        var (port, _) = OpenQuietPort();
        var buffer = new byte[16];

        var read = port.ReadAsync(buffer, -1).AsTask();
        await Task.Delay(150);
        Assert.False(read.IsCompleted);
        port.Abort();
        var ex = await Assert.ThrowsAsync<VisaException>(() => read).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(VisaStatus.ErrorAbort, ex.Status);

        // The abort ended the pending read and is not held for a later one.
        Assert.Throws<VisaTimeoutException>(() => port.Read(buffer, 100));
        await Assert.ThrowsAsync<VisaTimeoutException>(async () => await port.ReadAsync(buffer, 100));

        // Abort still ends a synchronous read blocked on another thread.
        var blocked = Task.Factory.StartNew(() => port.Read(buffer, -1), TaskCreationOptions.LongRunning);
        await Task.Delay(150);
        port.Abort();
        ex = await Assert.ThrowsAsync<VisaException>(() => blocked).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(VisaStatus.ErrorAbort, ex.Status);

        // An abort while nothing is in progress has no effect on later calls.
        port.Abort();
        var watch = Stopwatch.StartNew();
        Assert.Throws<VisaTimeoutException>(() => port.Read(buffer, 300));
        Assert.True(watch.ElapsedMilliseconds >= 250, $"The read ended after {watch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task AbortEndsBlockedSynchronousReadAndWriteTogether()
    {
        if (!HasPort) return;
        var (port, _) = OpenQuietPort();
        // A write that cannot finish: nobody reads the pseudo-terminal, or the UART sends at 30 characters a second.
        if (Uart is not null) port.Configure(SerialSettings.Default with { BaudRate = 300 });
        var reader = Task.Factory.StartNew(() => port.Read(new byte[16], -1), TaskCreationOptions.LongRunning);
        var writer = Task.Factory.StartNew(() => port.Write(new byte[1 << 20], -1), TaskCreationOptions.LongRunning);
        await Task.Delay(300);
        Assert.False(reader.IsCompleted);
        Assert.False(writer.IsCompleted);

        port.Abort();
        Assert.Equal(VisaStatus.ErrorAbort, (await Assert.ThrowsAsync<VisaException>(() => reader).WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(VisaStatus.ErrorAbort, (await Assert.ThrowsAsync<VisaException>(() => writer).WaitAsync(TimeSpan.FromSeconds(5))).Status);
        port.DiscardOutput();
    }

    [Fact]
    public async Task DisposeEndsPendingTransfers()
    {
        if (!HasPort) return;
        var (port, name) = OpenQuietPort();

        var read = port.ReadAsync(new byte[16], -1).AsTask();
        var blocked = Task.Factory.StartNew(() => port.Read(new byte[16], -1), TaskCreationOptions.LongRunning);
        await Task.Delay(150);
        Assert.False(read.IsCompleted);
        Assert.False(blocked.IsCompleted);

        if (OperatingSystem.IsLinux()) Assert.True(IsOpenInProcess(name));
        var watch = Stopwatch.StartNew();
        port.Dispose();
        Assert.True(watch.ElapsedMilliseconds < 100, $"Dispose took {watch.ElapsedMilliseconds} ms.");
        var ex = await Assert.ThrowsAsync<VisaException>(() => read).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(VisaStatus.ErrorAbort, ex.Status);
        ex = await Assert.ThrowsAsync<VisaException>(() => blocked).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(VisaStatus.ErrorAbort, ex.Status);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"Ending the transfers took {watch.ElapsedMilliseconds} ms.");

        Assert.Throws<ObjectDisposedException>(() => port.Read(new byte[1], 10));
        Assert.Throws<ObjectDisposedException>(() => port.Write(new byte[1], 10));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await port.ReadAsync(new byte[1], 10));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await port.WriteAsync(new byte[1], 10));
        port.Abort();
        port.Dispose();

        // The OS handle was released once the transfers had ended. A pseudo-terminal slave cannot be reopened to show
        // it (TIOCEXCL stays set while the master is open), so look at the process's descriptors there.
        if (OperatingSystem.IsLinux())
        {
            Assert.False(IsOpenInProcess(name));
        }
        else
        {
            var again = OpenPort(name);
            Assert.Throws<VisaTimeoutException>(() => again.Read(new byte[1], 50));
        }
    }

    [Fact]
    public async Task MembersThrowAfterDispose()
    {
        if (!HasPort) return;
        var (port, name) = OpenQuietPort();
        var settings = port.Settings;
        port.Dispose();

        Assert.Throws<ObjectDisposedException>(() => port.Configure(SerialSettings.Default));
        Assert.Throws<ObjectDisposedException>(() => port.BytesAvailable);
        Assert.Throws<ObjectDisposedException>(() => port.DiscardInput());
        Assert.Throws<ObjectDisposedException>(() => port.DiscardOutput());
        Assert.Throws<ObjectDisposedException>(() => port.Drain(100));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await port.DrainAsync(100));
        Assert.Throws<ObjectDisposedException>(() => port.Rts);
        Assert.Throws<ObjectDisposedException>(() => port.Rts = true);
        Assert.Throws<ObjectDisposedException>(() => port.Dtr);
        Assert.Throws<ObjectDisposedException>(() => port.Dtr = true);
        Assert.Throws<ObjectDisposedException>(() => port.Cts);
        Assert.Throws<ObjectDisposedException>(() => port.Dcd);
        Assert.Throws<ObjectDisposedException>(() => port.Break);
        Assert.Throws<ObjectDisposedException>(() => port.Break = true);
        Assert.Throws<ObjectDisposedException>(() => port.SetTransmitAllowed(true));
        port.Abort();
        port.Dispose();
        Assert.Equal(name, port.PortName);
        Assert.Equal(settings, port.Settings);
    }

    [Fact]
    public void GetPortNamesSkipsPlaceholderPorts()
    {
        var names = NativeSerialPort.GetPortNames();
        output.WriteLine(string.Join(", ", names));
        if (!OperatingSystem.IsLinux()) return;
        // The 8250 driver registers ttyS ports whether or not a UART exists; they report type 0 (PORT_UNKNOWN).
        foreach (var name in names)
        {
            string type = Path.Combine("/sys/class/tty", Path.GetFileName(name), "type");
            if (File.Exists(type)) Assert.NotEqual("0", File.ReadAllText(type).Trim());
        }
    }

    private static bool IsOpenInProcess(string path) => DescriptorOf(path) >= 0;

    /// <summary>The descriptor this process has open on <paramref name="path"/>, or -1.</summary>
    private static int DescriptorOf(string path)
    {
        foreach (var fd in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
        {
            try
            {
                if (new FileInfo(fd).LinkTarget == path) return int.Parse(Path.GetFileName(fd), System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (IOException)
            {
            }
        }
        return -1;
    }

    // ------------------------------------------------------------------ Windows UART

    [Fact]
    public async Task Uart_WritesAndDrainsAsynchronously()
    {
        if (Uart is not { } name) return;
        var port = OpenPort(name);
        var data = new byte[512];

        // 512 characters at 115200 baud take about 45 ms.
        var watch = Stopwatch.StartNew();
        await port.WriteAsync(data, 5000);
        await port.DrainAsync(5000);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"Writing took {watch.ElapsedMilliseconds} ms.");

        // The synchronous path on the handle bound to the completion port, mixed with the asynchronous one.
        port.Write(data, 5000);
        port.Drain(5000);
        await port.WriteAsync(data, 5000);
        port.Write(data, 5000);
        await port.DrainAsync(5000);

        // DrainAsync waits for a write in progress: 30 characters at 300 baud take one second.
        port.Configure(SerialSettings.Default with { BaudRate = 300 });
        var write = port.WriteAsync(new byte[30], 10_000).AsTask();
        await Task.Delay(50);
        using (var cts = new CancellationTokenSource(200))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => port.DrainAsync(-1, cts.Token).AsTask());
        Assert.False(write.IsCompleted);
        watch.Restart();
        await port.DrainAsync(-1);
        await write.WaitAsync(TimeSpan.FromSeconds(2));
        output.WriteLine($"DrainAsync waited {watch.ElapsedMilliseconds} ms for the rest of a 1 s write.");
        Assert.InRange(watch.ElapsedMilliseconds, 400, 3000);
    }

    [Fact]
    public async Task Uart_DrainTimesOutAndAborts()
    {
        if (Uart is not { } name) return;
        var port = OpenPort(name, baudRate: 300); // 30 characters per second
        var write = port.WriteAsync(new byte[60], -1).AsTask();
        await Task.Delay(50);

        var watch = Stopwatch.StartNew();
        Assert.Throws<VisaTimeoutException>(() => port.Drain(200));
        Assert.InRange(watch.ElapsedMilliseconds, 180, 1000);
        watch.Restart();
        await Assert.ThrowsAsync<VisaTimeoutException>(async () => await port.DrainAsync(200));
        Assert.InRange(watch.ElapsedMilliseconds, 180, 1000);
        Assert.Throws<VisaTimeoutException>(() => port.Drain(0));

        // Abort ends a drain blocked on another thread (and the write it waits for).
        var drain = Task.Factory.StartNew(() => port.Drain(-1), TaskCreationOptions.LongRunning);
        await Task.Delay(100);
        Assert.False(drain.IsCompleted);
        port.Abort();
        Assert.Equal(VisaStatus.ErrorAbort, (await Assert.ThrowsAsync<VisaException>(() => drain).WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(VisaStatus.ErrorAbort, (await Assert.ThrowsAsync<VisaException>(() => write).WaitAsync(TimeSpan.FromSeconds(5))).Status);
        port.DiscardOutput();
        port.Drain(2000);
    }

    [Fact]
    public async Task Uart_WriteTimeoutAndAbortEndSlowWrites()
    {
        if (Uart is not { } name) return;
        var port = OpenPort(name, baudRate: 300); // 30 characters per second
        var data = new byte[1000];

        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<VisaTimeoutException>(async () => await port.WriteAsync(data, 300));
        output.WriteLine($"Async write timed out after {watch.ElapsedMilliseconds} ms with {ex.BytesTransferred} bytes sent.");
        Assert.InRange(watch.ElapsedMilliseconds, 280, 2000);
        Assert.InRange(ex.BytesTransferred, 0, data.Length - 1);
        port.DiscardOutput();

        watch.Restart();
        ex = Assert.Throws<VisaTimeoutException>(() => port.Write(data, 300));
        output.WriteLine($"Sync write timed out after {watch.ElapsedMilliseconds} ms with {ex.BytesTransferred} bytes sent.");
        Assert.InRange(watch.ElapsedMilliseconds, 280, 2000);
        port.DiscardOutput();

        using (var cts = new CancellationTokenSource(200))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await port.WriteAsync(data, -1, cts.Token));
        port.DiscardOutput();

        var write = port.WriteAsync(data, -1).AsTask();
        await Task.Delay(200);
        port.Abort();
        var abort = await Assert.ThrowsAsync<VisaException>(() => write).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(VisaStatus.ErrorAbort, abort.Status);
        port.DiscardOutput();

        // A synchronous write that has sent part of its data also ends on Abort.
        var blocked = Task.Factory.StartNew(() => port.Write(data, -1), TaskCreationOptions.LongRunning);
        await Task.Delay(300);
        port.Abort();
        abort = await Assert.ThrowsAsync<VisaException>(() => blocked).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(VisaStatus.ErrorAbort, abort.Status);
        port.DiscardOutput();

        // DiscardOutput drops the data of a write in progress, which then ends as aborted.
        blocked = Task.Factory.StartNew(() => port.Write(data, -1), TaskCreationOptions.LongRunning);
        await Task.Delay(200);
        port.DiscardOutput();
        abort = await Assert.ThrowsAsync<VisaException>(() => blocked).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(VisaStatus.ErrorAbort, abort.Status);
    }

    [Fact]
    public async Task Uart_ReadsAndWritesAtTheSameTime()
    {
        if (Uart is not { } name) return;
        var port = OpenPort(name);
        using var cts = new CancellationTokenSource();
        var read = port.ReadAsync(new byte[16], -1, cts.Token).AsTask();
        var blocked = Task.Factory.StartNew(() => port.Read(new byte[16], 2000), TaskCreationOptions.LongRunning);

        await port.WriteAsync(new byte[256], 2000);
        port.Write(new byte[256], 2000);
        await port.DrainAsync(5000);
        Assert.False(read.IsCompleted);

        await Assert.ThrowsAsync<VisaTimeoutException>(() => blocked).WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Uart_SynchronousTransfersMayOverlap()
    {
        if (Uart is not { } name) return;
        var port = OpenPort(name);

        // Each synchronous transfer has its own OVERLAPPED and event, so several at once time out independently.
        var watch = Stopwatch.StartNew();
        var reads = Enumerable.Range(0, 4)
            .Select(i => Task.Factory.StartNew(() => port.Read(new byte[16], 300 + 100 * i), TaskCreationOptions.LongRunning))
            .ToList();
        var writes = Enumerable.Range(0, 4)
            .Select(_ => Task.Factory.StartNew(() => port.Write(new byte[128], 5000), TaskCreationOptions.LongRunning))
            .ToList();
        await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var read in reads)
            await Assert.ThrowsAsync<VisaTimeoutException>(() => read).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.InRange(watch.ElapsedMilliseconds, 550, 3000);
        port.Drain(5000);
    }

    [Fact]
    public async Task Uart_PendingReadsHoldNoThreads()
    {
        if (Uart is not { } name) return;
        var port = OpenPort(name);
        await Assert.ThrowsAsync<VisaTimeoutException>(async () => await port.ReadAsync(new byte[1], 10));
        await Task.Delay(200);

        int before = ThreadCount();
        using var cts = new CancellationTokenSource();
        var reads = Enumerable.Range(0, 20).Select(_ => port.ReadAsync(new byte[16], -1, cts.Token).AsTask()).ToList();
        await Task.Delay(1000);
        int during = ThreadCount();
        output.WriteLine($"{before} threads before, {during} with {reads.Count} pending reads.");
        Assert.All(reads, read => Assert.False(read.IsCompleted));
        Assert.True(during - before <= 4, $"{before} threads before, {during} with {reads.Count} pending reads.");

        cts.Cancel();
        foreach (var read in reads)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<VisaTimeoutException>(() => port.Read(new byte[1], 50));
    }

    // ------------------------------------------------------------------ Linux pseudo-terminals

    [Fact]
    public async Task Pty_ExchangesDataAsynchronously()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (master, _, port) = OpenPty();

        await port.WriteAsync("*IDN?\n"u8.ToArray(), 1000);
        Assert.Equal("*IDN?\n", Encoding.ASCII.GetString(MasterRead(master, 6, 2000)));

        var buffer = new byte[64];
        var read = port.ReadAsync(buffer, 5000).AsTask();
        await Task.Delay(50);
        Assert.False(read.IsCompleted);
        MasterWrite(master, "VisaCs,Serial\n"u8);
        int n = await read.WaitAsync(TimeSpan.FromSeconds(5));
        while (n < 14) n += await port.ReadAsync(buffer.AsMemory(n), 2000);
        Assert.Equal("VisaCs,Serial\n", Encoding.ASCII.GetString(buffer, 0, n));

        // A timeout of 0 returns bytes that have already arrived.
        MasterWrite(master, "now"u8);
        Assert.True(SpinWait.SpinUntil(() => port.BytesAvailable == 3, 2000));
        Assert.Equal(3, await port.ReadAsync(buffer, 0));
        Assert.Equal("now", Encoding.ASCII.GetString(buffer, 0, 3));

        // DiscardNull applies to asynchronous reads too.
        port.Configure(SerialSettings.Default with { BaudRate = 115200, DiscardNull = true });
        MasterWrite(master, [0, (byte)'A', 0, 0, (byte)'B']);
        n = 0;
        while (n < 2) n += await port.ReadAsync(buffer.AsMemory(n), 2000);
        Assert.Equal("AB", Encoding.ASCII.GetString(buffer, 0, n));

        await port.DrainAsync(5000).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        port.Drain(5000);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => port.DrainAsync(-1, new CancellationToken(true)).AsTask());
    }

    [Fact]
    public unsafe void Pty_ConfiguresTheKernelTermios()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (_, slave, port) = OpenPty();
        int fd = DescriptorOf(slave);
        Assert.True(fd >= 0);

        // Open put the terminal in raw mode.
        var t = GetTermios2(fd);
        Assert.Equal(0u, t.c_lflag & (0x2u | 0x8u | 0x1u)); // ICANON, ECHO, ISIG
        Assert.Equal(0u, t.c_oflag & 0x1u); // OPOST
        Assert.Equal(0u, t.c_iflag & (0x100u | 0x400u)); // ICRNL, IXON

        // The kernel's speed is the configured rate, standard or not, and the input speed follows it
        // (glibc 2.42's cfsetspeed takes plain numbers, so passing it the old Bxxx codes set 13 baud for 9600).
        foreach (int baud in new[] { 9600, 115200, 250000, 300, 1_000_000, 9600 })
        {
            port.Configure(SerialSettings.Default with { BaudRate = baud });
            t = GetTermios2(fd);
            Assert.Equal((uint)baud, t.c_ospeed);
            Assert.Equal((uint)baud, t.c_ispeed);
            Assert.Equal(0u, t.c_cflag & 0x100F0000u); // CIBAUD: input speed = output speed
        }

        port.Configure(SerialSettings.Default with
        {
            DataBits = 7, Parity = SerialParity.Mark, StopBits = SerialStopBitsMode.Two,
            FlowControl = SerialFlowControlModes.XOnXOff | SerialFlowControlModes.RtsCts, XOnCharacter = 0x01, XOffCharacter = 0x02,
        });
        t = GetTermios2(fd);
        // A pseudo-terminal forces CS8 and clears PARENB, so the character size and parity enable cannot be seen here.
        Assert.NotEqual(0u, t.c_cflag & 0x40u); // CSTOPB
        Assert.Equal(0x40000200u, t.c_cflag & 0x40000200u); // CMSPAR | PARODD (mark parity)
        Assert.NotEqual(0u, t.c_cflag & 0x80000000u); // CRTSCTS
        Assert.Equal(0x1410u, t.c_iflag & 0x1410u); // IXOFF | IXON | INPCK
        Assert.Equal(1, t.c_cc[8]); // VSTART
        Assert.Equal(2, t.c_cc[9]); // VSTOP

        // Rejected settings leave the port as it was.
        var before = port.Settings;
        Assert.Equal(VisaStatus.ErrorAttributeStateNotSupported,
            Assert.Throws<VisaException>(() => port.Configure(before with { FlowControl = SerialFlowControlModes.DtrDsr })).Status);
        Assert.Equal(VisaStatus.ErrorAttributeStateNotSupported,
            Assert.Throws<VisaException>(() => port.Configure(before with { ReplacementCharacter = (byte)'?', DiscardNull = true })).Status);
        Assert.Equal(before, port.Settings);
    }

    [Fact]
    public async Task Pty_HangUpEndsTransfersWithConnectionLost()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (master, _, port) = OpenPty();
        var buffer = new byte[16];
        var blocked = Task.Factory.StartNew(() => port.Read(buffer, 10_000), TaskCreationOptions.LongRunning);
        var pending = port.ReadAsync(new byte[16], 10_000).AsTask();
        await Task.Delay(150);
        Assert.False(blocked.IsCompleted);
        Assert.False(pending.IsCompleted);

        // Closing the master hangs up the slave like an unplugged USB adapter: poll keeps reporting it readable and
        // read() returns 0.
        var watch = Stopwatch.StartNew();
        _masters.Remove(master);
        close(master);
        Assert.Equal(VisaStatus.ErrorConnectionLost, (await Assert.ThrowsAsync<VisaException>(() => blocked).WaitAsync(TimeSpan.FromSeconds(15))).Status);
        Assert.Equal(VisaStatus.ErrorConnectionLost, (await Assert.ThrowsAsync<VisaException>(() => pending).WaitAsync(TimeSpan.FromSeconds(15))).Status);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"The transfers ended after {watch.ElapsedMilliseconds} ms.");

        Assert.Equal(VisaStatus.ErrorConnectionLost, Assert.Throws<VisaException>(() => port.Read(buffer, 5000)).Status);
        Assert.Equal(VisaStatus.ErrorConnectionLost, (await Assert.ThrowsAsync<VisaException>(async () => await port.ReadAsync(buffer, 5000))).Status);
        Assert.Equal(VisaStatus.ErrorConnectionLost, Assert.Throws<VisaException>(() => port.Write("x"u8, 5000)).Status);
        Assert.Equal(VisaStatus.ErrorConnectionLost, (await Assert.ThrowsAsync<VisaException>(async () => await port.WriteAsync("x"u8.ToArray(), 5000))).Status);
        Assert.True(watch.ElapsedMilliseconds < 3000, $"Took {watch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task Pty_CancelledReadsLoseNoData()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (master, _, port) = OpenPty();
        var received = new List<byte>();
        var buffer = new byte[256];
        int completed = 0, cancelled = 0;
        var random = new Random(1);
        for (int i = 0; i < 200; i++)
        {
            using var cts = new CancellationTokenSource();
            var read = port.ReadAsync(buffer, -1, cts.Token).AsTask();
            // Send a byte on another thread and cancel at about the same moment, in either order.
            byte value = (byte)i;
            int sendDelay = random.Next(20_000), cancelDelay = random.Next(20_000);
            var send = Task.Run(() =>
            {
                Thread.SpinWait(sendDelay);
                MasterWrite(master, [value]);
            });
            Thread.SpinWait(cancelDelay);
            cts.Cancel();
            await send;
            try
            {
                int n = await read;
                received.AddRange(buffer.AsSpan(0, n));
                completed++;
            }
            catch (OperationCanceledException)
            {
                cancelled++;
            }
            // What a cancelled read did not take is still waiting in the port; collect it so that the next read
            // starts with nothing received.
            while (received.Count < i + 1)
            {
                int n = await port.ReadAsync(buffer, 1000);
                received.AddRange(buffer.AsSpan(0, n));
            }
        }
        output.WriteLine($"{completed} reads returned data, {cancelled} were cancelled.");
        Assert.Equal(Enumerable.Range(0, 200).Select(i => (byte)i), received);
    }

    [Fact]
    public async Task Pty_ReadsAndWritesAtTheSameTime()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (master, _, port) = OpenPty();
        var data = Enumerable.Range(0, 1 << 20).Select(i => (byte)(i * 7 + (i >> 8))).ToArray();

        var write = port.WriteAsync(data, 30_000).AsTask();
        var buffer = new byte[16];
        var read = port.ReadAsync(buffer, 30_000).AsTask();
        await Task.Delay(200);
        Assert.False(write.IsCompleted); // nobody reads the master side yet: the pseudo-terminal's buffer is full
        Assert.False(read.IsCompleted);

        MasterWrite(master, "ping"u8);
        int n = await read.WaitAsync(TimeSpan.FromSeconds(5));
        while (n < 4) n += await port.ReadAsync(buffer.AsMemory(n), 2000);
        Assert.Equal("ping", Encoding.ASCII.GetString(buffer, 0, n));
        Assert.False(write.IsCompleted);

        var sent = MasterRead(master, data.Length, 30_000);
        await write.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(data.AsSpan().SequenceEqual(sent), $"{sent.Length} of {data.Length} bytes arrived intact.");

        // Synchronous write on another thread while an asynchronous read is pending.
        read = port.ReadAsync(buffer, 30_000).AsTask();
        var syncWrite = Task.Factory.StartNew(() => port.Write(data, 30_000), TaskCreationOptions.LongRunning);
        await Task.Delay(100);
        MasterWrite(master, "pong"u8);
        n = await read.WaitAsync(TimeSpan.FromSeconds(5));
        while (n < 4) n += await port.ReadAsync(buffer.AsMemory(n), 2000);
        Assert.Equal("pong", Encoding.ASCII.GetString(buffer, 0, n));
        sent = MasterRead(master, data.Length, 30_000);
        await syncWrite.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(data.AsSpan().SequenceEqual(sent), $"{sent.Length} of {data.Length} bytes arrived intact.");
    }

    [Fact]
    public async Task Pty_WriteTimeoutsReportBytesTransferred()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (master, _, port) = OpenPty();
        var data = new byte[1 << 20];

        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<VisaTimeoutException>(async () => await port.WriteAsync(data, 200));
        Assert.InRange(watch.ElapsedMilliseconds, 180, 1500);
        Assert.InRange(ex.BytesTransferred, 1, data.Length - 1);
        Assert.Equal(ex.BytesTransferred, MasterDrain(master));

        ex = Assert.Throws<VisaTimeoutException>(() => port.Write(data, 200));
        Assert.InRange(ex.BytesTransferred, 1, data.Length - 1);
        Assert.Equal(ex.BytesTransferred, MasterDrain(master));

        using var cts = new CancellationTokenSource(200);
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await port.WriteAsync(data, -1, cts.Token));
        var inner = Assert.IsType<VisaTimeoutException>(canceled.InnerException);
        Assert.Equal(inner.BytesTransferred, MasterDrain(master));
    }

    [Fact]
    public async Task Pty_AbortEndsPendingReadAndWrite()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (master, _, port) = OpenPty();
        var buffer = new byte[16];

        var write = port.WriteAsync(new byte[1 << 20], -1).AsTask();
        var read = port.ReadAsync(buffer, -1).AsTask();
        await Task.Delay(150);
        Assert.False(write.IsCompleted);
        Assert.False(read.IsCompleted);
        port.Abort();
        Assert.Equal(VisaStatus.ErrorAbort, (await Assert.ThrowsAsync<VisaException>(() => write).WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(VisaStatus.ErrorAbort, (await Assert.ThrowsAsync<VisaException>(() => read).WaitAsync(TimeSpan.FromSeconds(5))).Status);
        MasterDrain(master);
        Assert.Throws<VisaTimeoutException>(() => port.Read(buffer, 50));

        // An abort with nothing in progress is not remembered: later calls run normally.
        port.Abort();
        await Assert.ThrowsAsync<VisaTimeoutException>(async () => await port.ReadAsync(buffer, 50));
        Assert.Throws<VisaTimeoutException>(() => port.Read(buffer, 50));

        // The data path is intact.
        MasterWrite(master, "ok"u8);
        int n = await port.ReadAsync(buffer, 2000);
        while (n < 2) n += await port.ReadAsync(buffer.AsMemory(n), 2000);
        Assert.Equal("ok", Encoding.ASCII.GetString(buffer, 0, n));
    }

    [Fact]
    public async Task Pty_PendingReadsHoldNoThreads()
    {
        if (!OperatingSystem.IsLinux()) return;
        var pairs = Enumerable.Range(0, 50).Select(_ => OpenPty()).ToList();
        // Start the poll thread and warm up before counting.
        MasterWrite(pairs[0].Master, "w"u8);
        Assert.Equal(1, await pairs[0].Port.ReadAsync(new byte[1], 2000));
        await Task.Delay(200);

        int before = ThreadCount();
        var buffers = pairs.Select(_ => new byte[16]).ToArray();
        var reads = pairs.Select((p, i) => p.Port.ReadAsync(buffers[i], -1).AsTask()).ToList();
        await Task.Delay(1000);
        int during = ThreadCount();
        output.WriteLine($"{before} threads before, {during} with {reads.Count} pending reads.");
        Assert.All(reads, read => Assert.False(read.IsCompleted));
        Assert.True(during - before <= 4, $"{before} threads before, {during} with {reads.Count} pending reads.");

        // Every pending read completes with its own data.
        for (int i = 0; i < pairs.Count; i++) MasterWrite(pairs[i].Master, [(byte)(i + 1)]);
        var counts = await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(10));
        for (int i = 0; i < pairs.Count; i++)
        {
            Assert.Equal(1, counts[i]);
            Assert.Equal(i + 1, buffers[i][0]);
        }

        // Fifty reads cancelled at once.
        using var cts = new CancellationTokenSource();
        reads = pairs.Select(p => p.Port.ReadAsync(new byte[16], -1, cts.Token).AsTask()).ToList();
        await Task.Delay(100);
        cts.Cancel();
        foreach (var read in reads)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read).WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ------------------------------------------------------------------ SerialSession on a pseudo-terminal

    [Fact]
    public void Pty_SessionWritesLastBitAndClears()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (master, slave) = OpenPtyMaster();
        using var rm = new ResourceManager();
        using var session = rm.Open<SerialSession>($"ASRL{slave}::INSTR");
        session.TimeoutMilliseconds = 2000;

        // END_OUT = last bit: the highest data bit is clear in every byte and set in the last byte of a message.
        session.EndOutput = SerialTerminationMethod.LastBit;
        session.Write(new byte[] { 0x41, 0xC2, 0x43 });
        Assert.Equal(new byte[] { 0x41, 0x42, 0xC3 }, MasterRead(master, 3, 2000));
        session.SendEndEnabled = false;
        session.Write(new byte[] { 0xC1, 0x42 });
        Assert.Equal(new byte[] { 0x41, 0x42 }, MasterRead(master, 2, 2000));
        session.SendEndEnabled = true;
        session.DataBits = 7;
        session.Write(new byte[] { 0x7F, 0x00 });
        Assert.Equal(new byte[] { 0x3F, 0x40 }, MasterRead(master, 2, 2000));
        session.DataBits = 8;

        // viClear: a break of BreakLength (normal protocol) or "*CLS\n" (IEEE 488.2 strings); pending input is dropped.
        session.EndOutput = SerialTerminationMethod.TerminationCharacter;
        MasterWrite(master, "stale\n"u8);
        Assert.True(SpinWait.SpinUntil(() => session.BytesAvailable == 6, 2000));
        session.BreakLength = 100;
        var watch = Stopwatch.StartNew();
        session.Clear();
        Assert.True(watch.ElapsedMilliseconds >= 90, $"Clear took {watch.ElapsedMilliseconds} ms.");
        Assert.Equal(0, session.BytesAvailable);
        session.IOProtocol = IOProtocol.Ieee4882Strings;
        session.Clear();
        Assert.Equal("*CLS\n", Encoding.ASCII.GetString(MasterRead(master, 5, 2000)));
        session.AssertTrigger();
        Assert.Equal("*TRG\n", Encoding.ASCII.GetString(MasterRead(master, 5, 2000)));
        Assert.Equal(0, MasterDrain(master));

        Assert.Equal(VisaStatus.ErrorAttributeStateNotSupported, Assert.Throws<VisaException>(() => session.BreakLength = 0).Status);
        Assert.Equal(VisaStatus.ErrorAttributeStateNotSupported, Assert.Throws<VisaException>(() => session.BaudRate = 0).Status);
        Assert.Equal(VisaStatus.ErrorAttributeStateNotSupported,
            Assert.Throws<VisaException>(() => session.SetAttribute(VisaAttribute.SerialParity, (SerialParity)9)).Status);
        Assert.Equal(SerialParity.None, session.Parity);
        Assert.Equal(100, session.BreakLength);
    }

    [Fact]
    public void Pty_ClosedSessionRejectsSettingsAndLines()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (_, slave) = OpenPtyMaster();
        using var rm = new ResourceManager();
        var session = rm.Open<SerialSession>($"ASRL{slave}::INSTR");
        session.BaudRate = 115200;
        session.Dispose();

        // The descriptor is closed and its number may already belong to another file: nothing may touch it.
        Assert.False(IsOpenInProcess(slave));
        Assert.Equal(VisaStatus.ErrorInvalidObject, Assert.Throws<VisaException>(() => session.BaudRate = 9600).Status);
        Assert.Equal(VisaStatus.ErrorInvalidObject, Assert.Throws<VisaException>(() => session.RtsState).Status);
        Assert.Equal(VisaStatus.ErrorInvalidObject, Assert.Throws<VisaException>(() => session.BytesAvailable).Status);
        Assert.Equal(VisaStatus.ErrorInvalidObject, Assert.Throws<VisaException>(() => session.BreakState = LineState.Asserted).Status);
        Assert.Equal(115200, session.BaudRate);
    }
}

// Helpers with pointers, in a part of the class that is an unsafe context (the tests above await, which is not
// allowed in one).
public sealed unsafe partial class SerialPortAsyncTests
{
    private static int ThreadCount()
    {
        using var process = Process.GetCurrentProcess();
        return process.Threads.Count;
    }

    private (int Master, string Slave, NativeSerialPort Port) OpenPty()
    {
        var (master, slave) = OpenPtyMaster();
        return (master, slave, OpenPort(slave));
    }

    private (int Master, string Slave) OpenPtyMaster()
    {
        int master = posix_openpt(2 | 0x100 | 0x800); // O_RDWR | O_NOCTTY | O_NONBLOCK
        Assert.True(master >= 0);
        _masters.Add(master);
        Assert.Equal(0, grantpt(master));
        Assert.Equal(0, unlockpt(master));
        return (master, Marshal.PtrToStringUTF8(ptsname(master))!);
    }

    private static void MasterWrite(int master, ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
        {
            int offset = 0;
            while (offset < data.Length)
            {
                nint n = write(master, p + offset, data.Length - offset);
                if (n > 0)
                {
                    offset += (int)n;
                    continue;
                }
                var pfd = new PollFd { fd = master, events = POLLOUT };
                poll(&pfd, 1, 100);
            }
        }
    }

    /// <summary>Reads <paramref name="count"/> bytes from the master side, or what arrives in time.</summary>
    private static byte[] MasterRead(int master, int count, int timeoutMilliseconds)
    {
        var result = new List<byte>(count);
        var buffer = new byte[64 * 1024];
        var watch = Stopwatch.StartNew();
        while (result.Count < count && watch.ElapsedMilliseconds < timeoutMilliseconds)
        {
            var pfd = new PollFd { fd = master, events = POLLIN };
            poll(&pfd, 1, 50);
            nint n;
            fixed (byte* p = buffer) n = read(master, p, Math.Min(buffer.Length, count - result.Count));
            if (n > 0) result.AddRange(buffer.AsSpan(0, (int)n));
        }
        return result.ToArray();
    }

    /// <summary>Reads from the master side until nothing arrives for 200 ms; returns the byte count.</summary>
    private static int MasterDrain(int master)
    {
        int total = 0;
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var pfd = new PollFd { fd = master, events = POLLIN };
            if (poll(&pfd, 1, 200) <= 0) return total;
            nint n;
            fixed (byte* p = buffer) n = read(master, p, buffer.Length);
            if (n <= 0) return total;
            total += (int)n;
        }
    }

    /// <summary>Kernel struct termios2 (asm-generic layout).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Termios2
    {
        public uint c_iflag, c_oflag, c_cflag, c_lflag;
        public byte c_line;
        public fixed byte c_cc[19];
        public uint c_ispeed, c_ospeed;
    }

    private static Termios2 GetTermios2(int fd)
    {
        Termios2 t;
        Assert.Equal(0, ioctl(fd, 0x802C542A, &t)); // TCGETS2
        return t;
    }

    public void Dispose()
    {
        foreach (var port in _ports) port.Dispose();
        foreach (int master in _masters) close(master);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    private const short POLLIN = 0x1;
    private const short POLLOUT = 0x4;

    [LibraryImport("libc.so.6", SetLastError = true)] private static partial int posix_openpt(int flags);
    [LibraryImport("libc.so.6", SetLastError = true)] private static partial int grantpt(int fd);
    [LibraryImport("libc.so.6", SetLastError = true)] private static partial int unlockpt(int fd);
    [LibraryImport("libc.so.6", SetLastError = true)] private static partial nint ptsname(int fd);
    [LibraryImport("libc.so.6", SetLastError = true)] private static partial nint read(int fd, byte* buffer, nint count);
    [LibraryImport("libc.so.6", SetLastError = true)] private static partial nint write(int fd, byte* buffer, nint count);
    [LibraryImport("libc.so.6", SetLastError = true)] private static partial int poll(PollFd* fds, nuint nfds, int timeout);
    [LibraryImport("libc.so.6", SetLastError = true)] private static partial int close(int fd);
    [LibraryImport("libc.so.6", SetLastError = true)] private static partial int ioctl(int fd, nuint request, void* arg);
}

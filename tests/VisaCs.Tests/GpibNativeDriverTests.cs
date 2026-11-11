using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using VisaCs.Gpib;

namespace VisaCs.Tests;

/// <summary>
/// Exercises <see cref="Ni488Driver"/> (the real native binding) against tests/Native/fake_ni4882.c, a library that
/// exports the NI-488.2 C API. Build it with tests/Native/build-fake-gpib.(sh|cmd) and set
/// VISACS_TEST_GPIB_LIBRARY to its path. The build also makes a variant without ibnotify (completion is polled, as
/// with linux-gpib) and one without asynchronous calls; run the tests with each.
/// </summary>
public sealed class GpibNativeDriverTests
{
    private const int SetNotifyFails = 1, DropNotifications = 2, StopCalls = 3, UnsynchronizedTransfers = 4,
        CallbackResyncs = 5, WaitResyncs = 6, CallDelay = 7, LateCallbackReturn = 8, UndoneArmings = 9, HideSrqi = 10,
        SerialPolls = 11;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static readonly Lazy<nint> LibraryHandle = new(() => NativeLibrary.Load(LibraryPath!));

    private static string? LibraryPath => Environment.GetEnvironmentVariable("VISACS_TEST_GPIB_LIBRARY");

    /// <summary>True if the library has asynchronous transfers (ibrda and friends).</summary>
    private static bool HasAsyncCalls => Exports("ibrda");

    /// <summary>True if the library under test exports <paramref name="name"/>.</summary>
    private static bool Exports(string name) => NativeLibrary.TryGetExport(LibraryHandle.Value, name, out _);

    /// <summary>Calls the fake library's test hook (fake_ni4882_control in fake_ni4882.c).</summary>
    private static unsafe int Control(int option, int value = 0) =>
        ((delegate* unmanaged<int, int, int>)NativeLibrary.GetExport(LibraryHandle.Value, "fake_ni4882_control"))(option, value);

    private static Ni488Driver? CreateDriver() => LibraryPath is null ? null : new Ni488Driver(LibraryPath);

    private static Task WriteAsync(IGpibIO io, string text, bool end = true) =>
        io.WriteAsync(Encoding.ASCII.GetBytes(text), end, 5000).AsTask().WaitAsync(Patience);

    private static async Task<string> ReadAsync(IGpibIO io, byte? termChar = null)
    {
        var buffer = new byte[4096];
        var result = await io.ReadAsync(buffer, termChar, 5000).AsTask().WaitAsync(Patience);
        return Encoding.ASCII.GetString(buffer, 0, result.Count);
    }

    private static async Task<string> QueryAsync(IGpibIO io, string command)
    {
        await WriteAsync(io, command + "\n");
        return await ReadAsync(io);
    }

    private static int ThreadCount()
    {
        using var process = Process.GetCurrentProcess();
        return process.Threads.Count;
    }

    private static ResourceManager? Create(out Ni488Driver driver)
    {
        driver = new Ni488Driver(LibraryPath);
        if (LibraryPath is null) return null;
        return new ResourceManager(new ResourceManagerOptions
        {
            GpibDriver = driver,
            EnableLanDiscovery = false,
            EnableMdnsDiscovery = false,
            EnableUsb = false,
            EnableSerial = false,
        });
    }

    [Fact]
    public void LoadsTheDriverAndFindsListeners()
    {
        using var rm = Create(out var driver);
        if (rm is null) return;
        Assert.True(driver.IsAvailable);
        Assert.Equal(LibraryPath, driver.LoadedLibrary);
        Assert.Equal([0], driver.ListBoards());
        Assert.Equal(["GPIB0::22::INSTR", "GPIB0::5::INSTR", "GPIB0::INTFC"], rm.Find("GPIB?*"));
    }

    [Fact]
    public void ReportsMissingLibraries()
    {
        string path = Path.Combine(Path.GetTempPath(), "no-such-gpib-driver.dll");
        var driver = new Ni488Driver(path);
        Assert.False(driver.IsAvailable);
        Assert.Contains(path, driver.LoadError);
        Assert.Empty(driver.ListBoards());
        var error = Assert.Throws<GpibException>(() => driver.OpenBoard(0));
        Assert.Equal(GpibError.LibraryNotFound, error.Error);
        Assert.Contains(path, error.Message);
    }

    [Fact]
    public async Task SessionsTalkAsynchronously()
    {
        await using var rm = Create(out _);
        if (rm is null) return;
        await using var session = await rm.OpenAsync<GpibSession>("GPIB0::22::INSTR");
        session.TimeoutMilliseconds = 5000;
        Assert.Equal("FAKE,GPIB,22,1.0", await session.QueryAsync("*IDN?").WaitAsync(Patience));
        Assert.Equal(1000, (await session.QueryBinaryBlockAsync("DATA? 1000").WaitAsync(Patience)).Length);
        Assert.Equal("late", await session.QueryAsync("DELAY? 300 late").WaitAsync(Patience));

        // A timeout and a cancellation both leave the session usable.
        session.TimeoutMilliseconds = 200;
        await Assert.ThrowsAsync<VisaTimeoutException>(() => session.QueryAsync("DELAY? 1000 slow").WaitAsync(Patience));
        session.TimeoutMilliseconds = 5000;
        await session.ClearAsync().WaitAsync(Patience);
        using (var cts = new CancellationTokenSource(200))
        {
            var slow = session.QueryAsync("DELAY? 1000 slow", cts.Token).WaitAsync(Patience);
            // Without ibrda a transfer runs as a blocking driver call that cannot be stopped once it has started.
            if (HasAsyncCalls) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow);
            else Assert.Equal("slow", await slow);
        }
        await session.ClearAsync().WaitAsync(Patience);
        Assert.Equal("again", await session.QueryAsync("ECHO? again").WaitAsync(Patience));
        _ = await session.ReadStatusByteAsync().WaitAsync(Patience);
        await session.AssertTriggerAsync().WaitAsync(Patience);
    }

    [Fact]
    public void TalksToInstruments()
    {
        using var rm = Create(out _);
        if (rm is null) return;
        using var session = rm.Open<GpibSession>("GPIB0::22::INSTR");
        Assert.Equal("FAKE,GPIB,22,1.0", session.Query("*IDN?"));
        Assert.Equal("hello gpib", session.Query("ECHO? hello gpib"));

        var block = session.QueryBinaryBlock("DATA? 1000");
        Assert.Equal(1000, block.Length);
        Assert.Equal(10, block[10]); // contains '\n' bytes: EOI, not EOS, ends the message

        // EOS (termination character) handled by the driver.
        session.TerminationCharacter = (byte)',';
        session.TerminationCharacterEnabled = true;
        session.WriteLine("ECHO? a,b");
        Assert.Equal("a,", session.ReadString());
        session.TerminationCharacterEnabled = false;
        Assert.Equal("b", session.ReadLine());

        using var other = rm.Open("GPIB0::5::INSTR");
        Assert.Equal("FAKE,GPIB,5,1.0", other.Query("*IDN?"));
    }

    [Fact]
    public void PollsTriggersClearsAndTimesOut()
    {
        using var rm = Create(out _);
        if (rm is null) return;
        using var session = rm.Open("GPIB0::22::INSTR");
        session.WriteLine("*CLS");
        Assert.Equal(0, session.ReadStatusByte());
        session.WriteLine("SRQ");
        Assert.Equal(0x41, session.ReadStatusByte());
        Assert.Equal(0x01, session.ReadStatusByte()); // the serial poll cleared RQS
        session.WriteLine("*CLS");

        int before = int.Parse(session.Query("TRG?"));
        session.AssertTrigger();
        Assert.Equal(before + 1, int.Parse(session.Query("TRG?")));

        session.WriteLine("*IDN?");
        session.Clear();
        session.TimeoutMilliseconds = 300;
        var ex = Assert.Throws<VisaTimeoutException>(() => session.ReadLine()); // the clear discarded the response
        Assert.Equal(VisaStatus.ErrorTimeout, ex.Status);
    }

    [Fact]
    public void ReportsAbsentListeners()
    {
        using var rm = Create(out _);
        if (rm is null) return;
        using var session = rm.Open("GPIB0::7::INSTR");
        var error = Assert.Throws<VisaException>(() => session.WriteLine("*IDN?"));
        Assert.Equal(VisaStatus.ErrorNoListeners, error.Status);
        Assert.Equal(2, error.NativeError); // ENOL
        Assert.IsType<GpibException>(error.InnerException);
        Assert.Equal(VisaStatus.ErrorResourceNotFound, Assert.Throws<VisaException>(() => rm.Open("GPIB1::INTFC")).Status);
    }

    [Fact]
    public void DeliversServiceRequests()
    {
        using var rm = Create(out _);
        if (rm is null) return;
        using var session = rm.Open("GPIB0::5::INSTR");
        session.WriteLine("*CLS");
        session.EnableEvent(EventType.ServiceRequest, EventMechanism.Queue);
        session.WriteLine("SRQ");
        var e = Assert.IsType<ServiceRequestEventArgs>(session.WaitOnEvent(EventType.ServiceRequest, 5000));
        Assert.Equal((byte)0x41, e.StatusByte!.Value);
        session.DisableEvent(EventType.ServiceRequest);
        session.WriteLine("*CLS");
    }

    [Fact]
    public void ControlsTheBoard()
    {
        using var rm = Create(out _);
        if (rm is null) return;
        using var device = rm.Open<GpibSession>("GPIB0::22::INSTR");
        device.ControlRen(RemoteLocalMode.AssertAddressLocalLockout);
        Assert.Equal(LineState.Asserted, device.RenState);
        device.ControlRen(RemoteLocalMode.Deassert);
        Assert.Equal(LineState.Unasserted, device.RenState);

        using var board = rm.Open<GpibInterfaceSession>("GPIB0::INTFC");
        Assert.True(board.IsSystemController);
        Assert.True(board.IsControllerInCharge);
        board.SendCommand([0x3F, 0x5F]); // UNL, UNT
        board.SendInterfaceClear();
        board.ControlAtn(GpibAtnMode.Assert);
        Assert.Equal(0, board.PrimaryAddress);
    }

    [Fact]
    public async Task WritesAndReadsAsynchronously()
    {
        var driver = CreateDriver();
        if (driver is null) return;
        using var device = driver.OpenDevice(0, 22, null);
        device.Clear(1000);
        Assert.Equal("FAKE,GPIB,22,1.0\n", await QueryAsync(device, "*IDN?"));
        Assert.Equal("hello async\n", await QueryAsync(device, "ECHO? hello async"));

        // An answer that comes later completes the pending read.
        await WriteAsync(device, "DELAY? 150 later\n");
        var buffer = new byte[64];
        var result = await device.ReadAsync(buffer, null, 5000).AsTask().WaitAsync(Patience);
        Assert.Equal("later\n", Encoding.ASCII.GetString(buffer, 0, result.Count));
        Assert.True(result.End);

        // Binary data: EOI, not the '\n' bytes inside, ends the message.
        await WriteAsync(device, "DATA? 1000\n");
        var block = new byte[2000];
        result = await device.ReadAsync(block, null, 5000).AsTask().WaitAsync(Patience);
        Assert.Equal(6 + 1000 + 1, result.Count); // "#41000", the data, '\n'
        Assert.True(result.End);
        Assert.Equal(10, block[6 + 10]);

        // The termination character (EOS) ends a read.
        await WriteAsync(device, "ECHO? a,b\n");
        Assert.Equal("a,", await ReadAsync(device, (byte)','));
        Assert.Equal("b\n", await ReadAsync(device));

        // EOI only with the last part of a message.
        await WriteAsync(device, "ECHO? pie", end: false);
        await WriteAsync(device, "ces\n");
        Assert.Equal("pieces\n", await ReadAsync(device));

        using var board = driver.OpenBoard(0);
        await board.SendCommandAsync(new byte[] { 0x3F, 0x5F }, 1000).AsTask().WaitAsync(Patience); // UNL, UNT

        // Errors map like those of the synchronous calls, also when the driver reports them at completion.
        using var absent = driver.OpenDevice(0, 7, null);
        var error = await Assert.ThrowsAsync<GpibException>(() => WriteAsync(absent, "*IDN?\n"));
        Assert.Equal(GpibError.NoListeners, error.Error);
        Assert.Equal(0, Control(UnsynchronizedTransfers));
    }

    [Fact]
    public async Task AsynchronousReadsTimeOut()
    {
        var driver = CreateDriver();
        if (driver is null) return;
        using var device = driver.OpenDevice(0, 22, null);
        device.Clear(1000);

        var watch = Stopwatch.StartNew();
        var timeout = await Assert.ThrowsAsync<GpibTimeoutException>(async () => await device.ReadAsync(new byte[64], null, 300));
        Assert.Equal(6, timeout.NativeError); // EABO
        if (HasAsyncCalls) Assert.InRange(watch.ElapsedMilliseconds, 250, 5000); // the driver's timeout ended it

        // Bytes received before the timeout are returned; the next read times out.
        await WriteAsync(device, "NOEOI? abc\n");
        var buffer = new byte[64];
        var result = await device.ReadAsync(buffer, null, 300).AsTask().WaitAsync(Patience);
        Assert.Equal("abc\n", Encoding.ASCII.GetString(buffer, 0, result.Count));
        Assert.False(result.End);
        await Assert.ThrowsAsync<GpibTimeoutException>(async () => await device.ReadAsync(buffer, null, 300));
        Assert.Equal(0, Control(UnsynchronizedTransfers));
    }

    [Fact]
    public async Task CancelsPendingAsynchronousReads()
    {
        var driver = CreateDriver();
        if (driver is null) return;
        using var device = driver.OpenDevice(0, 22, null);
        device.Clear(1000);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await device.ReadAsync(new byte[64], null, 1000, new CancellationToken(true)));
        if (!HasAsyncCalls) return; // the read would time out at once on the worker thread

        int stops = Control(StopCalls);
        using var cancellation = new CancellationTokenSource();
        var read = device.ReadAsync(new byte[64], null, Timeout.Infinite, cancellation.Token).AsTask();
        await Task.Delay(100);
        Assert.False(read.IsCompleted);

        // One transfer at a time per descriptor.
        var busy = await Assert.ThrowsAsync<GpibException>(async () => await device.ReadAsync(new byte[64], null, 1000));
        Assert.Equal(GpibError.AsyncInProgress, busy.Error);

        cancellation.Cancel();
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Patience));
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.True(Control(StopCalls) > stops); // the driver stopped the transfer
        Assert.Equal(0, Control(UnsynchronizedTransfers));

        // The device works afterwards.
        Assert.Equal("FAKE,GPIB,22,1.0\n", await QueryAsync(device, "*IDN?"));

        // Cancelling while the answer is on its way.
        await WriteAsync(device, "DELAY? 300 slow\n");
        using var soon = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await device.ReadAsync(new byte[64], null, 5000, soon.Token).AsTask().WaitAsync(Patience));
        device.Clear(1000);
        Assert.Equal("FAKE,GPIB,22,1.0\n", await QueryAsync(device, "*IDN?"));
    }

    [Fact]
    public async Task PollsClearsAndTriggersAsynchronously()
    {
        var driver = CreateDriver();
        if (driver is null) return;
        using var device = driver.OpenDevice(0, 22, null);
        await WriteAsync(device, "*CLS\n");
        Assert.Equal(0, await device.SerialPollAsync(1000).AsTask().WaitAsync(Patience));
        await WriteAsync(device, "SRQ\n");
        Assert.Equal(0x41, await device.SerialPollAsync(1000).AsTask().WaitAsync(Patience));
        Assert.Equal(0x01, await device.SerialPollAsync(1000).AsTask().WaitAsync(Patience)); // the poll cleared RQS
        await WriteAsync(device, "*CLS\n");

        int before = int.Parse(await QueryAsync(device, "TRG?"));
        await device.TriggerAsync(1000).AsTask().WaitAsync(Patience);
        Assert.Equal(before + 1, int.Parse(await QueryAsync(device, "TRG?")));

        await WriteAsync(device, "*IDN?\n");
        await device.ClearAsync(1000).AsTask().WaitAsync(Patience);
        await Assert.ThrowsAsync<GpibTimeoutException>(async () => await device.ReadAsync(new byte[64], null, 200)); // cleared

        using var absent = driver.OpenDevice(0, 7, null);
        var error = await Assert.ThrowsAsync<GpibException>(async () => await absent.SerialPollAsync(1000));
        Assert.Equal(GpibError.NoListeners, error.Error);
    }

    [Fact]
    public async Task CancelsQueuedBlockingCalls()
    {
        var driver = CreateDriver();
        if (driver is null) return;
        using var device = driver.OpenDevice(0, 22, null);
        Control(CallDelay, 300); // serial poll, clear and trigger take 300 ms
        try
        {
            // Blocking calls of a board run in order on its worker thread: the poll waits behind the clear.
            var clear = device.ClearAsync(5000).AsTask();
            using var cancellation = new CancellationTokenSource();
            var poll = device.SerialPollAsync(5000, cancellation.Token).AsTask();
            await Task.Delay(50);
            var watch = Stopwatch.StartNew();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll.WaitAsync(Patience));
            Assert.True(watch.ElapsedMilliseconds < 200, $"Cancelling took {watch.ElapsedMilliseconds} ms.");
            Assert.False(clear.IsCompleted);
            await clear.WaitAsync(Patience);

            // A call that has started runs to its end; when it succeeds, its result wins.
            using var late = new CancellationTokenSource(100);
            await device.TriggerAsync(5000, late.Token).AsTask().WaitAsync(Patience);
            Assert.True(late.IsCancellationRequested);
        }
        finally
        {
            Control(CallDelay, 0);
        }
    }

    [Fact]
    public async Task WaitsForServiceRequestsAsynchronously()
    {
        var driver = CreateDriver();
        if (driver is null) return;
        using var device = driver.OpenDevice(0, 5, null);
        using var board = driver.OpenBoard(0);
        await WriteAsync(device, "*CLS\n");

        var watch = Stopwatch.StartNew();
        Assert.Null(await device.WaitForServiceRequestAsync(200).AsTask().WaitAsync(Patience));
        Assert.False(await board.WaitForServiceRequestAsync(200).AsTask().WaitAsync(Patience));
        Assert.InRange(watch.ElapsedMilliseconds, 350, 5000);

        // The device requests service while the wait is pending.
        var wait = device.WaitForServiceRequestAsync(5000).AsTask();
        await Task.Delay(100);
        Assert.False(wait.IsCompleted);
        await WriteAsync(device, "SRQ\n");
        Assert.Equal((byte)0x41, await wait.WaitAsync(Patience));

        var boardWait = board.WaitForServiceRequestAsync(Timeout.Infinite).AsTask();
        await Task.Delay(50);
        Assert.False(boardWait.IsCompleted);
        await WriteAsync(device, "SRQ\n");
        Assert.True(await boardWait.WaitAsync(Patience));
        Assert.Equal(0x41, await device.SerialPollAsync(1000).AsTask().WaitAsync(Patience));

        using var cancellation = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await device.WaitForServiceRequestAsync(Timeout.Infinite, cancellation.Token).AsTask().WaitAsync(Patience));
        await WriteAsync(device, "*CLS\n");
    }

    [Fact]
    public async Task DisposeEndsPendingOperations()
    {
        var driver = CreateDriver();
        if (driver is null) return;
        var device = driver.OpenDevice(0, 22, null);
        var slow = driver.OpenDevice(0, 22, null); // a device with a transfer in progress rejects other calls (EOIP)
        var board = driver.OpenBoard(0);
        device.Clear(1000);
        await WriteAsync(device, "*CLS\n");
        var read = HasAsyncCalls ? device.ReadAsync(new byte[64], null, Timeout.Infinite).AsTask() : null;
        var deviceWait = device.WaitForServiceRequestAsync(Timeout.Infinite).AsTask();
        var boardWait = board.WaitForServiceRequestAsync(Timeout.Infinite).AsTask();
        Control(CallDelay, 1500); // long enough that the clear below is still running when Dispose comes, even on a slow CI machine
        try
        {
            var clear = slow.ClearAsync(5000).AsTask(); // running on the worker thread
            var poll = slow.SerialPollAsync(5000).AsTask(); // queued behind it
            await Task.Delay(100);

            var watch = Stopwatch.StartNew();
            device.Dispose();
            slow.Dispose();
            board.Dispose();
            Assert.True(watch.ElapsedMilliseconds < 200, $"Dispose took {watch.ElapsedMilliseconds} ms.");

            if (read is not null) await Assert.ThrowsAsync<ObjectDisposedException>(() => read.WaitAsync(Patience));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => deviceWait.WaitAsync(Patience));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => boardWait.WaitAsync(Patience));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => poll.WaitAsync(Patience));
            await clear.WaitAsync(Patience); // the running call ends normally; the descriptor closes after it
            Assert.Equal(0, Control(UnsynchronizedTransfers));
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await device.ReadAsync(new byte[8], null, 100));
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await slow.SerialPollAsync(100));
        }
        finally
        {
            Control(CallDelay, 0);
        }
    }

    [Fact]
    public async Task CompletesThroughCallbacksOrPolling()
    {
        var driver = CreateDriver();
        if (driver is null) return;
        using var device = driver.OpenDevice(0, 22, null);
        int byCallback = Control(CallbackResyncs), byWait = Control(WaitResyncs);
        Assert.Equal("FAKE,GPIB,22,1.0\n", await QueryAsync(device, "*IDN?"));
        if (!HasAsyncCalls)
        {
            // Without asynchronous calls the board's worker thread runs the synchronous ones.
            Assert.Equal(byCallback, Control(CallbackResyncs));
            Assert.Equal(byWait, Control(WaitResyncs));
            return;
        }
        if (!Exports("ibnotify"))
        {
            // linux-gpib: the polling thread resynchronizes with ibwait and reads AsyncIbsta/AsyncIberr/AsyncIbcnt.
            Assert.True(Control(WaitResyncs) >= byWait + 2);
            Assert.Equal(byCallback, Control(CallbackResyncs));
            return;
        }

        // ibnotify callbacks report both transfers.
        Assert.True(Control(CallbackResyncs) >= byCallback + 2);
        Assert.Equal(byWait, Control(WaitResyncs));

        // A library that refuses ibnotify: the transfers are polled instead.
        Control(SetNotifyFails, 1);
        try
        {
            byWait = Control(WaitResyncs);
            Assert.Equal("FAKE,GPIB,22,1.0\n", await QueryAsync(device, "*IDN?"));
            Assert.True(Control(WaitResyncs) >= byWait + 2);
            await Assert.ThrowsAsync<GpibTimeoutException>(async () => await device.ReadAsync(new byte[8], null, 200));
        }
        finally
        {
            Control(SetNotifyFails, 0);
        }

        // A lost notification: the watchdog finds the finished transfer and arms again.
        Control(DropNotifications, 1);
        await WriteAsync(device, "ECHO? recovered\n");
        Assert.Equal("recovered\n", await ReadAsync(device));
        Assert.Equal(0, Control(DropNotifications));
        Assert.Equal(0, Control(UnsynchronizedTransfers));
    }

    [Fact]
    public async Task PendingAsynchronousReadsHoldNoThreads()
    {
        var driver = CreateDriver();
        if (driver is null) return;
        using (var first = driver.OpenDevice(0, 22, null))
        {
            first.Clear(1000);
            Assert.Equal("FAKE,GPIB,22,1.0\n", await QueryAsync(first, "*IDN?")); // starts the library's and our threads
        }

        const int count = 150;
        var devices = new List<IGpibDevice>();
        try
        {
            for (int i = 0; i < count; i++) devices.Add(driver.OpenDevice(0, 22, null));
            int before = ThreadCount();
            using var cancellation = new CancellationTokenSource();
            var reads = devices.Select(d => d.ReadAsync(new byte[64], null, Timeout.Infinite, cancellation.Token).AsTask()).ToList();
            await Task.Delay(500);
            int during = ThreadCount();
            Assert.True(during - before < 20, $"{count} pending reads: {before} threads before, {during} during.");
            if (HasAsyncCalls)
            {
                Assert.All(reads, r => Assert.False(r.IsCompleted));
                cancellation.Cancel();
                foreach (var read in reads) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Patience));
            }
            else
            {
                // The worker thread runs them one after the other; with nothing to read each times out at once.
                foreach (var read in reads) await Assert.ThrowsAsync<GpibTimeoutException>(() => read.WaitAsync(Patience));
            }
        }
        finally
        {
            foreach (var device in devices) device.Dispose();
        }
        Assert.Equal(0, Control(UnsynchronizedTransfers));
    }

    [Fact]
    public async Task ComparesTheTerminationCharacterInAllEightBits()
    {
        var driver = CreateDriver();
        if (driver is null) return;
        using var device = driver.OpenDevice(0, 22, null);
        device.Clear(1000);
        // 0x8A matches '\n' (0x0A) in a 7-bit comparison, the driver's default.
        byte[] command = [.. "ECHO? A"u8, 0x8A, (byte)'B', (byte)'\n'];
        device.Write(command, end: true, 1000);
        var buffer = new byte[64];
        var result = device.Read(buffer, (byte)'\n', 1000);
        Assert.Equal(new byte[] { (byte)'A', 0x8A, (byte)'B', (byte)'\n' }, buffer[..result.Count]);
        Assert.True(result.End);

        await device.WriteAsync(command, end: true, 1000).AsTask().WaitAsync(Patience);
        result = await device.ReadAsync(buffer, (byte)'\n', 1000).AsTask().WaitAsync(Patience);
        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void ReportsDriverErrorsAndMisuse()
    {
        var driver = CreateDriver();
        if (driver is null) return;
        using (var absent = driver.OpenDevice(0, 7, null))
        {
            var error = Assert.Throws<GpibException>(() => absent.Write("*IDN?\n"u8, true, 1000));
            Assert.Equal(GpibError.NoListeners, error.Error);
            Assert.Equal(2, error.NativeError); // ENOL
            Assert.Contains("ENOL", error.Message);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.OpenDevice(0, 31, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => driver.OpenDevice(0, 5, 31));

        // Every call on a disposed object throws, so a stale object never reaches a descriptor reused by another device.
        var device = driver.OpenDevice(0, 22, null);
        var board = driver.OpenBoard(0);
        device.Dispose();
        board.Dispose();
        Assert.Throws<ObjectDisposedException>(() => device.Write("*IDN?\n"u8, true, 1000));
        Assert.Throws<ObjectDisposedException>(() => device.Read(new byte[8], null, 1000));
        Assert.Throws<ObjectDisposedException>(() => device.SerialPoll(1000));
        Assert.Throws<ObjectDisposedException>(() => device.ReaddressingEnabled = false);
        Assert.Throws<ObjectDisposedException>(() => device.WaitForServiceRequest(0));
        Assert.Throws<ObjectDisposedException>(() => board.SendCommand([GpibCommand.Unlisten], 1000));
        Assert.Throws<ObjectDisposedException>(() => board.PrimaryAddress);
    }

    [Fact]
    public void ControlsTheBoardDirectly()
    {
        var driver = CreateDriver();
        if (driver is null) return;
        using var board = driver.OpenBoard(0);
        board.SetRemoteEnable(true);
        Assert.True(board.GetLines().IsAsserted(GpibLines.Ren));
        board.SetRemoteEnable(false);
        Assert.False(board.GetLines().IsAsserted(GpibLines.Ren));
        Assert.Equal(GpibAddressState.Unaddressed, board.AddressState);
        Assert.True(board.IsControllerInCharge);
        Assert.Null(board.SecondaryAddress);
        board.SendCommand([GpibCommand.Unlisten, GpibCommand.Listen(22)], 1000);
        board.PassControl(22, null, 1000);
    }

    [Fact]
    public async Task DeviceWaitsSeeRequestsWhileTheDriverPollsAutomatically()
    {
        var driver = CreateDriver();
        if (driver is null) return;
        using var device = driver.OpenDevice(0, 5, null);
        using var board = driver.OpenBoard(0);
        device.Write("*CLS\n"u8, true, 1000);
        Assert.Null(device.WaitForServiceRequest(0));
        Control(HideSrqi, 1); // NI-488.2 with automatic serial polling: no SRQI, RQS on the device
        try
        {
            device.Write("SRQ\n"u8, true, 1000);
            Assert.False(board.WaitForServiceRequest(100));
            Assert.Equal((byte)0x41, device.WaitForServiceRequest(2000));

            device.Write("SRQ\n"u8, true, 1000);
            Assert.Equal((byte)0x41, await device.WaitForServiceRequestAsync(2000).AsTask().WaitAsync(Patience));
        }
        finally
        {
            Control(HideSrqi, 0);
            device.Write("*CLS\n"u8, true, 1000);
        }
    }

    [Fact]
    public async Task DeviceWaitsIgnoreOtherDevicesRequests()
    {
        var driver = CreateDriver();
        if (driver is null) return;
        using var mine = driver.OpenDevice(0, 5, null);
        using var other = driver.OpenDevice(0, 22, null);
        mine.Write("*CLS\n"u8, true, 1000);
        other.Write("SRQ\n"u8, true, 1000); // the other device holds SRQ and nobody polls it
        try
        {
            int polls = Control(SerialPolls);
            var watch = Stopwatch.StartNew();
            Assert.Null(mine.WaitForServiceRequest(500));
            Assert.Null(await mine.WaitForServiceRequestAsync(500).AsTask().WaitAsync(Patience));
            Assert.InRange(watch.ElapsedMilliseconds, 900, 5000);
            // At most one poll of this device per 100 ms while the foreign request stays, not one per check.
            Assert.InRange(Control(SerialPolls) - polls, 1, 16);

            // This device's own request is still recognized while the other one holds SRQ.
            var wait = mine.WaitForServiceRequestAsync(5000).AsTask();
            await Task.Delay(150);
            mine.Write("SRQ\n"u8, true, 1000);
            Assert.Equal((byte)0x41, await wait.WaitAsync(Patience));
        }
        finally
        {
            other.SerialPoll(1000);
            mine.Write("*CLS\n"u8, true, 1000);
        }
    }

    [Fact]
    public void InterfaceSessionsReportEachServiceRequestOnce()
    {
        using var rm = Create(out _);
        if (rm is null) return;
        using var device = rm.Open<GpibSession>("GPIB0::22::INSTR");
        using var board = rm.Open<GpibInterfaceSession>("GPIB0::INTFC");
        device.WriteLine("*CLS");
        board.EnableEvent(EventType.ServiceRequest, EventMechanism.Queue);
        try
        {
            device.WriteLine("SRQ");
            Assert.IsType<ServiceRequestEventArgs>(board.WaitOnEvent(EventType.ServiceRequest, 5000));
            // SRQ stays asserted until the device is polled, but that is still one request (repeated after a second).
            Assert.Throws<VisaTimeoutException>(() => board.WaitOnEvent(EventType.ServiceRequest, 300));
            Assert.IsType<ServiceRequestEventArgs>(board.WaitOnEvent(EventType.ServiceRequest, 5000));
            Assert.Equal(0x41, device.ReadStatusByte());
            Thread.Sleep(200); // the release is seen; the next request is reported at once
            device.WriteLine("SRQ");
            var watch = Stopwatch.StartNew();
            Assert.IsType<ServiceRequestEventArgs>(board.WaitOnEvent(EventType.ServiceRequest, 5000));
            Assert.True(watch.ElapsedMilliseconds < 500, $"The second request took {watch.ElapsedMilliseconds} ms.");
            Assert.Equal(0x41, device.ReadStatusByte());
            device.WriteLine("SRQ"); // requested again right after the poll: reported within about a second
            Assert.IsType<ServiceRequestEventArgs>(board.WaitOnEvent(EventType.ServiceRequest, 5000));
            Assert.Equal(0x41, device.ReadStatusByte());
        }
        finally
        {
            board.DisableEvent(EventType.ServiceRequest);
            device.WriteLine("*CLS");
        }
    }

    [Fact]
    public async Task RecoversArmingsUndoneByCallbackReturns()
    {
        var driver = CreateDriver();
        if (driver is null || !Exports("ibnotify")) return;
        using var device = driver.OpenDevice(0, 22, null);
        device.Clear(1000);
        // The library returns from each callback 5 ms late and then rearms with its return value (0), undoing an
        // ibnotify made in between: the next transfer of a query is armed in exactly that window.
        Control(LateCallbackReturn, 5);
        try
        {
            long slowest = 0;
            for (int i = 0; i < 40 && (i < 10 || Control(UndoneArmings) == 0); i++)
            {
                var watch = Stopwatch.StartNew();
                Assert.Equal($"q{i}\n", await QueryAsync(device, $"ECHO? q{i}"));
                slowest = Math.Max(slowest, watch.ElapsedMilliseconds);
            }
            Assert.True(Control(UndoneArmings) > 0, "The race was never provoked.");
            // Without a new arming the transfer would wait for the watchdog (500 ms or more).
            Assert.True(slowest < 400, $"The slowest query took {slowest} ms.");
        }
        finally
        {
            Control(LateCallbackReturn, 0);
        }
        Assert.Equal(0, Control(UnsynchronizedTransfers));
    }
}

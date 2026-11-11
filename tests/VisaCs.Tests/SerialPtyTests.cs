using System.Runtime.InteropServices;
using System.Text;
using VisaCs.Serial;
using VisaCs.Simulation;

namespace VisaCs.Tests;

/// <summary>
/// Exercises the termios serial implementation against a pseudo-terminal whose master side is
/// driven by a simulated instrument. Linux only (there is no built-in virtual COM port on Windows).
/// </summary>
public sealed unsafe partial class SerialPtyTests : IDisposable
{
    private readonly int _master = -1;
    private readonly string _slave = "";
    private readonly SimulatedInstrument _instrument = new();
    private readonly Thread? _thread;
    private volatile bool _stop;

    public SerialPtyTests()
    {
        if (!OperatingSystem.IsLinux()) return;
        _master = posix_openpt(2 | 0x100); // O_RDWR | O_NOCTTY
        Assert.True(_master >= 0);
        Assert.Equal(0, grantpt(_master));
        Assert.Equal(0, unlockpt(_master));
        _slave = Marshal.PtrToStringUTF8(ptsname(_master))!;
        _thread = new Thread(Serve) { IsBackground = true };
        _thread.Start();
    }

    private void Serve()
    {
        var line = new List<byte>();
        var buffer = new byte[4096];
        while (!_stop)
        {
            nint n;
            fixed (byte* p = buffer) n = read(_master, p, buffer.Length);
            if (n <= 0)
            {
                if (_stop) return;
                Thread.Sleep(5);
                continue;
            }
            for (int i = 0; i < n; i++)
            {
                line.Add(buffer[i]);
                if (buffer[i] != (byte)'\n') continue;
                foreach (var response in _instrument.Process(line.ToArray()))
                {
                    byte[] message = [.. response, (byte)'\n'];
                    fixed (byte* p = message) write(_master, p, message.Length);
                }
                line.Clear();
            }
        }
    }

    [Fact]
    public void TalksToAnInstrumentOverTermios()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var rm = new ResourceManager();
        using var session = rm.Open<SerialSession>($"ASRL{_slave}::INSTR");
        session.TimeoutMilliseconds = 3000;
        Assert.Equal(_slave, session.PortName);
        Assert.Equal(SerialTerminationMethod.TerminationCharacter, session.EndInput);

        session.BaudRate = 115200;
        session.DataBits = 8;
        session.Parity = SerialParity.Even;
        session.Parity = SerialParity.None;
        session.StopBits = SerialStopBitsMode.Two;
        session.FlowControl = SerialFlowControlModes.XOnXOff;
        session.FlowControl = SerialFlowControlModes.None;
        session.BaudRate = 250000; // non-standard rate: termios2 / BOTHER

        Assert.Equal(_instrument.Identity, session.Query("*IDN?"));
        Assert.Equal("x y z", session.Query("ECHO? x y z"));
        Assert.Equal(1.2345, session.Query<double>("MEAS:VOLT?"), 6);
        Assert.Equal(0, session.ReadStatusByte() & 0x41); // emulated with *STB?

        // Binary data containing '\n': the block reader ignores END_IN termination inside the block.
        var block = session.QueryBinaryBlock("DATA:BIN? 300");
        Assert.Equal(300, block.Length);
        Assert.Equal(10, block[10]);
        Assert.Equal(_instrument.Identity, session.Query("*IDN?"));

        session.TimeoutMilliseconds = 200;
        Assert.Throws<VisaTimeoutException>(() => session.ReadLine());
        session.TimeoutMilliseconds = 3000;

        int triggers = _instrument.TriggerCount;
        session.AssertTrigger();
        Assert.True(SpinWait.SpinUntil(() => _instrument.TriggerCount > triggers, 2000));
        Assert.Equal(0, session.BytesAvailable);
        session.Flush(BufferTypes.InBufferDiscard | BufferTypes.OutBuffer);
    }

    [Fact]
    public void SendsTermCharAsEndOutput()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var rm = new ResourceManager();
        using var session = rm.Open<SerialSession>($"ASRL{_slave}::INSTR");
        session.TimeoutMilliseconds = 3000;
        session.EndOutput = SerialTerminationMethod.TerminationCharacter;
        session.Write("ECHO? appended"); // the termination character is added by END_OUT
        Assert.Equal("appended", session.ReadLine());
        Assert.Equal(SerialTerminationMethod.TerminationCharacter,
            session.GetAttribute<SerialTerminationMethod>(VisaAttribute.SerialEndOut));
    }

    [Fact]
    public void DiscardsNullBytes()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var rm = new ResourceManager();
        using var session = rm.Open<SerialSession>($"ASRL{_slave}::INSTR");
        session.TimeoutMilliseconds = 3000;

        byte[] withNulls = [(byte)'A', 0, (byte)'B', 0, 0, (byte)'\n'];
        fixed (byte* p = withNulls) write(_master, p, withNulls.Length);
        Assert.Equal("A\0B\0\0", session.ReadLine());

        session.DiscardNull = true;
        fixed (byte* p = withNulls) write(_master, p, withNulls.Length);
        Assert.Equal("AB", session.ReadLine());

        // termios cannot substitute a character for bytes with parity errors.
        var ex = Assert.Throws<VisaException>(() => session.ReplacementCharacter = (byte)'?');
        Assert.Equal(VisaStatus.ErrorAttributeStateNotSupported, ex.Status);
        Assert.Equal(0, session.ReplacementCharacter);
        Assert.True(session.DiscardNull);
    }

    [Fact]
    public void WorksWithoutTheVisaLayer()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var port = NativeSerialPort.Open(_slave);
        port.Configure(SerialSettings.Default with { BaudRate = 115200 });

        port.Write("*IDN?\n"u8, 1000);
        var response = new List<byte>();
        var buffer = new byte[256];
        while (!response.Contains((byte)'\n'))
            response.AddRange(buffer.AsSpan(0, port.Read(buffer, 2000)).ToArray());
        Assert.Equal(_instrument.Identity + "\n", Encoding.ASCII.GetString(response.ToArray()));

        Assert.Throws<SerialTimeoutException>(() => port.Read(buffer, 50));

        // Abort ends a read that is blocked on another thread.
        Exception? error = null;
        var reader = new Thread(() =>
        {
            try { port.Read(new byte[16], -1); }
            catch (Exception e) { error = e; }
        });
        reader.Start();
        Thread.Sleep(100);
        port.Abort();
        Assert.True(reader.Join(5000));
        Assert.Equal(SerialPortError.Aborted, Assert.IsType<SerialPortException>(error).Error);
    }

    public void Dispose()
    {
        _stop = true;
        if (_master >= 0) close(_master);
        _thread?.Join(1000);
    }

    [LibraryImport("libc.so.6", SetLastError = true)] private static partial int posix_openpt(int flags);
    [LibraryImport("libc.so.6", SetLastError = true)] private static partial int grantpt(int fd);
    [LibraryImport("libc.so.6", SetLastError = true)] private static partial int unlockpt(int fd);
    [LibraryImport("libc.so.6", SetLastError = true)] private static partial nint ptsname(int fd);
    [LibraryImport("libc.so.6", SetLastError = true)] private static partial nint read(int fd, byte* buffer, nint count);
    [LibraryImport("libc.so.6", SetLastError = true)] private static partial nint write(int fd, byte* buffer, nint count);
    [LibraryImport("libc.so.6", SetLastError = true)] private static partial int close(int fd);
}

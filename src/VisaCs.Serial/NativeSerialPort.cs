using VisaCs.Internal;

namespace VisaCs.Serial;

/// <summary>
/// A serial port opened directly through the operating system: the Win32 comm API with overlapped I/O on
/// Windows, termios with poll on Linux and macOS. It does not use System.IO.Ports. Every read and write has its
/// own timeout, and <see cref="Abort"/> ends transfers that are blocked on other threads or pending asynchronously.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Open(string)"/> applies <see cref="SerialSettings.Default"/> (raw 8-bit mode, 9600 baud, 8N1, no flow
/// control) and asserts DTR and RTS, so a port starts in the same state on every platform.
/// </para>
/// <para>
/// Timeouts are in milliseconds: <see cref="Timeout.Infinite"/> (-1, or any negative value) waits without limit, and
/// 0 only takes what can be done at once (a read returns bytes that have already arrived). A transfer that runs out
/// of time throws <see cref="VisaTimeoutException"/>; other errors are <see cref="VisaException"/> with a
/// <see cref="VisaStatus"/> code. A disconnected device (a USB adapter unplugged) gives
/// <see cref="VisaStatus.ErrorConnectionLost"/>. Parity, framing and overrun errors are not reported as exceptions:
/// see <see cref="SerialSettings.ReplacementCharacter"/>.
/// </para>
/// <para>
/// The asynchronous methods hold no thread while they wait: on Windows the port handle is bound to the thread pool's
/// I/O completion port, on Linux and macOS one shared thread polls every waiting descriptor. Cancelling their token
/// ends them with <see cref="OperationCanceledException"/>; data that arrived just then stays in the port for the
/// next read.
/// </para>
/// <para>
/// Thread safety: all members may be called from any thread. Reads and writes are independent, so a read and a write
/// (each synchronous or asynchronous) can be in progress at the same time. Several reads at the same time are
/// allowed but each gets an unpredictable part of the data. <see cref="Abort"/>, <see cref="Dispose"/>, the modem
/// lines, <see cref="Break"/>, the discard methods and <see cref="Configure"/> may be used while transfers are in
/// progress.
/// </para>
/// <para>
/// After <see cref="Dispose"/>, every member except <see cref="Dispose"/>, <see cref="Abort"/>,
/// <see cref="PortName"/> and <see cref="Settings"/> throws <see cref="ObjectDisposedException"/>.
/// </para>
/// </remarks>
public abstract class NativeSerialPort : IDisposable
{
    private readonly Lock _configureLock = new();
    private SerialSettings _settings = SerialSettings.Default;
    private int _bitsPerCharacter = 10;
    private bool _disposed;
    private int _abortGeneration;

    private protected NativeSerialPort(string portName)
    {
        PortName = portName;
    }

    /// <summary>
    /// Opens a serial port by its operating-system name, such as <c>COM3</c> or <c>/dev/ttyUSB0</c>, applies
    /// <see cref="SerialSettings.Default"/> and asserts DTR and RTS.
    /// </summary>
    /// <exception cref="VisaException">
    /// <see cref="VisaStatus.ErrorResourceNotFound"/>: the port does not exist or is not a serial port.
    /// <see cref="VisaStatus.ErrorResourceBusy"/>: the port is open in this or another process (on Windows also when
    /// access is denied). <see cref="VisaStatus.ErrorNoPermission"/>: the user may not open the device (Linux and
    /// macOS). <see cref="VisaStatus.ErrorOperationNotSupported"/>: the operating system is not Windows, Linux or
    /// macOS. <see cref="VisaStatus.ErrorIO"/>: any other failure.
    /// </exception>
    public static NativeSerialPort Open(string portName) => Open(portName, SerialSettings.Default);

    /// <summary>
    /// Opens a serial port by its operating-system name, applies <paramref name="settings"/> and asserts DTR and RTS
    /// (except a line that the chosen flow control drives).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A setting is outside its range (see <see cref="SerialSettings"/>).</exception>
    /// <exception cref="VisaException">
    /// The port cannot be opened (see <see cref="Open(string)"/>) or rejects the settings
    /// (<see cref="VisaStatus.ErrorAttributeStateNotSupported"/>).
    /// </exception>
    public static NativeSerialPort Open(string portName, SerialSettings settings)
    {
        ArgumentNullException.ThrowIfNull(portName);
        settings.Validate();
        NativeSerialPort port;
        if (OperatingSystem.IsWindows()) port = new WindowsSerialPort(portName);
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) port = new UnixSerialPort(portName);
        else throw new VisaException(VisaStatus.ErrorOperationNotSupported, "Serial ports are not supported on this platform.");
        try
        {
            port.Configure(settings);
        }
        catch
        {
            port.Dispose();
            throw;
        }
        return port;
    }

    /// <summary>
    /// Lists the serial ports present, including ports that are in use. Windows: the names under
    /// <c>HKLM\HARDWARE\DEVICEMAP\SERIALCOMM</c> (COM1, COM2, COM10, …). Linux: the <c>/dev</c> nodes of the
    /// <c>/sys/class/tty</c> devices backed by hardware, without the placeholder ports of serial drivers that found
    /// no UART. macOS: the <c>/dev/cu.*</c> call-out devices. Empty on other platforms or when the list cannot be read.
    /// </summary>
    public static IReadOnlyList<string> GetPortNames()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsSerialPort.EnumeratePorts();
            if (OperatingSystem.IsLinux()) return UnixSerialPort.EnumerateLinuxPorts();
            if (OperatingSystem.IsMacOS()) return UnixSerialPort.EnumerateMacPorts();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return Array.Empty<string>();
    }

    /// <summary>The operating-system name the port was opened with.</summary>
    public string PortName { get; }

    /// <summary>The settings last applied with <see cref="Configure"/> (or by <see cref="Open(string)"/>).</summary>
    public SerialSettings Settings
    {
        get
        {
            lock (_configureLock) return _settings;
        }
    }

    /// <summary>
    /// Applies line settings: speed, framing and flow control. It takes effect at once, also for transfers in
    /// progress. If the port rejects the settings, it keeps the previous ones.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A setting is outside its range (see <see cref="SerialSettings"/>).</exception>
    /// <exception cref="VisaException">
    /// The platform or the port does not support a setting or combination
    /// (<see cref="VisaStatus.ErrorAttributeStateNotSupported"/>), for example DTR/DSR flow control on Linux.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The port was disposed.</exception>
    public void Configure(SerialSettings settings)
    {
        settings.Validate();
        ThrowIfDisposed();
        lock (_configureLock)
        {
            ConfigureCore(settings);
            _settings = settings;
            Volatile.Write(ref _bitsPerCharacter, 1 + settings.DataBits + (settings.Parity == SerialParity.None ? 0 : 1) +
                (settings.StopBits == SerialStopBitsMode.One ? 1 : 2));
        }
    }

    /// <summary>
    /// Reads at least one byte into <paramref name="buffer"/> and returns how many were read. Waits up to
    /// <paramref name="timeoutMilliseconds"/> for the first byte (-1: no limit); with 0 it returns bytes that have
    /// already arrived. An empty buffer returns 0 at once.
    /// </summary>
    /// <exception cref="VisaTimeoutException">No byte arrived in time.</exception>
    /// <exception cref="VisaException">
    /// <see cref="Abort"/> was called or the port was disposed during the call (<see cref="VisaStatus.ErrorAbort"/>),
    /// the device was disconnected (<see cref="VisaStatus.ErrorConnectionLost"/>), or an I/O error occurred.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The port was disposed before the call.</exception>
    public int Read(Span<byte> buffer, int timeoutMilliseconds) =>
        buffer.IsEmpty ? 0 : ReadCore(buffer, Deadline.FromMilliseconds(timeoutMilliseconds));

    /// <summary>
    /// Writes all of <paramref name="data"/>, waiting up to <paramref name="timeoutMilliseconds"/> (-1: no limit).
    /// </summary>
    /// <exception cref="VisaTimeoutException">
    /// The data could not be written in time; <see cref="VisaTimeoutException.BytesTransferred"/> tells how much was.
    /// </exception>
    /// <exception cref="VisaException">
    /// <see cref="Abort"/> or <see cref="DiscardOutput"/> (Windows) was called or the port was disposed during the
    /// call (<see cref="VisaStatus.ErrorAbort"/>), the device was disconnected
    /// (<see cref="VisaStatus.ErrorConnectionLost"/>), or an I/O error occurred.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The port was disposed before the call.</exception>
    public void Write(ReadOnlySpan<byte> data, int timeoutMilliseconds)
    {
        if (!data.IsEmpty) WriteCore(data, Deadline.FromMilliseconds(timeoutMilliseconds));
    }

    /// <summary>
    /// Reads at least one byte into <paramref name="buffer"/>, like <see cref="Read"/>, without holding a thread while
    /// waiting. Cancelling <paramref name="cancellationToken"/> ends the call with
    /// <see cref="OperationCanceledException"/>. A cancelled or timed-out read takes no data from the port: bytes
    /// that arrived just as it was cancelled are returned instead.
    /// </summary>
    /// <exception cref="VisaTimeoutException">No byte arrived in time.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="VisaException">
    /// <see cref="Abort"/> was called or the port was disposed during the call (<see cref="VisaStatus.ErrorAbort"/>),
    /// the device was disconnected (<see cref="VisaStatus.ErrorConnectionLost"/>), or an I/O error occurred.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The port was disposed before the call.</exception>
    public ValueTask<int> ReadAsync(Memory<byte> buffer, int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled<int>(cancellationToken);
        if (buffer.IsEmpty) return new ValueTask<int>(0);
        return ReadCoreAsync(buffer, Deadline.FromMilliseconds(timeoutMilliseconds).WithCancellation(cancellationToken));
    }

    /// <summary>
    /// Writes all of <paramref name="data"/>, like <see cref="Write"/>, without holding a thread while waiting.
    /// Cancelling <paramref name="cancellationToken"/> ends the call with <see cref="OperationCanceledException"/>;
    /// part of the data may have been sent, and the exception's <see cref="Exception.InnerException"/> is a
    /// <see cref="VisaTimeoutException"/> whose <see cref="VisaTimeoutException.BytesTransferred"/> tells how much.
    /// </summary>
    /// <exception cref="VisaTimeoutException">
    /// The data could not be written in time; <see cref="VisaTimeoutException.BytesTransferred"/> tells how much was.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="VisaException">
    /// <see cref="Abort"/> or <see cref="DiscardOutput"/> (Windows) was called or the port was disposed during the
    /// call (<see cref="VisaStatus.ErrorAbort"/>), the device was disconnected
    /// (<see cref="VisaStatus.ErrorConnectionLost"/>), or an I/O error occurred.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The port was disposed before the call.</exception>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled(cancellationToken);
        if (data.IsEmpty) return ValueTask.CompletedTask;
        return WriteCoreAsync(data, Deadline.FromMilliseconds(timeoutMilliseconds).WithCancellation(cancellationToken));
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMilliseconds"/> (-1: no limit; 0: only checks) until all written data has
    /// been transmitted. Neither Win32 nor termios can wait for this with a timeout, so it polls the driver's
    /// transmit queue (<c>ClearCommError</c> on Windows, <c>TIOCOUTQ</c> on Linux and macOS, plus the UART's
    /// transmitter-empty flag on Linux where the driver reports it), sleeping between checks for about as long as
    /// the queued bytes take to send (1 to 50 ms). Pseudo-terminals always report an empty queue.
    /// </summary>
    /// <exception cref="VisaTimeoutException">Data was still queued when the time ran out.</exception>
    /// <exception cref="VisaException">
    /// <see cref="Abort"/> was called or the port was disposed during the call (<see cref="VisaStatus.ErrorAbort"/>).
    /// </exception>
    /// <exception cref="ObjectDisposedException">The port was disposed before the call.</exception>
    public void Drain(int timeoutMilliseconds) =>
        AsyncIO.Complete(DrainCoreAsync(Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>
    /// Waits until all written data has been transmitted, like <see cref="Drain"/>, without holding a thread.
    /// Cancelling <paramref name="cancellationToken"/> ends the call with <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <exception cref="VisaTimeoutException">Data was still queued when the time ran out.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="VisaException">
    /// <see cref="Abort"/> was called or the port was disposed during the call (<see cref="VisaStatus.ErrorAbort"/>).
    /// </exception>
    /// <exception cref="ObjectDisposedException">The port was disposed before the call.</exception>
    public ValueTask DrainAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled(cancellationToken);
        return DrainCoreAsync(Deadline.FromMilliseconds(timeoutMilliseconds).WithCancellation(cancellationToken), async: true);
    }

    private async ValueTask DrainCoreAsync(Deadline deadline, bool async)
    {
        ThrowIfDisposed();
        int generation = AbortGeneration;
        int pending;
        while ((pending = PendingOutputBytes) > 0)
        {
            int remaining = deadline.RemainingMilliseconds;
            if (remaining == 0) throw TimedOut(deadline, "Serial drain timed out.");
            // About as long as the queued characters take to send, but at least 1 and at most 50 ms.
            int baud = Math.Max(1, Settings.BaudRate);
            long send = ((long)pending * Volatile.Read(ref _bitsPerCharacter) * 1000 + baud - 1) / baud;
            int delay = (int)Math.Clamp(send, 1, 50);
            if (remaining > 0) delay = Math.Min(delay, remaining);
            if (!async)
            {
                Thread.Sleep(delay);
            }
            else
            {
                try
                {
                    await Task.Delay(delay, deadline.CancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The deadline now reports itself expired; the next round throws.
                }
            }
            // Checked before the queue: an abort also empties it on Windows (the writes it cancels leave the queue).
            ThrowIfInterrupted(generation);
        }
    }

    /// <summary>
    /// Ends every <see cref="Read"/>, <see cref="Write"/>, <see cref="ReadAsync"/>, <see cref="WriteAsync"/>,
    /// <see cref="Drain"/> and <see cref="DrainAsync"/> in progress with <see cref="VisaException"/>
    /// (<see cref="VisaStatus.ErrorAbort"/>). Transfers that start after it returns are not affected. Does nothing
    /// once the port is disposed.
    /// </summary>
    /// <remarks>
    /// Asynchronous calls end at once. A blocked synchronous call ends at once on Windows and within 200 ms on Linux
    /// and macOS, and a drain within 50 ms. Bytes a write had already sent are not reported; use a timeout or the
    /// cancellation token of <see cref="WriteAsync"/> when that matters.
    /// </remarks>
    public void Abort()
    {
        if (IsDisposed) return;
        Interlocked.Increment(ref _abortGeneration);
        AbortCore();
    }

    /// <summary>Number of received bytes waiting to be read.</summary>
    public abstract int BytesAvailable { get; }

    /// <summary>Discards received bytes that have not been read. A read in progress keeps waiting for new data.</summary>
    public abstract void DiscardInput();

    /// <summary>
    /// Discards written bytes that have not been transmitted yet. On Windows a write in progress ends with
    /// <see cref="VisaException"/> (<see cref="VisaStatus.ErrorAbort"/>) and its remaining data is dropped; on Linux
    /// and macOS only the data already queued in the driver is dropped, and a write in progress goes on with the
    /// rest of its data.
    /// </summary>
    public abstract void DiscardOutput();

    /// <summary>
    /// State of the RTS output line; asserted after <see cref="Open(string)"/>. While RTS/CTS flow control is on the
    /// driver drives the line: setting it then throws <see cref="VisaException"/>
    /// (<see cref="VisaStatus.ErrorAttributeStateNotSupported"/>) on Windows and has no lasting effect on Linux and
    /// macOS. On Windows the getter returns the last value set rather than reading the line.
    /// </summary>
    public abstract bool Rts { get; set; }

    /// <summary>
    /// State of the DTR output line; asserted after <see cref="Open(string)"/>. While DTR/DSR flow control is on
    /// (Windows) setting it throws <see cref="VisaException"/> (<see cref="VisaStatus.ErrorAttributeStateNotSupported"/>).
    /// On Windows the getter returns the last value set rather than reading the line.
    /// </summary>
    public abstract bool Dtr { get; set; }

    /// <summary>State of the CTS input line. False when the driver cannot tell (pseudo-terminals).</summary>
    public abstract bool Cts { get; }

    /// <summary>State of the DSR input line. False when the driver cannot tell.</summary>
    public abstract bool Dsr { get; }

    /// <summary>State of the DCD (carrier detect) input line. False when the driver cannot tell.</summary>
    public abstract bool Dcd { get; }

    /// <summary>State of the RI (ring indicator) input line. False when the driver cannot tell.</summary>
    public abstract bool Ri { get; }

    /// <summary>True while a break is sent (the transmit line held in the spacing state). Set it to start or end one.</summary>
    public abstract bool Break { get; set; }

    /// <summary>Suspends (false) or resumes (true) transmission, as if XOFF or XON had been received.</summary>
    public abstract void SetTransmitAllowed(bool allowed);

    /// <summary>
    /// Closes the port. Transfers in progress on other threads and pending asynchronous calls end with a
    /// <see cref="VisaException"/> (<see cref="VisaStatus.ErrorAbort"/>); the OS handle is released when the last
    /// of them has returned, which is at once for asynchronous calls and within 200 ms for a blocked synchronous call
    /// on Linux and macOS. Calling it again does nothing.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, true)) return;
        DisposeCore();
    }

    // ------------------------------------------------------------------ for the platform implementations

    private protected bool IsDisposed => Volatile.Read(ref _disposed);

    /// <summary>Changes with every <see cref="Abort"/>; a transfer that sees it change while it waits was aborted.</summary>
    private protected int AbortGeneration => Volatile.Read(ref _abortGeneration);

    private protected void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    /// <summary>Throws if the port was disposed, or aborted since <paramref name="generation"/> was read.</summary>
    private protected void ThrowIfInterrupted(int generation)
    {
        if (IsDisposed) throw Closed();
        if (AbortGeneration != generation) throw Aborted();
    }

    private protected abstract void ConfigureCore(SerialSettings settings);

    private protected abstract int ReadCore(Span<byte> buffer, Deadline deadline);

    private protected abstract void WriteCore(ReadOnlySpan<byte> data, Deadline deadline);

    private protected abstract ValueTask<int> ReadCoreAsync(Memory<byte> buffer, Deadline deadline);

    private protected abstract ValueTask WriteCoreAsync(ReadOnlyMemory<byte> data, Deadline deadline);

    /// <summary>Ends the transfers in progress; <see cref="AbortGeneration"/> has already changed.</summary>
    private protected abstract void AbortCore();

    /// <summary>Ends the transfers in progress and releases the handle once they have; called once.</summary>
    private protected abstract void DisposeCore();

    /// <summary>Bytes written but not transmitted yet; at least 1 while the transmitter is still sending, if the OS tells.</summary>
    private protected abstract int PendingOutputBytes { get; }

    /// <summary>The exception for a transfer ended by its deadline: a timeout, or cancellation if the caller's token fired.</summary>
    private protected static Exception TimedOut(Deadline deadline, bool reading, int bytesTransferred = 0) =>
        TimedOut(deadline, reading ? "Serial read timed out." : "Serial write timed out.", bytesTransferred);

    private static Exception TimedOut(Deadline deadline, string message, int bytesTransferred = 0)
    {
        var timeout = new VisaTimeoutException(message) { BytesTransferred = bytesTransferred };
        return deadline.IsCanceled ? AsyncIO.Canceled(timeout, deadline.CancellationToken) : timeout;
    }

    private protected static VisaException Aborted() => new(VisaStatus.ErrorAbort, "The serial operation was aborted.");

    private protected static VisaException Closed() => new(VisaStatus.ErrorAbort, "The serial port was closed.");

    private protected static VisaException Disconnected() => new(VisaStatus.ErrorConnectionLost, "The serial device was disconnected.");

    private protected static VisaException NotSupported(string message) => new(VisaStatus.ErrorAttributeStateNotSupported, message);
}

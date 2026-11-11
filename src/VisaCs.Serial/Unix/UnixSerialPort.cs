using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using VisaCs.Internal;

namespace VisaCs.Serial;

/// <summary>
/// Serial port through termios (Linux and macOS). The descriptor is non-blocking: transfers try the read or write
/// and wait with poll when it cannot proceed, the synchronous path in slices of
/// <see cref="SyncPollSliceMilliseconds"/> so that Abort and Dispose are noticed, the asynchronous path through
/// <see cref="UnixPoller"/>.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed unsafe partial class UnixSerialPort : NativeSerialPort
{
    private static readonly bool IsMac = OperatingSystem.IsMacOS();
    private const int SyncPollSliceMilliseconds = 200;

    private readonly DescriptorHandle _handle;
    // Abort and Dispose cancel this source to end the asynchronous waits in progress; Abort then replaces it.
    private readonly Lock _abortLock = new();
    private CancellationTokenSource _abortSource = new();
    private volatile bool _discardNull;
    private volatile bool _break;

    public UnixSerialPort(string path) : base(path)
    {
        NativeLibraries.EnsureRegistered();
        int fd = Libc.open(path, Libc.O_RDWR | Libc.O_NOCTTY | Libc.O_NONBLOCK | Libc.O_CLOEXEC);
        if (fd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            throw errno switch
            {
                Libc.ENOENT or Libc.ENXIO or Libc.ENODEV =>
                    SerialErrors.Create(SerialPortError.NotFound, $"Serial port {path} does not exist.", errno),
                Libc.EACCES => SerialErrors.Create(SerialPortError.AccessDenied,
                    $"Permission denied opening {path} (on most Linux distributions the user must be in the 'dialout' or 'uucp' group).", errno),
                Libc.EBUSY => SerialErrors.Create(SerialPortError.InUse, $"Serial port {path} is busy.", errno),
                _ => SerialErrors.Create(SerialPortError.IOError, $"Cannot open serial port {path} (errno {errno}).", errno),
            };
        }
        _handle = new DescriptorHandle(fd);
        if (Native.isatty(fd) != 1)
        {
            int errno = Marshal.GetLastPInvokeError();
            _handle.Dispose();
            throw SerialErrors.Create(SerialPortError.NotFound, $"{path} is not a serial port (errno {errno}).", errno);
        }
        // Exclusive access, like Windows COM ports (root can still open the device).
        Libc.ioctl(fd, Requests.TIOCEXCL, null);
        // Most drivers assert DTR and RTS on open; make sure. Pseudo-terminals have no modem lines and refuse.
        int lines = TIOCM_DTR | TIOCM_RTS;
        Libc.ioctl(fd, Requests.TIOCMBIS, &lines);
    }

    public static IReadOnlyList<string> EnumerateLinuxPorts()
    {
        var ports = new List<string>();
        const string sysTty = "/sys/class/tty";
        if (!Directory.Exists(sysTty)) return ports;
        foreach (var dir in Directory.EnumerateDirectories(sysTty))
        {
            string name = Path.GetFileName(dir);
            // Virtual consoles and pseudo-terminals have no device behind them.
            if (!Directory.Exists(Path.Combine(dir, "device"))) continue;
            // serial_core ports report their UART type. 0 (PORT_UNKNOWN) is a placeholder without hardware, which the
            // 8250 driver registers whether or not a UART exists.
            string type = Path.Combine(dir, "type");
            if (File.Exists(type) && File.ReadAllText(type).Trim() == "0") continue;
            string node = "/dev/" + name;
            if (Path.Exists(node)) ports.Add(node);
        }
        return ports.Order(StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<string> EnumerateMacPorts() =>
        Directory.GetFiles("/dev", "cu.*").Order(StringComparer.Ordinal).ToList();

    private static SerialPortException Error(string operation)
    {
        int errno = Marshal.GetLastPInvokeError();
        return SerialErrors.Create(SerialPortError.IOError, $"{operation} failed (errno {errno}).", errno);
    }

    /// <summary>
    /// Keeps the descriptor open while it is used, so that its number cannot be reused by another file: Dispose closes
    /// it after the last <see cref="Release"/>. Throws <see cref="ObjectDisposedException"/> once disposed.
    /// </summary>
    private int Acquire()
    {
        ThrowIfDisposed();
        bool added = false;
        _handle.DangerousAddRef(ref added);
        return (int)_handle.DangerousGetHandle();
    }

    private void Release() => _handle.DangerousRelease();

    private int Ioctl(nuint request, void* arg)
    {
        int fd = Acquire();
        try
        {
            return Libc.ioctl(fd, request, arg);
        }
        finally
        {
            Release();
        }
    }

    private static SerialPortException TransferFailed(int errno, bool read) =>
        errno is Libc.EIO or Libc.ENXIO or Libc.ENODEV
            ? SerialErrors.Create(SerialPortError.Disconnected, $"The serial device was disconnected (errno {errno}).", errno)
            : SerialErrors.Create(SerialPortError.IOError, $"Serial {(read ? "read" : "write")} failed (errno {errno}).", errno);

    /// <summary>One non-blocking read: the number of bytes read, or 0 when none are waiting.</summary>
    private int TryRead(int fd, Span<byte> buffer)
    {
        fixed (byte* p = buffer)
        {
            while (true)
            {
                // In raw mode with VMIN = VTIME = 0, read() returns 0 rather than EAGAIN when nothing is waiting, and
                // also once the device was hung up; the poll result tells the two apart (see ThrowIfHungUp).
                nint n = Libc.read(fd, p, buffer.Length);
                if (n > 0 && _discardNull)
                {
                    // termios has no option to drop NUL bytes; do it here (VI_ATTR_ASRL_DISCARD_NULL).
                    int kept = 0;
                    for (int i = 0; i < n; i++)
                        if (p[i] != 0) p[kept++] = p[i];
                    if (kept == 0) continue;
                    n = kept;
                }
                if (n >= 0) return (int)n;
                int errno = Marshal.GetLastPInvokeError();
                if (errno == Libc.EINTR) continue;
                if (errno == Libc.EAGAIN) return 0;
                throw TransferFailed(errno, read: true);
            }
        }
    }

    /// <summary>One non-blocking write: the number of bytes written, or 0 when the output queue is full.</summary>
    private static int TryWrite(int fd, ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
        {
            while (true)
            {
                nint n = Libc.write(fd, p, data.Length);
                if (n >= 0) return (int)n;
                int errno = Marshal.GetLastPInvokeError();
                if (errno == Libc.EINTR) continue;
                if (errno == Libc.EAGAIN) return 0;
                throw TransferFailed(errno, read: false);
            }
        }
    }

    /// <summary>
    /// Called when a read or write made no progress after a wait that returned <paramref name="revents"/>. A hung-up
    /// terminal (a USB adapter unplugged, the master side of a pseudo-terminal closed) keeps reporting that it is
    /// ready while read() returns 0, so a hang-up or error reported by poll ends the transfer.
    /// </summary>
    private static void ThrowIfHungUp(short revents)
    {
        if ((revents & (Libc.POLLHUP | Libc.POLLERR | Libc.POLLNVAL)) != 0) throw Disconnected();
    }

    /// <summary>Waits on the synchronous path, polling in slices so that Abort and Dispose are noticed.</summary>
    private short WaitFor(int fd, short events, Deadline deadline, int generation, bool reading, int bytesTransferred)
    {
        while (true)
        {
            ThrowIfInterrupted(generation);
            if (deadline.RemainingMilliseconds == 0) throw TimedOut(deadline, reading, bytesTransferred);
            var pfd = new Libc.PollFd { fd = fd, events = events };
            int rc = Libc.poll(&pfd, 1, deadline.Slice(SyncPollSliceMilliseconds));
            if (rc < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                if (errno == Libc.EINTR) continue;
                throw SerialErrors.Create(SerialPortError.IOError, $"poll failed (errno {errno}).", errno);
            }
            if (rc > 0) return pfd.revents;
        }
    }

    private protected override int ReadCore(Span<byte> buffer, Deadline deadline)
    {
        int generation = AbortGeneration;
        int fd = Acquire();
        try
        {
            short revents = 0;
            while (true)
            {
                int n = TryRead(fd, buffer);
                if (n > 0) return n;
                ThrowIfHungUp(revents);
                revents = WaitFor(fd, Libc.POLLIN, deadline, generation, reading: true, 0);
            }
        }
        finally
        {
            Release();
        }
    }

    private protected override void WriteCore(ReadOnlySpan<byte> data, Deadline deadline)
    {
        int generation = AbortGeneration;
        int fd = Acquire();
        try
        {
            short revents = 0;
            int offset = 0;
            while (offset < data.Length)
            {
                int n = TryWrite(fd, data[offset..]);
                offset += n;
                if (n > 0) continue;
                ThrowIfHungUp(revents);
                revents = WaitFor(fd, Libc.POLLOUT, deadline, generation, reading: false, offset);
            }
        }
        finally
        {
            Release();
        }
    }

    /// <summary>The token the asynchronous transfers starting now wait with; Abort and Dispose cancel it.</summary>
    private CancellationToken AbortToken
    {
        get
        {
            lock (_abortLock) return _abortSource.Token;
        }
    }

    private protected override void AbortCore()
    {
        CancellationTokenSource aborted;
        lock (_abortLock)
        {
            if (IsDisposed) return;
            aborted = _abortSource;
            _abortSource = new CancellationTokenSource();
        }
        // Synchronous transfers notice the new AbortGeneration at their next poll slice.
        aborted.Cancel();
    }

    private protected override void DisposeCore()
    {
        CancellationTokenSource abort;
        lock (_abortLock) abort = _abortSource;
        // Pending asynchronous waits end now, synchronous ones at their next poll slice; all report the port as
        // closed. The descriptor is closed when the last of them has released it.
        abort.Cancel();
        _handle.Dispose();
    }

    private const int TIOCM_DTR = 0x002, TIOCM_RTS = 0x004, TIOCM_CTS = 0x020, TIOCM_CAR = 0x040, TIOCM_RNG = 0x080, TIOCM_DSR = 0x100;

    /// <summary>The modem lines; 0 when the driver cannot tell (pseudo-terminals).</summary>
    private int ModemBits
    {
        get
        {
            int bits = 0;
            return Ioctl(Requests.TIOCMGET, &bits) == 0 ? bits : 0;
        }
    }

    private void SetModemBit(int bit, bool value)
    {
        if (Ioctl(value ? Requests.TIOCMBIS : Requests.TIOCMBIC, &bit) != 0) throw Error("Setting a modem line");
    }

    public override int BytesAvailable
    {
        get
        {
            int count = 0;
            return Ioctl(Requests.FIONREAD, &count) == 0 ? count : 0;
        }
    }

    private protected override int PendingOutputBytes
    {
        get
        {
            int fd = Acquire();
            try
            {
                int queued = 0;
                if (Libc.ioctl(fd, Requests.TIOCOUTQ, &queued) != 0) return 0; // on error, like tcdrain's, done
                if (queued == 0 && !IsMac)
                {
                    // The UART may still be shifting out what left the queue. serial_core drivers tell through
                    // TIOCSERGETLSR (TIOCSER_TEMT); others (USB adapters, pseudo-terminals) reject the request.
                    int lsr = 0;
                    if (Libc.ioctl(fd, Requests.TIOCSERGETLSR, &lsr) == 0 && (lsr & 1) == 0) return 1;
                }
                return queued;
            }
            finally
            {
                Release();
            }
        }
    }

    public override void DiscardInput() => Flush(Requests.TCIFLUSH);

    public override void DiscardOutput() => Flush(Requests.TCOFLUSH);

    private void Flush(int queue)
    {
        int fd = Acquire();
        try
        {
            Native.tcflush(fd, queue);
        }
        finally
        {
            Release();
        }
    }

    public override bool Rts
    {
        get => (ModemBits & TIOCM_RTS) != 0;
        set => SetModemBit(TIOCM_RTS, value);
    }

    public override bool Dtr
    {
        get => (ModemBits & TIOCM_DTR) != 0;
        set => SetModemBit(TIOCM_DTR, value);
    }

    public override bool Cts => (ModemBits & TIOCM_CTS) != 0;
    public override bool Dsr => (ModemBits & TIOCM_DSR) != 0;
    public override bool Dcd => (ModemBits & TIOCM_CAR) != 0;
    public override bool Ri => (ModemBits & TIOCM_RNG) != 0;

    public override bool Break
    {
        get
        {
            ThrowIfDisposed();
            return _break;
        }
        set
        {
            if (Ioctl(value ? Requests.TIOCSBRK : Requests.TIOCCBRK, null) != 0) throw Error(value ? "TIOCSBRK" : "TIOCCBRK");
            _break = value;
        }
    }

    public override void SetTransmitAllowed(bool allowed)
    {
        int fd = Acquire();
        try
        {
            Native.tcflow(fd, allowed ? Requests.TCOON : Requests.TCOOFF);
        }
        finally
        {
            Release();
        }
    }

    /// <summary>The descriptor, closed when Dispose has been called and no transfer uses it any more.</summary>
    private sealed class DescriptorHandle : SafeHandleMinusOneIsInvalid
    {
        public DescriptorHandle(int fd) : base(ownsHandle: true) => SetHandle(fd);

        protected override bool ReleaseHandle() => Libc.close((int)handle) == 0;
    }
}

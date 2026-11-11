using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using VisaCs.Internal;

namespace VisaCs.Serial;

/// <summary>
/// Serial port through the Win32 comm API. The handle is opened for overlapped I/O and bound to the thread pool's
/// completion port: asynchronous transfers complete there, synchronous ones wait on an event of their own whose
/// handle has the low-order bit set, which keeps their completion off the completion port.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WindowsSerialPort : NativeSerialPort
{
    private readonly CommHandle _handle;
    // The comm API cannot read back the RTS and DTR outputs; Configure needs them to rebuild the DCB.
    private volatile bool _rts = true;
    private volatile bool _dtr = true;
    private volatile bool _break;

    public WindowsSerialPort(string portName) : base(portName)
    {
        string path = portName.StartsWith(@"\\.\", StringComparison.Ordinal) ? portName : @"\\.\" + portName;
        nint handle = Native.CreateFileW(path, Native.GENERIC_READ | Native.GENERIC_WRITE, 0, 0, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, 0);
        if (handle == -1)
        {
            int error = Marshal.GetLastPInvokeError();
            throw error switch
            {
                Native.ERROR_FILE_NOT_FOUND or Native.ERROR_PATH_NOT_FOUND =>
                    SerialErrors.Create(SerialPortError.NotFound, $"Serial port {portName} does not exist.", error),
                Native.ERROR_ACCESS_DENIED or Native.ERROR_SHARING_VIOLATION =>
                    SerialErrors.Create(SerialPortError.InUse, $"Serial port {portName} is in use or access is denied.", error),
                _ => SerialErrors.Create(SerialPortError.IOError, $"Cannot open serial port {portName} (Win32 error {error}).", error),
            };
        }
        _handle = new CommHandle(handle);

        Native.SetupComm(_handle, 64 * 1024, 64 * 1024);
        // Reads return as soon as at least one byte is available and otherwise wait for the first byte for about 49
        // days, which counts as without limit. Deadlines, cancellation and Abort end them with CancelIoEx.
        var timeouts = new Native.COMMTIMEOUTS
        {
            ReadIntervalTimeout = uint.MaxValue,
            ReadTotalTimeoutMultiplier = uint.MaxValue,
            ReadTotalTimeoutConstant = uint.MaxValue - 1,
            WriteTotalTimeoutMultiplier = 0,
            WriteTotalTimeoutConstant = 0,
        };
        if (!Native.SetCommTimeouts(_handle, &timeouts))
        {
            int error = Marshal.GetLastPInvokeError();
            _handle.Dispose();
            throw SerialErrors.Create(SerialPortError.NotFound, $"{portName} is not a serial port (Win32 error {error}).", error);
        }

        try
        {
            _handle.Binding = ThreadPoolBoundHandle.BindHandle(_handle);
        }
        catch
        {
            _handle.Dispose();
            throw;
        }
        Native.PurgeComm(_handle, Native.PURGE_RXCLEAR | Native.PURGE_TXCLEAR);
        Native.ClearCommError(_handle, out _, null);
    }

    public static IReadOnlyList<string> EnumeratePorts()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
        if (key is null) return Array.Empty<string>();
        return key.GetValueNames()
            .Select(n => key.GetValue(n) as string)
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v.Length).ThenBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private protected override void ConfigureCore(SerialSettings s)
    {
        Native.DCB dcb;
        dcb.DCBlength = (uint)sizeof(Native.DCB);
        if (!Native.GetCommState(_handle, &dcb)) throw Error("GetCommState");

        dcb.BaudRate = (uint)s.BaudRate;
        dcb.ByteSize = (byte)s.DataBits;
        dcb.Parity = (byte)s.Parity; // NOPARITY=0 ODD=1 EVEN=2 MARK=3 SPACE=4, same as VISA
        dcb.StopBits = s.StopBits switch
        {
            SerialStopBitsMode.One => 0,
            SerialStopBitsMode.OneAndOneHalf => 1,
            _ => 2,
        };
        dcb.XonChar = s.XOnCharacter;
        dcb.XoffChar = s.XOffCharacter;
        dcb.ErrorChar = s.ReplacementCharacter;
        dcb.XonLim = 2048;
        dcb.XoffLim = 512;

        // fAbortOnError stays off: line errors do not fail transfers (the comm API would then refuse every transfer
        // until ClearCommError), and errored bytes arrive as received or as ErrorChar.
        uint flags = 1; // fBinary
        if (s.Parity != SerialParity.None) flags |= 1 << 1; // fParity
        bool rtsCts = s.FlowControl.HasFlag(SerialFlowControlModes.RtsCts);
        bool dtrDsr = s.FlowControl.HasFlag(SerialFlowControlModes.DtrDsr);
        bool xon = s.FlowControl.HasFlag(SerialFlowControlModes.XOnXOff);
        if (rtsCts) flags |= 1 << 2;                       // fOutxCtsFlow
        if (dtrDsr) flags |= 1 << 3;                       // fOutxDsrFlow
        flags |= (uint)(dtrDsr ? 2 : _dtr ? 1 : 0) << 4;  // fDtrControl: handshake, enable, disable
        flags |= 1 << 7;                                  // fTXContinueOnXoff
        if (xon) flags |= (1 << 8) | (1 << 9);            // fOutX, fInX
        if (s.ReplacementCharacter != 0 && s.Parity != SerialParity.None) flags |= 1 << 10; // fErrorChar
        if (s.DiscardNull) flags |= 1 << 11;              // fNull
        flags |= (uint)(rtsCts ? 2 : _rts ? 1 : 0) << 12; // fRtsControl: handshake, enable, disable
        dcb.Flags = flags;

        if (!Native.SetCommState(_handle, &dcb))
            throw NotSupported($"The serial port rejected the settings (baud {s.BaudRate}, {s.DataBits} data bits, {s.Parity}, {s.StopBits}).");
    }

    private static SerialPortException Error(string operation)
    {
        int error = Marshal.GetLastPInvokeError();
        return SerialErrors.Create(SerialPortError.IOError, $"{operation} failed (Win32 error {error}).", error);
    }

    /// <summary>
    /// Keeps the handle open while a transfer uses its raw value: Dispose closes it after the last
    /// <see cref="SafeHandle.DangerousRelease"/>. Throws <see cref="ObjectDisposedException"/> once disposed.
    /// </summary>
    private nint Acquire()
    {
        ThrowIfDisposed();
        bool added = false;
        _handle.DangerousAddRef(ref added);
        return _handle.DangerousGetHandle();
    }

    /// <summary>
    /// One ReadFile or WriteFile of the synchronous path, waited for with <paramref name="evt"/>. The OVERLAPPED
    /// lives on this stack frame: the method returns only once the transfer has finished, also after a timeout.
    /// </summary>
    private int Transfer(nint handle, nint evt, byte* buffer, int count, bool read, Deadline deadline, int generation,
        out int error, out bool timedOut)
    {
        NativeOverlapped overlapped = default;
        // The low-order bit keeps the completion off the thread pool's completion port.
        overlapped.EventHandle = evt | 1;
        timedOut = false;
        error = 0;
        bool ok = read
            ? Native.ReadFile(handle, buffer, (uint)count, null, &overlapped)
            : Native.WriteFile(handle, buffer, (uint)count, null, &overlapped);
        if (!ok)
        {
            error = Marshal.GetLastPInvokeError();
            if (error != Native.ERROR_IO_PENDING) return 0;
            // Dispose and Abort cancel the transfers in progress; this one may have started just after.
            Interlocked.MemoryBarrier();
            if (IsDisposed || AbortGeneration != generation) Native.CancelIoEx(handle, &overlapped);
            int remaining = deadline.RemainingMilliseconds;
            if (Native.WaitForSingleObject(evt, remaining < 0 ? Native.INFINITE : (uint)remaining) != Native.WAIT_OBJECT_0)
            {
                // Timed out: cancel, then wait below for the cancellation so the buffer is no longer in use.
                timedOut = true;
                Native.CancelIoEx(handle, &overlapped);
            }
        }

        uint transferred = 0;
        error = Native.GetOverlappedResult(handle, &overlapped, &transferred, true) ? 0 : Marshal.GetLastPInvokeError();
        return (int)transferred;
    }

    private static nint CreateTransferEvent()
    {
        nint evt = Native.CreateEventW(0, true, false, 0);
        if (evt == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            throw SerialErrors.Create(SerialPortError.SystemError, $"CreateEvent failed (Win32 error {error}).", error);
        }
        return evt;
    }

    /// <summary>What one read transfer means: the bytes to return, 0 to read again, or an exception.</summary>
    private int ReadResult(int transferred, int error, bool timedOut, Deadline deadline)
    {
        // Bytes that arrived are returned even when the read was cancelled at the same moment, so none are lost.
        if (transferred > 0 && error is 0 or Native.ERROR_OPERATION_ABORTED) return transferred;
        if (error != 0) throw TransferFailed(error, read: true, timedOut, deadline, 0);
        if (timedOut || deadline.HasExpired) throw TimedOut(deadline, reading: true);
        return 0;
    }

    /// <summary>Checks the outcome of one write transfer; <paramref name="offset"/> already counts its bytes.</summary>
    private void CheckWrite(int offset, int length, int error, bool timedOut, Deadline deadline)
    {
        if (error != 0) throw TransferFailed(error, read: false, timedOut, deadline, offset);
        if (offset < length && (timedOut || deadline.HasExpired)) throw TimedOut(deadline, reading: false, offset);
    }

    private Exception TransferFailed(int error, bool read, bool timedOut, Deadline deadline, int bytesTransferred)
    {
        if (error == Native.ERROR_OPERATION_ABORTED)
        {
            // Cancelled by the deadline or the caller's token, by Dispose, or by Abort or DiscardOutput.
            if (timedOut || deadline.HasExpired) return TimedOut(deadline, read, bytesTransferred);
            return IsDisposed ? Closed() : Aborted();
        }
        // What drivers return once the device is gone differs; these are the usual ones (not tested with hardware).
        if (error is Native.ERROR_ACCESS_DENIED or Native.ERROR_BAD_COMMAND or Native.ERROR_GEN_FAILURE or
            Native.ERROR_NO_SUCH_DEVICE or Native.ERROR_DEVICE_NOT_CONNECTED or Native.ERROR_DEVICE_REMOVED)
            return SerialErrors.Create(SerialPortError.Disconnected, $"The serial device was disconnected (Win32 error {error}).", error);
        return SerialErrors.Create(SerialPortError.IOError, $"Serial {(read ? "read" : "write")} failed (Win32 error {error}).", error);
    }

    private protected override int ReadCore(Span<byte> buffer, Deadline deadline)
    {
        int generation = AbortGeneration;
        nint handle = Acquire();
        nint evt = 0;
        try
        {
            evt = CreateTransferEvent();
            fixed (byte* p = buffer)
            {
                while (true)
                {
                    int transferred = Transfer(handle, evt, p, buffer.Length, read: true, deadline, generation, out int error, out bool timedOut);
                    int n = ReadResult(transferred, error, timedOut, deadline);
                    if (n > 0) return n;
                }
            }
        }
        finally
        {
            if (evt != 0) Native.CloseHandle(evt);
            _handle.DangerousRelease();
        }
    }

    private protected override void WriteCore(ReadOnlySpan<byte> data, Deadline deadline)
    {
        int generation = AbortGeneration;
        nint handle = Acquire();
        nint evt = 0;
        try
        {
            evt = CreateTransferEvent();
            fixed (byte* p = data)
            {
                int offset = 0;
                while (offset < data.Length)
                {
                    offset += Transfer(handle, evt, p + offset, data.Length - offset, read: false, deadline, generation, out int error, out bool timedOut);
                    CheckWrite(offset, data.Length, error, timedOut, deadline);
                }
            }
        }
        finally
        {
            if (evt != 0) Native.CloseHandle(evt);
            _handle.DangerousRelease();
        }
    }

    private protected override void AbortCore()
    {
        bool added = false;
        try
        {
            _handle.DangerousAddRef(ref added);
            Native.CancelIoEx(_handle.DangerousGetHandle(), null);
        }
        catch (ObjectDisposedException)
        {
            // Disposed: that has ended every transfer already.
        }
        finally
        {
            if (added) _handle.DangerousRelease();
        }
    }

    private uint QueueStatus(bool output)
    {
        ThrowIfDisposed();
        Native.COMSTAT stat;
        if (!Native.ClearCommError(_handle, out _, &stat)) return 0;
        return output ? stat.cbOutQue : stat.cbInQue;
    }

    public override int BytesAvailable => (int)QueueStatus(output: false);

    private protected override int PendingOutputBytes => (int)QueueStatus(output: true);

    public override void DiscardInput()
    {
        ThrowIfDisposed();
        Native.PurgeComm(_handle, Native.PURGE_RXCLEAR);
    }

    public override void DiscardOutput()
    {
        ThrowIfDisposed();
        // Written data waits in the pending WriteFile calls, so they are aborted too.
        Native.PurgeComm(_handle, Native.PURGE_TXCLEAR | Native.PURGE_TXABORT);
    }

    public override bool Rts
    {
        get
        {
            ThrowIfDisposed();
            return _rts;
        }
        set
        {
            ThrowIfDisposed();
            if (!Native.EscapeCommFunction(_handle, value ? Native.SETRTS : Native.CLRRTS))
                throw NotSupported("RTS cannot be changed while RTS/CTS flow control is enabled.");
            _rts = value;
        }
    }

    public override bool Dtr
    {
        get
        {
            ThrowIfDisposed();
            return _dtr;
        }
        set
        {
            ThrowIfDisposed();
            if (!Native.EscapeCommFunction(_handle, value ? Native.SETDTR : Native.CLRDTR))
                throw NotSupported("DTR cannot be changed while DTR/DSR flow control is enabled.");
            _dtr = value;
        }
    }

    private uint ModemStatus
    {
        get
        {
            ThrowIfDisposed();
            return Native.GetCommModemStatus(_handle, out uint status) ? status : 0;
        }
    }

    public override bool Cts => (ModemStatus & Native.MS_CTS_ON) != 0;
    public override bool Dsr => (ModemStatus & Native.MS_DSR_ON) != 0;
    public override bool Ri => (ModemStatus & Native.MS_RING_ON) != 0;
    public override bool Dcd => (ModemStatus & Native.MS_RLSD_ON) != 0;

    public override bool Break
    {
        get
        {
            ThrowIfDisposed();
            return _break;
        }
        set
        {
            ThrowIfDisposed();
            bool ok = value ? Native.SetCommBreak(_handle) : Native.ClearCommBreak(_handle);
            if (!ok) throw Error(value ? "SetCommBreak" : "ClearCommBreak");
            _break = value;
        }
    }

    public override void SetTransmitAllowed(bool allowed)
    {
        ThrowIfDisposed();
        Native.EscapeCommFunction(_handle, allowed ? Native.SETXON : Native.SETXOFF);
    }

    private protected override void DisposeCore()
    {
        // Transfers in progress end with an error (synchronous ones also check IsDisposed after starting). Only this
        // method closes the handle, so its raw value is still valid here.
        Native.CancelIoEx(_handle.DangerousGetHandle(), null);
        _handle.Dispose();
    }

    /// <summary>
    /// The port handle and its thread-pool binding. Transfers hold a reference, so Dispose closes the handle only
    /// after the last transfer has finished with it.
    /// </summary>
    private sealed class CommHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ThreadPoolBoundHandle? Binding;

        public CommHandle(nint handle) : base(ownsHandle: true) => SetHandle(handle);

        protected override bool ReleaseHandle()
        {
            Binding?.Dispose();
            return Native.CloseHandle(handle);
        }
    }
}

using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VisaCs.Internal;

namespace VisaCs.Usb;

public sealed partial class UsbFsBackend
{
    /// <summary>
    /// An opened usbfs device. Every transfer is a URB (USBDEVFS_SUBMITURB). usbfs reports POLLOUT while completed
    /// URBs wait to be reaped, so one pump per handle awaits that on the shared poll thread and reaps every completed
    /// URB for its waiter; a synchronous transfer blocks on its URB's completion. URBs have no timeout of their own:
    /// at the deadline, when the caller cancels, or when the handle is disposed, the URB is discarded
    /// (USBDEVFS_DISCARDURB) and the waiter still waits until it is reaped.
    /// </summary>
    /// <remarks>
    /// The kernel reads a URB's memory in SUBMITURB and writes it (status, length and IN data) only in REAPURB, both
    /// issued under <see cref="_urbSync"/>. A URB's memory is therefore released only once it was reaped, or once the
    /// handle stopped reaping for good (disposed, or the device is gone); pending URBs are then killed by the kernel
    /// when the interface is released or the descriptor closed. Since Linux 3.3 a bulk URB is only limited by the
    /// usbfs memory limit (usbfs_memory_mb, 16 MiB by default), so transfers are not split.
    /// </remarks>
    private sealed class UsbFsDeviceHandle : IUsbDeviceHandle
    {
        private const int ENOENT = 2, EINVAL = 22, ENOTTY = 25, ENODATA = 61, ECONNRESET = 104;

        private readonly SafeFileHandle _file; // owns the descriptor: closing waits for a ClearHalt in progress
        private readonly int _fd;
        private readonly int _interface;
        private readonly bool _reattachDriver;
        private volatile bool _disposed;

        private readonly Lock _urbSync = new();
        private readonly Dictionary<nint, Urb> _urbs = new(); // submitted and not yet reaped, by native address
        private readonly CancellationTokenSource _pumpStop = new();
        private bool _pumping;
        private bool _broken; // the device is gone or reaping failed: no more submits or reaps

        public unsafe UsbFsDeviceHandle(UsbDeviceInfo device, int interfaceNumber)
        {
            NativeLibraries.EnsureRegistered();
            Device = device;
            Interface = device.Interfaces.FirstOrDefault(i => i.Number == interfaceNumber)
                ?? throw UsbErrors.NotFound($"Interface {interfaceNumber} of {device} not found.");
            _interface = interfaceNumber;

            _fd = Libc.open(device.Location, Libc.O_RDWR | Libc.O_CLOEXEC);
            if (_fd < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                throw UsbErrors.Create(errno == Libc.EACCES ? UsbError.AccessDenied : UsbError.NotFound,
                    $"Cannot open {device.Location} (errno {errno}). Add a udev rule granting access to the device.", errno);
            }
            _file = new SafeFileHandle(_fd, ownsHandle: true);
            try
            {
                _reattachDriver = Claim(interfaceNumber);
            }
            catch
            {
                _file.Dispose();
                throw;
            }
        }

        public UsbDeviceInfo Device { get; }

        public UsbInterfaceInfo Interface { get; }

        /// <summary>
        /// Claims the interface, detaching a kernel driver if one is bound (but never another program's usbfs
        /// claim). Returns true when a driver was detached and must be reattached on Dispose.
        /// </summary>
        private unsafe bool Claim(int interfaceNumber)
        {
            uint ifnum = (uint)interfaceNumber;
            if (Libc.ioctl(_fd, Ioctl.ClaimInterface, &ifnum) == 0) return false;
            int errno = Marshal.GetLastPInvokeError();
            if (errno != Libc.EBUSY) throw ClaimError(errno, interfaceNumber);

            string? driver = BoundDriver(ifnum);
            if (driver == "usbfs")
                throw UsbErrors.Busy($"USB interface {interfaceNumber} is in use by another program.");

            // Detach the kernel driver and claim in one step, unless another program claimed the interface meanwhile.
            var request = new Ioctl.DisconnectClaimRequest { @interface = ifnum, flags = Ioctl.DisconnectClaimExceptDriver };
            "usbfs"u8.CopyTo(new Span<byte>(request.driver, Ioctl.DriverNameSize));
            if (Libc.ioctl(_fd, Ioctl.DisconnectClaim, &request) == 0) return driver is not null;
            errno = Marshal.GetLastPInvokeError();
            if (errno == ENOTTY && driver is not null)
            {
                // Linux before 3.8: detach, then claim.
                var command = new Ioctl.UsbDevFsIoctl { ifno = interfaceNumber, ioctl_code = (int)Ioctl.Disconnect, data = 0 };
                Libc.ioctl(_fd, Ioctl.IoctlCommand, &command);
                if (Libc.ioctl(_fd, Ioctl.ClaimInterface, &ifnum) == 0) return true;
                errno = Marshal.GetLastPInvokeError();
            }
            if (driver is not null) Reattach(interfaceNumber); // in case the driver was detached before the claim failed
            throw ClaimError(errno, interfaceNumber);
        }

        /// <summary>The driver bound to an interface ("usbfs" when a program claimed it), or null if none is.</summary>
        private unsafe string? BoundDriver(uint ifnum)
        {
            var request = new Ioctl.GetDriver { @interface = ifnum };
            if (Libc.ioctl(_fd, Ioctl.GetDriverName, &request) != 0) return null; // ENODATA: no driver
            var name = new ReadOnlySpan<byte>(request.driver, Ioctl.DriverNameSize);
            int end = name.IndexOf((byte)0);
            return System.Text.Encoding.ASCII.GetString(end < 0 ? name : name[..end]);
        }

        private unsafe void Reattach(int interfaceNumber)
        {
            var command = new Ioctl.UsbDevFsIoctl { ifno = interfaceNumber, ioctl_code = (int)Ioctl.Connect, data = 0 };
            Libc.ioctl(_fd, Ioctl.IoctlCommand, &command);
        }

        private static UsbException ClaimError(int errno, int interfaceNumber) => errno switch
        {
            Libc.EBUSY => UsbErrors.Busy($"USB interface {interfaceNumber} is in use by another program.", errno),
            ENOENT or EINVAL or Libc.ENODEV => UsbErrors.NotFound($"USB interface {interfaceNumber} not found (errno {errno}).", errno),
            Libc.EACCES => UsbErrors.AccessDenied($"No permission to claim USB interface {interfaceNumber}.", errno),
            _ => UsbErrors.Busy($"Cannot claim USB interface {interfaceNumber} (errno {errno}).", errno),
        };

        private static Exception Error(int errno, string operation) => errno switch
        {
            Libc.ETIMEDOUT => UsbErrors.TimedOut(operation),
            Libc.ENODEV or Libc.ESHUTDOWN => UsbErrors.Disconnected(operation, errno),
            Libc.EPIPE => UsbErrors.Stalled(operation, errno),
            _ => UsbErrors.Failed(operation, $"errno {errno}", errno),
        };

        // ------------------------------------------------------------------ transfers

        public int ControlTransfer(byte requestType, byte request, ushort value, ushort index, Span<byte> buffer, int timeoutMilliseconds)
        {
            UsbTransferHelpers.CheckControlLength(buffer.Length);
            bool input = (requestType & 0x80) != 0;
            var urb = Urb.Control(this, requestType, request, value, index, buffer.Length, input ? default : buffer);
            try
            {
                int n = Run(urb, timeoutMilliseconds, "control transfer");
                if (input) urb.ControlData(n).CopyTo(buffer);
                return n;
            }
            finally
            {
                urb.Free();
            }
        }

        public int BulkTransfer(byte endpoint, Span<byte> buffer, int timeoutMilliseconds) =>
            PipeTransfer(endpoint, buffer, timeoutMilliseconds);

        public int InterruptTransfer(byte endpoint, Span<byte> buffer, int timeoutMilliseconds) =>
            PipeTransfer(endpoint, buffer, timeoutMilliseconds);

        private unsafe int PipeTransfer(byte endpoint, Span<byte> buffer, int timeoutMilliseconds)
        {
            // The buffer stays fixed until the URB was reaped or abandoned: Run does not return before.
            fixed (byte* p = buffer)
            {
                var urb = new Urb(this, UrbType(endpoint), endpoint, p, buffer.Length, default);
                try
                {
                    return Run(urb, timeoutMilliseconds, UsbTransferHelpers.OperationName(endpoint));
                }
                finally
                {
                    urb.Free();
                }
            }
        }

        public async ValueTask<int> ControlTransferAsync(byte requestType, byte request, ushort value, ushort index, Memory<byte> buffer,
            int timeoutMilliseconds, CancellationToken cancellationToken = default)
        {
            UsbTransferHelpers.CheckControlLength(buffer.Length);
            bool input = (requestType & 0x80) != 0;
            var urb = Urb.Control(this, requestType, request, value, index, buffer.Length, input ? default : buffer.Span);
            try
            {
                int n = await RunAsync(urb, timeoutMilliseconds, "control transfer", cancellationToken).ConfigureAwait(false);
                if (input) urb.ControlData(n).CopyTo(buffer.Span);
                return n;
            }
            finally
            {
                urb.Free();
            }
        }

        public ValueTask<int> BulkTransferAsync(byte endpoint, Memory<byte> buffer, int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
            PipeTransferAsync(endpoint, buffer, timeoutMilliseconds, cancellationToken);

        public ValueTask<int> InterruptTransferAsync(byte endpoint, Memory<byte> buffer, int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
            PipeTransferAsync(endpoint, buffer, timeoutMilliseconds, cancellationToken);

        private async ValueTask<int> PipeTransferAsync(byte endpoint, Memory<byte> buffer, int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            var urb = Urb.Pinned(this, UrbType(endpoint), endpoint, buffer);
            try
            {
                return await RunAsync(urb, timeoutMilliseconds, UsbTransferHelpers.OperationName(endpoint), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                urb.Free();
            }
        }

        /// <summary>The URB type matching the endpoint, so either method works on either endpoint type.</summary>
        private byte UrbType(byte endpoint) =>
            Interface.Endpoints.Any(e => e.Address == endpoint && e.Type == UsbTransferType.Interrupt) ? Ioctl.UrbTypeInterrupt : Ioctl.UrbTypeBulk;

        /// <summary>Submits a URB and blocks until it was reaped (or abandoned); discards it at the timeout.</summary>
        private int Run(Urb urb, int timeoutMilliseconds, string operation)
        {
            Submit(urb, operation);
            if (!UsbTransferHelpers.Wait(urb.Task, UsbTransferHelpers.WaitMilliseconds(timeoutMilliseconds)))
            {
                Discard(urb);
                UsbTransferHelpers.Wait(urb.Task, Timeout.Infinite);
            }
            urb.Task.GetAwaiter().GetResult(); // disposed, or the device is gone
            return urb.GetResult(operation, CancellationToken.None);
        }

        /// <summary>Submits a URB and waits until it was reaped (or abandoned); discards it at the deadline.</summary>
        private async ValueTask<int> RunAsync(Urb urb, int timeoutMilliseconds, string operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var expiry = UsbTransferHelpers.TransferDeadline(timeoutMilliseconds, cancellationToken).CreateTokenSource();
            Submit(urb, operation);
            // Registered after submitting: a token that already fired discards the URB right away.
            using (expiry is null ? default : expiry.Token.UnsafeRegister(static state => ((Urb)state!).Discard(), urb))
                await urb.Task.ConfigureAwait(false);
            return urb.GetResult(operation, cancellationToken);
        }

        private unsafe void Submit(Urb urb, string operation)
        {
            bool startPump = false;
            lock (_urbSync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_broken) throw UsbErrors.Disconnected(operation);
                if (Libc.ioctl(_fd, Ioctl.SubmitUrb, urb.Native) < 0) throw Error(Marshal.GetLastPInvokeError(), operation);
                _urbs.Add((nint)urb.Native, urb);
                if (!_pumping) _pumping = startPump = true;
            }
            if (startPump) _ = PumpAsync();
        }

        /// <summary>Cancels a submitted URB; it then completes with -ENOENT or -ECONNRESET and is reaped as usual.</summary>
        private unsafe void Discard(Urb urb)
        {
            lock (_urbSync)
            {
                if (_disposed || _broken || !_urbs.ContainsKey((nint)urb.Native)) return;
                urb.Discarded = true;
                // Fails with EINVAL when the URB has already completed; it is then reaped with its real status.
                Libc.ioctl(_fd, Ioctl.DiscardUrb, urb.Native);
            }
        }

        /// <summary>Waits for completed URBs on the shared poll thread while any URB is in flight.</summary>
        private async Task PumpAsync()
        {
            while (true)
            {
                short events;
                try
                {
                    events = await UnixPoller.Shared.WaitAsync(_fd, Libc.POLLOUT, _pumpStop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return; // disposed
                }
                catch (Exception)
                {
                    events = Libc.POLLERR; // poll() itself failed: give up on the in-flight URBs
                }
                if (!ReapCompleted(events)) return;
            }
        }

        /// <summary>Reaps every completed URB and completes its waiter. Returns false when the pump should stop.</summary>
        private unsafe bool ReapCompleted(short events)
        {
            List<Urb>? completed = null, orphaned = null;
            bool pumping;
            lock (_urbSync)
            {
                if (_disposed) return false; // Dispose took over the remaining URBs
                bool lost = (events & (Libc.POLLERR | Libc.POLLHUP | Libc.POLLNVAL)) != 0;
                while (true)
                {
                    nint address;
                    if (Libc.ioctl(_fd, Ioctl.ReapUrbNoDelay, &address) < 0)
                    {
                        int errno = Marshal.GetLastPInvokeError();
                        if (errno == Libc.EINTR) continue;
                        if (errno != Libc.EAGAIN) lost = true; // ENODEV: disconnected and nothing left to reap
                        break;
                    }
                    if (_urbs.Remove(address, out var urb))
                    {
                        urb.Status = urb.Native->status;
                        urb.ActualLength = urb.Native->actual_length;
                        (completed ??= new()).Add(urb);
                    }
                }
                if (lost && _urbs.Count > 0)
                {
                    // Stop reaping for good, so that the remaining URBs' memory can be released.
                    _broken = true;
                    orphaned = [.. _urbs.Values];
                    _urbs.Clear();
                }
                pumping = _pumping = _urbs.Count > 0;
            }
            if (completed is not null)
                foreach (var urb in completed) urb.TrySetResult();
            if (orphaned is not null)
                foreach (var urb in orphaned)
                    urb.TrySetException(UsbErrors.Disconnected("a transfer"));
            return pumping;
        }

        public unsafe void ClearHalt(byte endpoint)
        {
            bool added = false;
            try
            {
                _file.DangerousAddRef(ref added); // throws ObjectDisposedException once disposed
                ObjectDisposedException.ThrowIf(_disposed, this);
                uint ep = endpoint;
                if (Libc.ioctl(_fd, Ioctl.ClearHalt, &ep) < 0) throw Error(Marshal.GetLastPInvokeError(), "clear halt");
            }
            finally
            {
                if (added) _file.DangerousRelease();
            }
        }

        public unsafe void Dispose()
        {
            List<Urb> pending;
            lock (_urbSync)
            {
                if (_disposed) return;
                _disposed = true; // from now on nothing submits or reaps URBs
                pending = [.. _urbs.Values];
                _urbs.Clear();
            }
            _pumpStop.Cancel(); // ends the pump's wait before the descriptor is closed
            uint ifnum = (uint)_interface;
            Libc.ioctl(_fd, Ioctl.ReleaseInterface, &ifnum); // kills the URBs of the interface
            if (_reattachDriver) Reattach(_interface);
            _file.Dispose();
            foreach (var urb in pending) urb.TrySetException(UsbErrors.Disposed());
        }

        /// <summary>
        /// A URB in native memory, with the setup packet and data of a control transfer behind it, or pointing at the
        /// caller's buffer for bulk and interrupt transfers. Completes when reaped or abandoned.
        /// </summary>
        private sealed unsafe class Urb : TaskCompletionSource
        {
            private readonly UsbFsDeviceHandle _owner;
            private readonly bool _control;
            private MemoryHandle _pin;

            /// <summary>A bulk or interrupt URB on a buffer that stays fixed until the URB is over.</summary>
            public Urb(UsbFsDeviceHandle owner, byte type, byte endpoint, byte* buffer, int length, MemoryHandle pin)
                : base(TaskCreationOptions.RunContinuationsAsynchronously)
            {
                _owner = owner;
                _pin = pin;
                Native = (Ioctl.Urb*)NativeMemory.AllocZeroed((nuint)sizeof(Ioctl.Urb));
                Native->type = type;
                Native->endpoint = endpoint;
                Native->buffer = (nint)buffer;
                Native->buffer_length = length;
            }

            private Urb(UsbFsDeviceHandle owner, int length)
                : base(TaskCreationOptions.RunContinuationsAsynchronously)
            {
                _owner = owner;
                _control = true;
                int total = UsbTransferHelpers.SetupPacketSize + length;
                Native = (Ioctl.Urb*)NativeMemory.AllocZeroed((nuint)(sizeof(Ioctl.Urb) + total));
                Native->type = Ioctl.UrbTypeControl;
                Native->buffer = (nint)Data;
                Native->buffer_length = total;
            }

            /// <summary>A control URB: the kernel expects the setup packet in front of the data, and puts IN data there.</summary>
            public static Urb Control(UsbFsDeviceHandle owner, byte requestType, byte request, ushort value, ushort index, int length,
                ReadOnlySpan<byte> outData)
            {
                var urb = new Urb(owner, length);
                var data = new Span<byte>(urb.Data, UsbTransferHelpers.SetupPacketSize + length);
                UsbTransferHelpers.WriteSetupPacket(data, requestType, request, value, index, length);
                outData.CopyTo(data[UsbTransferHelpers.SetupPacketSize..]);
                return urb;
            }

            /// <summary>A bulk or interrupt URB on a pinned caller buffer (asynchronous transfers).</summary>
            public static Urb Pinned(UsbFsDeviceHandle owner, byte type, byte endpoint, Memory<byte> buffer)
            {
                var pin = buffer.Pin();
                return new Urb(owner, type, endpoint, (byte*)pin.Pointer, buffer.Length, pin);
            }

            public Ioctl.Urb* Native { get; private set; }

            /// <summary>The native memory behind the URB structure (control transfers).</summary>
            private byte* Data => (byte*)Native + sizeof(Ioctl.Urb);

            /// <summary>Set (under the owner's lock) when the URB was discarded for a deadline or cancellation.</summary>
            public bool Discarded { get; set; }

            public int Status { get; set; }

            public int ActualLength { get; set; }

            public void Discard() => _owner.Discard(this);

            /// <summary>The data stage of a control URB.</summary>
            public ReadOnlySpan<byte> ControlData(int length) => new(Data + UsbTransferHelpers.SetupPacketSize, length);

            /// <summary>The number of bytes transferred, or the exception for the URB's status.</summary>
            public int GetResult(string operation, CancellationToken cancellationToken)
            {
                if (Status == 0)
                {
                    int capacity = Native->buffer_length - (_control ? UsbTransferHelpers.SetupPacketSize : 0);
                    return Math.Clamp(ActualLength, 0, capacity);
                }
                if (Discarded && Status is -ENOENT or -ECONNRESET)
                    throw UsbErrors.Interrupted(operation, cancellationToken, disposed: false);
                throw Error(-Status, operation);
            }

            /// <summary>Releases the memory. Only once the URB was reaped, never submitted, or abandoned by the handle.</summary>
            public void Free()
            {
                NativeMemory.Free(Native);
                Native = null;
                _pin.Dispose();
            }
        }
    }
}

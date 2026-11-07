using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VisaCs.Internal;

namespace VisaCs.Usb;

public sealed partial class WinUsbBackend
{
    /// <summary>
    /// An opened WinUSB interface. All transfers are overlapped I/O on the device handle (opened with
    /// FILE_FLAG_OVERLAPPED), and every pipe waits without limit (PIPE_TRANSFER_TIMEOUT 0): a transfer that reaches
    /// its timeout is cancelled with CancelIoEx. Synchronous transfers wait for their own event; asynchronous
    /// transfers bind the handle to the thread pool's completion port (<see cref="ThreadPoolBoundHandle"/>) on first
    /// use and complete in its callback, so no thread waits while they are pending.
    /// </summary>
    /// <remarks>
    /// Once the handle is bound, every overlapped I/O on it is posted to the thread pool, which would crash on an
    /// OVERLAPPED it did not allocate, unless the event in the OVERLAPPED has its low-order bit set. Synchronous
    /// transfers therefore pass their own OVERLAPPED with such an event. WinUSB's own internal synchronous I/O
    /// (WinUsb_SetPipePolicy, WinUsb_ResetPipe, ...) sets that bit too (checked in the disassembly of winusb.dll of
    /// Windows 11).
    /// </remarks>
    private sealed class WinUsbDeviceHandle : IUsbDeviceHandle
    {
        /// <summary>How long Dispose waits for calls in progress on other threads to leave WinUSB.</summary>
        private const int DisposeWaitMs = 5000;

        private static readonly unsafe IOCompletionCallback IOCompleted = OnIOCompleted;

        private readonly SafeFileHandle _file;
        private readonly nint _winusb;
        private readonly nint _handle; // the interface handle (may be an associated interface)
        private readonly Stack<nint> _events = new(); // events for synchronous transfers
        private readonly Lock _sync = new();
        private ThreadPoolBoundHandle? _bound;
        private int _activeCalls; // calls using the WinUSB handles right now
        private int _disposed;

        public WinUsbDeviceHandle(UsbDeviceInfo device, DeviceData data, int interfaceNumber, int descriptorTimeoutMs)
        {
            if (!data.Interfaces.TryGetValue(interfaceNumber, out var path))
            {
                // A device bound to WinUSB as a whole reaches all its interfaces through its one path.
                if (data.Composite || data.Interfaces.Count == 0)
                    throw new VisaException(VisaStatus.ErrorResourceNotFound, $"Interface {interfaceNumber} of {device} is not bound to WinUSB.");
                path = data.Interfaces.Values.First();
            }

            _file = Native.CreateFileW(path.DevicePath, Native.GENERIC_READ | Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                0, Native.OPEN_EXISTING, Native.FILE_ATTRIBUTE_NORMAL | Native.FILE_FLAG_OVERLAPPED, 0);
            if (_file.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                throw new VisaException(error == Native.ERROR_ACCESS_DENIED ? VisaStatus.ErrorResourceBusy : VisaStatus.ErrorResourceNotFound,
                    $"Cannot open USB device {device} (Win32 error {error}). Is it bound to WinUSB and not in use?");
            }
            try
            {
                if (!Native.WinUsb_Initialize(_file, out _winusb))
                    throw new VisaException(VisaStatus.ErrorResourceNotFound, $"WinUsb_Initialize failed (Win32 error {Marshal.GetLastPInvokeError()}).");
                _handle = FindInterface(interfaceNumber, out var desc)
                    ?? throw new VisaException(VisaStatus.ErrorResourceNotFound, $"Interface {interfaceNumber} of {device} is not reachable through WinUSB.");

                // Transfers enforce their timeouts by cancelling the I/O, so no pipe may time out on its own (the
                // default pipe's default is 5 seconds). Control transfers use the first interface's default pipe.
                SetNoTimeout(_winusb, 0);
                var endpoints = new List<UsbEndpointInfo>();
                for (byte i = 0; i < desc.bNumEndpoints; i++)
                {
                    if (!Native.WinUsb_QueryPipe(_handle, 0, i, out var pipe)) continue;
                    SetNoTimeout(_handle, pipe.PipeId);
                    endpoints.Add(new UsbEndpointInfo
                    {
                        Address = pipe.PipeId,
                        Type = (UsbTransferType)pipe.PipeType,
                        MaxPacketSize = (ushort)(pipe.MaximumPacketSize & 0x7FF),
                        Interval = pipe.Interval,
                    });
                }
                Interface = new UsbInterfaceInfo
                {
                    Number = desc.bInterfaceNumber,
                    AlternateSetting = desc.bAlternateSetting,
                    Class = desc.bInterfaceClass,
                    SubClass = desc.bInterfaceSubClass,
                    Protocol = desc.bInterfaceProtocol,
                    Endpoints = endpoints,
                };
                Device = ReadDevice(device, data, descriptorTimeoutMs);
            }
            catch
            {
                FreeHandles();
                throw;
            }
        }

        public UsbDeviceInfo Device { get; }

        public UsbInterfaceInfo Interface { get; }

        /// <summary>The WinUSB handle of an interface: the first one, or one associated with it.</summary>
        private nint? FindInterface(int interfaceNumber, out Native.USB_INTERFACE_DESCRIPTOR desc)
        {
            if (Native.WinUsb_QueryInterfaceSettings(_winusb, 0, out desc) && desc.bInterfaceNumber == interfaceNumber) return _winusb;
            for (byte i = 0; i < 32; i++)
            {
                if (!Native.WinUsb_GetAssociatedInterface(_winusb, i, out nint associated)) break;
                if (Native.WinUsb_QueryInterfaceSettings(associated, 0, out desc) && desc.bInterfaceNumber == interfaceNumber) return associated;
                Native.WinUsb_Free(associated);
            }
            desc = default;
            return null;
        }

        private static void SetNoTimeout(nint handle, byte pipe)
        {
            uint value = 0;
            Native.WinUsb_SetPipePolicy(handle, pipe, Native.PIPE_TRANSFER_TIMEOUT, 4, ref value);
        }

        /// <summary>
        /// Reads the device, configuration and string descriptors. Keeps what enumeration found when the device does
        /// not answer.
        /// </summary>
        private UsbDeviceInfo ReadDevice(UsbDeviceInfo device, DeviceData data, int timeoutMilliseconds)
        {
            string serial = device.SerialNumber;
            string? manufacturer = device.Manufacturer, product = device.Product;
            ushort release = device.DeviceRelease;
            byte deviceClass = device.DeviceClass;
            IReadOnlyList<UsbInterfaceInfo> interfaces = device.Interfaces;
            try
            {
                Span<byte> d = stackalloc byte[18];
                if (ControlTransfer(0x80, 6, UsbDescriptorParser.DescriptorDevice << 8, 0, d, timeoutMilliseconds) >= 18)
                {
                    var dd = UsbDescriptorParser.ParseDevice(d);
                    release = dd.DeviceRelease;
                    deviceClass = dd.DeviceClass;
                    manufacturer = UsbDescriptorParser.ReadString(this, dd.ManufacturerIndex, timeoutMilliseconds) ?? manufacturer;
                    product = UsbDescriptorParser.ReadString(this, dd.ProductIndex, timeoutMilliseconds) ?? product;
                    if (serial.Length == 0) serial = UsbDescriptorParser.ReadString(this, dd.SerialNumberIndex, timeoutMilliseconds) ?? "";
                }
                Span<byte> header = stackalloc byte[9];
                if (ControlTransfer(0x80, 6, UsbDescriptorParser.DescriptorConfiguration << 8, 0, header, timeoutMilliseconds) >= 4)
                {
                    var configuration = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header[2..])];
                    int n = ControlTransfer(0x80, 6, UsbDescriptorParser.DescriptorConfiguration << 8, 0, configuration, timeoutMilliseconds);
                    var parsed = UsbDescriptorParser.ParseConfiguration(configuration.AsSpan(0, n));
                    // Of a composite device, only the interfaces bound to WinUSB can be opened.
                    if (parsed.Count > 0) interfaces = parsed.Where(i => !data.Composite || data.Interfaces.ContainsKey(i.Number)).ToList();
                }
            }
            catch (VisaException)
            {
                // Not answering: keep what enumeration found.
            }
            // The claimed interface as WinUSB reports it.
            interfaces = [.. interfaces.Where(i => i.Number != Interface.Number), Interface];
            return new UsbDeviceInfo
            {
                VendorId = device.VendorId, ProductId = device.ProductId, SerialNumber = serial, Manufacturer = manufacturer, Product = product,
                DeviceRelease = release, DeviceClass = deviceClass, Interfaces = interfaces.OrderBy(i => i.Number).ToList(),
                Location = device.Location, BackendData = device.BackendData,
            };
        }

        /// <summary>Marks a call that uses the WinUSB handles, which Dispose must not free underneath it.</summary>
        private void Enter()
        {
            Interlocked.Increment(ref _activeCalls);
            if (Volatile.Read(ref _disposed) != 0)
            {
                Interlocked.Decrement(ref _activeCalls);
                throw new ObjectDisposedException(GetType().FullName);
            }
        }

        private void Exit() => Interlocked.Decrement(ref _activeCalls);

        private static Exception Error(int error, string operation) => error switch
        {
            Native.ERROR_SEM_TIMEOUT => UsbTransferHelpers.TimedOut(operation),
            Native.ERROR_OPERATION_ABORTED => new VisaException(VisaStatus.ErrorAbort, $"USB {operation} aborted."),
            Native.ERROR_DEVICE_NOT_CONNECTED or Native.ERROR_BAD_COMMAND or Native.ERROR_FILE_NOT_FOUND => UsbTransferHelpers.Disconnected(operation),
            _ => UsbTransferHelpers.Failed(operation, $"Win32 error {error}"),
        };

        private static Native.WINUSB_SETUP_PACKET Setup(byte requestType, byte request, ushort value, ushort index, int length) =>
            new() { RequestType = requestType, Request = request, Value = value, Index = index, Length = (ushort)length };

        /// <summary>Starts a control transfer (<paramref name="setup"/> not null) or a pipe transfer.</summary>
        private unsafe bool Issue(byte pipe, Native.WINUSB_SETUP_PACKET* setup, byte* buffer, int length, NativeOverlapped* overlapped)
        {
            if (setup is not null)
                return Native.WinUsb_ControlTransfer(_winusb, *setup, buffer, (uint)length, out _, (nint)overlapped);
            return (pipe & 0x80) != 0
                ? Native.WinUsb_ReadPipe(_handle, pipe, buffer, (uint)length, out _, (nint)overlapped)
                : Native.WinUsb_WritePipe(_handle, pipe, buffer, (uint)length, out _, (nint)overlapped);
        }

        public unsafe int ControlTransfer(byte requestType, byte request, ushort value, ushort index, Span<byte> buffer, int timeoutMilliseconds)
        {
            UsbTransferHelpers.CheckControlLength(buffer.Length);
            var setup = Setup(requestType, request, value, index, buffer.Length);
            return Transfer(0, &setup, buffer, timeoutMilliseconds, "control transfer");
        }

        public unsafe int BulkTransfer(byte endpoint, Span<byte> buffer, int timeoutMilliseconds) =>
            Transfer(endpoint, null, buffer, timeoutMilliseconds, UsbTransferHelpers.OperationName(endpoint));

        public unsafe int InterruptTransfer(byte endpoint, Span<byte> buffer, int timeoutMilliseconds) =>
            Transfer(endpoint, null, buffer, timeoutMilliseconds, UsbTransferHelpers.OperationName(endpoint));

        /// <summary>A synchronous transfer: overlapped I/O with an event that keeps it off the completion port.</summary>
        private unsafe int Transfer(byte pipe, Native.WINUSB_SETUP_PACKET* setup, Span<byte> buffer, int timeoutMilliseconds, string operation)
        {
            Enter();
            nint ev = 0;
            try
            {
                ev = RentEvent();
                fixed (byte* p = buffer)
                {
                    var overlapped = new NativeOverlapped { EventHandle = ev | 1 };
                    if (!Issue(pipe, setup, p, buffer.Length, &overlapped))
                    {
                        int error = Marshal.GetLastPInvokeError();
                        if (error != Native.ERROR_IO_PENDING) throw Error(error, operation);
                    }
                    bool timedOut = false;
                    uint wait = timeoutMilliseconds <= 0 ? Native.INFINITE : (uint)timeoutMilliseconds;
                    if (Native.WaitForSingleObject(ev, wait) == Native.WAIT_TIMEOUT)
                    {
                        timedOut = true;
                        Native.CancelIoEx(_file, &overlapped); // fails with ERROR_NOT_FOUND when the I/O has just completed
                    }
                    // Waits until the I/O is over (the kernel ignores the low-order tag bit of the event handle), so
                    // that the buffer and the OVERLAPPED can go.
                    if (Native.GetOverlappedResult(_file, &overlapped, out uint transferred, true)) return (int)transferred;
                    int result = Marshal.GetLastPInvokeError();
                    if (result == Native.ERROR_OPERATION_ABORTED)
                    {
                        // Data that an IN transfer received before it was cancelled is lost.
                        if (Volatile.Read(ref _disposed) != 0) throw UsbTransferHelpers.Disposed();
                        if (timedOut) throw UsbTransferHelpers.TimedOut(operation);
                    }
                    throw Error(result, operation);
                }
            }
            finally
            {
                ReturnEvent(ev);
                Exit();
            }
        }

        private nint RentEvent()
        {
            nint ev = 0;
            lock (_sync)
            {
                if (_events.Count > 0) ev = _events.Pop();
            }
            if (ev == 0)
            {
                ev = Native.CreateEventW(0, true, false, 0);
                if (ev == 0)
                    throw new VisaException(VisaStatus.ErrorSystemError, $"CreateEvent failed (Win32 error {Marshal.GetLastPInvokeError()}).");
            }
            Native.ResetEvent(ev);
            return ev;
        }

        private void ReturnEvent(nint ev)
        {
            if (ev == 0) return;
            lock (_sync)
            {
                if (_disposed == 0)
                {
                    _events.Push(ev);
                    return;
                }
            }
            Native.CloseHandle(ev);
        }

        public ValueTask<int> ControlTransferAsync(byte requestType, byte request, ushort value, ushort index, Memory<byte> buffer,
            int timeoutMilliseconds, CancellationToken cancellationToken = default)
        {
            UsbTransferHelpers.CheckControlLength(buffer.Length);
            return TransferAsync(0, Setup(requestType, request, value, index, buffer.Length), buffer, timeoutMilliseconds, "control transfer",
                cancellationToken);
        }

        public ValueTask<int> BulkTransferAsync(byte endpoint, Memory<byte> buffer, int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
            TransferAsync(endpoint, null, buffer, timeoutMilliseconds, UsbTransferHelpers.OperationName(endpoint), cancellationToken);

        public ValueTask<int> InterruptTransferAsync(byte endpoint, Memory<byte> buffer, int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
            TransferAsync(endpoint, null, buffer, timeoutMilliseconds, UsbTransferHelpers.OperationName(endpoint), cancellationToken);

        private async ValueTask<int> TransferAsync(byte pipe, Native.WINUSB_SETUP_PACKET? setup, Memory<byte> buffer, int timeoutMilliseconds,
            string operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var expiry = UsbTransferHelpers.TransferDeadline(timeoutMilliseconds, cancellationToken).CreateTokenSource();
            var transfer = new PendingTransfer(this);
            using (var pin = buffer.Pin())
            {
                Start(transfer, pipe, setup, pin, buffer.Length, operation);
                try
                {
                    // Registered after starting: a token that already fired cancels the transfer right away.
                    using (expiry is null ? default : expiry.Token.UnsafeRegister(static state => ((PendingTransfer)state!).Cancel(), transfer))
                        await transfer.Task.ConfigureAwait(false);
                }
                finally
                {
                    // The completion has been delivered and no cancellation can run any more.
                    transfer.Free();
                }
            }
            return transfer.GetResult(operation, cancellationToken, Volatile.Read(ref _disposed) != 0);
        }

        /// <summary>Starts an asynchronous transfer; throws (with nothing pending) if it fails right away.</summary>
        private unsafe void Start(PendingTransfer transfer, byte pipe, Native.WINUSB_SETUP_PACKET? setup, MemoryHandle pin, int length, string operation)
        {
            Enter();
            try
            {
                ThreadPoolBoundHandle bound;
                lock (_sync) bound = _bound ??= ThreadPoolBoundHandle.BindHandle(_file);
                var overlapped = bound.AllocateNativeOverlapped(IOCompleted, transfer, null);
                var packet = setup.GetValueOrDefault();
                if (!Issue(pipe, setup.HasValue ? &packet : null, (byte*)pin.Pointer, length, overlapped))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error != Native.ERROR_IO_PENDING)
                    {
                        // Failed synchronously: no completion will be posted.
                        bound.FreeNativeOverlapped(overlapped);
                        throw Error(error, operation);
                    }
                }
                // Pending, or done synchronously: either way the completion is posted to the thread pool.
                transfer.Started(bound, overlapped);
            }
            finally
            {
                Exit();
            }
        }

        private static unsafe void OnIOCompleted(uint errorCode, uint numBytes, NativeOverlapped* overlapped)
        {
            var transfer = (PendingTransfer)ThreadPoolBoundHandle.GetNativeOverlappedState(overlapped)!;
            transfer.Completed(errorCode, numBytes);
        }

        /// <summary>Cancels one asynchronous transfer, or every transfer on the handle when <paramref name="overlapped"/> is null.</summary>
        private unsafe void CancelIO(NativeOverlapped* overlapped)
        {
            try
            {
                Native.CancelIoEx(_file, overlapped); // fails with ERROR_NOT_FOUND when the I/O has completed
            }
            catch (ObjectDisposedException)
            {
                // Closed: closing the handle has cancelled the I/O.
            }
        }

        public void ClearHalt(byte endpoint)
        {
            Enter();
            try
            {
                if (!Native.WinUsb_ResetPipe(_handle, endpoint)) throw Error(Marshal.GetLastPInvokeError(), "clear halt");
            }
            finally
            {
                Exit();
            }
        }

        public unsafe void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // Cancel all I/O on the handle (synchronous transfers of other threads too), and let calls in progress
            // leave WinUSB before its handles are freed.
            var deadline = Deadline.FromMilliseconds(DisposeWaitMs);
            while (true)
            {
                CancelIO(null);
                if (Volatile.Read(ref _activeCalls) == 0) break;
                if (deadline.HasExpired) return; // a call is stuck inside WinUSB: leak the handles rather than free them under it
                Thread.Sleep(1);
            }
            FreeHandles();
        }

        private void FreeHandles()
        {
            if (_handle != 0 && _handle != _winusb) Native.WinUsb_Free(_handle);
            if (_winusb != 0) Native.WinUsb_Free(_winusb);
            _file.Dispose();
            lock (_sync)
            {
                _bound?.Dispose(); // pending completions are still delivered, and their overlapped freed afterwards
                while (_events.Count > 0) Native.CloseHandle(_events.Pop());
            }
        }

        /// <summary>
        /// An asynchronous transfer. The NativeOverlapped is freed by the waiter only after the completion callback
        /// ran and the cancellation registration was disposed, so CancelIoEx never sees a freed (or reused) pointer.
        /// </summary>
        private sealed unsafe class PendingTransfer(WinUsbDeviceHandle owner) : TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        {
            private ThreadPoolBoundHandle? _bound;
            private NativeOverlapped* _overlapped;
            private uint _error;
            private uint _transferred;
            private volatile bool _cancelled;

            public void Started(ThreadPoolBoundHandle bound, NativeOverlapped* overlapped)
            {
                _bound = bound;
                _overlapped = overlapped;
            }

            public void Completed(uint error, uint transferred)
            {
                _error = error;
                _transferred = transferred;
                TrySetResult();
            }

            public void Cancel()
            {
                _cancelled = true;
                if (_overlapped is not null && Volatile.Read(ref owner._disposed) == 0) owner.CancelIO(_overlapped);
            }

            public void Free()
            {
                _bound?.FreeNativeOverlapped(_overlapped);
                _overlapped = null;
            }

            public int GetResult(string operation, CancellationToken cancellationToken, bool disposed)
            {
                if (_error == 0) return (int)_transferred;
                // Cancelled by the deadline, the caller's token or Dispose.
                if (_error == Native.ERROR_OPERATION_ABORTED && (_cancelled || disposed || cancellationToken.IsCancellationRequested))
                    throw UsbTransferHelpers.Interrupted(operation, cancellationToken, disposed);
                throw Error((int)_error, operation);
            }
        }
    }
}

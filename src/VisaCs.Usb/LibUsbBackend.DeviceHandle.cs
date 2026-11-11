using System.Runtime.InteropServices;
using VisaCs.Internal;

namespace VisaCs.Usb;

public sealed partial class LibUsbBackend
{
    /// <summary>
    /// A device handle with one claimed interface. Every transfer is a libusb asynchronous transfer completed by the
    /// backend's event thread; a synchronous transfer blocks until its completion callback has run. libusb enforces
    /// the transfer timeouts itself.
    /// </summary>
    private sealed class Handle : IUsbDeviceHandle
    {
        private readonly LibUsbBackend _backend;
        private readonly nint _context;
        private readonly nint _handle;
        private readonly Lock _sync = new(); // guards _pending and the submission of transfers
        private readonly Lock _closeSync = new(); // makes concurrent Dispose calls wait for the first one
        private readonly HashSet<AsyncTransfer> _pending = new();
        private volatile bool _disposed;
        private bool _closed;
        private int _activeCalls; // ClearHalt calls, which use the handle outside the transfer machinery

        public Handle(LibUsbBackend backend, nint context, nint handle, UsbDeviceInfo device, UsbInterfaceInfo info)
        {
            _backend = backend;
            _context = context;
            _handle = handle;
            Device = device;
            Interface = info;
        }

        public UsbDeviceInfo Device { get; }

        public UsbInterfaceInfo Interface { get; }

        public unsafe int ControlTransfer(byte requestType, byte request, ushort value, ushort index, Span<byte> buffer, int timeoutMilliseconds)
        {
            var transfer = CreateControl(requestType, request, value, index, buffer, timeoutMilliseconds);
            return Run(transfer, (requestType & 0x80) != 0 ? buffer : default, UsbTransferHelpers.SetupPacketSize, "control transfer");
        }

        public int BulkTransfer(byte endpoint, Span<byte> buffer, int timeoutMilliseconds) =>
            PipeTransfer(Native.LIBUSB_TRANSFER_TYPE_BULK, endpoint, buffer, timeoutMilliseconds);

        public int InterruptTransfer(byte endpoint, Span<byte> buffer, int timeoutMilliseconds) =>
            PipeTransfer(Native.LIBUSB_TRANSFER_TYPE_INTERRUPT, endpoint, buffer, timeoutMilliseconds);

        private unsafe int PipeTransfer(byte type, byte endpoint, Span<byte> buffer, int timeoutMilliseconds)
        {
            var transfer = new AsyncTransfer(_handle, type, endpoint, buffer.Length, timeoutMilliseconds);
            bool input = (endpoint & 0x80) != 0;
            if (!input) buffer.CopyTo(new Span<byte>(transfer.Buffer, buffer.Length));
            return Run(transfer, input ? buffer : default, 0, UsbTransferHelpers.OperationName(endpoint));
        }

        public async ValueTask<int> ControlTransferAsync(byte requestType, byte request, ushort value, ushort index, Memory<byte> buffer,
            int timeoutMilliseconds, CancellationToken cancellationToken = default)
        {
            var transfer = CreateControl(requestType, request, value, index, buffer.Span, timeoutMilliseconds);
            return await RunAsync(transfer, (requestType & 0x80) != 0 ? buffer : default, UsbTransferHelpers.SetupPacketSize,
                "control transfer", cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<int> BulkTransferAsync(byte endpoint, Memory<byte> buffer, int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
            PipeTransferAsync(Native.LIBUSB_TRANSFER_TYPE_BULK, endpoint, buffer, timeoutMilliseconds, cancellationToken);

        public ValueTask<int> InterruptTransferAsync(byte endpoint, Memory<byte> buffer, int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
            PipeTransferAsync(Native.LIBUSB_TRANSFER_TYPE_INTERRUPT, endpoint, buffer, timeoutMilliseconds, cancellationToken);

        private unsafe ValueTask<int> PipeTransferAsync(byte type, byte endpoint, Memory<byte> buffer, int timeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            var transfer = new AsyncTransfer(_handle, type, endpoint, buffer.Length, timeoutMilliseconds);
            bool input = (endpoint & 0x80) != 0;
            if (!input) buffer.Span.CopyTo(new Span<byte>(transfer.Buffer, buffer.Length));
            return RunAsync(transfer, input ? buffer : default, 0, UsbTransferHelpers.OperationName(endpoint), cancellationToken);
        }

        /// <summary>A control transfer: libusb expects the setup packet in front of the data, and puts IN data there.</summary>
        private unsafe AsyncTransfer CreateControl(byte requestType, byte request, ushort value, ushort index, ReadOnlySpan<byte> buffer,
            int timeoutMilliseconds)
        {
            UsbTransferHelpers.CheckControlLength(buffer.Length);
            int total = UsbTransferHelpers.SetupPacketSize + buffer.Length;
            var transfer = new AsyncTransfer(_handle, Native.LIBUSB_TRANSFER_TYPE_CONTROL, 0, total, timeoutMilliseconds);
            var data = new Span<byte>(transfer.Buffer, total);
            UsbTransferHelpers.WriteSetupPacket(data, requestType, request, value, index, buffer.Length);
            if ((requestType & 0x80) == 0) buffer.CopyTo(data[UsbTransferHelpers.SetupPacketSize..]);
            return transfer;
        }

        /// <summary>Submits a transfer and blocks until libusb reported it (or Dispose gave up on it).</summary>
        private int Run(AsyncTransfer transfer, Span<byte> input, int dataOffset, string operation)
        {
            try
            {
                Submit(transfer, operation);
                try
                {
                    // Released by the completion callback on the event thread; the thread pool is not involved.
                    transfer.Task.GetAwaiter().GetResult();
                }
                finally
                {
                    lock (_sync) _pending.Remove(transfer);
                }
                return transfer.GetResult(input, dataOffset, operation, CancellationToken.None, _disposed);
            }
            finally
            {
                transfer.Release();
            }
        }

        private async ValueTask<int> RunAsync(AsyncTransfer transfer, Memory<byte> input, int dataOffset, string operation,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Submit(transfer, operation);
                try
                {
                    // Registered after submitting: a token that is already cancelled cancels the transfer right away.
                    using (cancellationToken.UnsafeRegister(static state => ((AsyncTransfer)state!).Cancel(), transfer))
                        await transfer.Task.ConfigureAwait(false);
                }
                finally
                {
                    lock (_sync) _pending.Remove(transfer);
                }
                return transfer.GetResult(input.Span, dataOffset, operation, cancellationToken, _disposed);
            }
            finally
            {
                transfer.Release();
            }
        }

        private void Submit(AsyncTransfer transfer, string operation)
        {
            _backend.EnsureEventLoop(_context);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                int rc = transfer.Submit();
                if (rc < 0) throw Error(rc, operation);
                _pending.Add(transfer);
            }
            if (transfer.HasTimeout) _backend.WakeForTimeout(_context);
        }

        public void ClearHalt(byte endpoint)
        {
            Interlocked.Increment(ref _activeCalls);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                int rc = Native.libusb_clear_halt(_handle, endpoint);
                if (rc < 0) throw Error(rc, "clear halt");
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        public void Dispose()
        {
            lock (_closeSync)
            {
                if (_closed) return;
                _closed = true;
                AsyncTransfer[] pending;
                lock (_sync)
                {
                    _disposed = true;
                    pending = [.. _pending];
                    foreach (var transfer in pending) transfer.Cancel();
                }
                // libusb forgets the transfers of a closed handle without calling back, so wait until the event
                // thread has reported the cancelled ones. Transfers it fails to report are abandoned (leaked).
                var deadline = Deadline.FromMilliseconds(CancelWaitMs);
                if (pending.Length > 0 && !_backend.IsEventThread)
                {
                    // Task.Wait is released by the callback itself, so this does not depend on the thread pool.
                    foreach (var transfer in pending)
                    {
                        if (!UsbTransferHelpers.Wait(transfer.Task, deadline.RemainingMilliseconds)) break;
                    }
                }
                foreach (var transfer in pending) transfer.Abandon();
                // A ClearHalt in progress uses the handle: let it finish (libusb bounds it with its control timeout).
                while (Volatile.Read(ref _activeCalls) != 0 && !deadline.HasExpired) Thread.Sleep(1);
                Native.libusb_release_interface(_handle, Interface.Number);
                Native.libusb_close(_handle);
            }
            lock (_backend._sync) _backend._handles.Remove(this);
        }
    }

    /// <summary>
    /// One asynchronous transfer: a libusb_transfer and a native data buffer (with the setup packet in front for a
    /// control transfer). libusb calls back on the event thread when the transfer has completed, timed out or been
    /// cancelled. The callback frees the libusb_transfer, so <see cref="Cancel"/> takes the same lock; the data
    /// buffer is freed by the waiter once the transfer is over. The data goes through native memory rather than the
    /// caller's buffer so that an abandoned transfer can be leaked without the caller's memory staying in use.
    /// </summary>
    private sealed unsafe class AsyncTransfer : TaskCompletionSource
    {
        private readonly Lock _sync = new();
        private Native.Transfer* _transfer; // null once freed (after the callback, or when it was never submitted)
        private GCHandle _self;
        private bool _submitted;
        private bool _abandoned;

        public AsyncTransfer(nint deviceHandle, byte type, byte endpoint, int length, int timeoutMilliseconds)
            : base(TaskCreationOptions.RunContinuationsAsynchronously)
        {
            Buffer = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, length));
            _transfer = Native.libusb_alloc_transfer(0);
            if (_transfer is null)
            {
                NativeMemory.Free(Buffer);
                throw UsbErrors.Create(UsbError.OutOfMemory, "libusb_alloc_transfer failed.");
            }
            HasTimeout = timeoutMilliseconds >= 0;
            _transfer->dev_handle = deviceHandle;
            _transfer->flags = 0;
            _transfer->endpoint = endpoint;
            _transfer->type = type;
            // libusb has no "do not wait": 0 means no timeout, so the shortest timeout it has stands in for it.
            _transfer->timeout = HasTimeout ? (uint)Math.Max(1, timeoutMilliseconds) : 0;
            _transfer->length = length;
            _transfer->callback = &OnCompleted;
            _transfer->buffer = Buffer;
            _transfer->num_iso_packets = 0;
        }

        public byte* Buffer { get; private set; }

        public bool HasTimeout { get; }

        public int Status { get; private set; }

        public int ActualLength { get; private set; }

        /// <summary>Submits the transfer; returns a libusb error code.</summary>
        public int Submit()
        {
            _self = GCHandle.Alloc(this);
            _transfer->user_data = GCHandle.ToIntPtr(_self);
            int rc = Native.libusb_submit_transfer(_transfer);
            if (rc < 0) _self.Free();
            else _submitted = true;
            return rc;
        }

        /// <summary>Asks libusb to cancel the transfer; it then calls back with LIBUSB_TRANSFER_CANCELLED.</summary>
        public void Cancel()
        {
            lock (_sync)
            {
                if (_submitted && _transfer is not null) Native.libusb_cancel_transfer(_transfer);
            }
        }

        [UnmanagedCallersOnly]
        private static void OnCompleted(Native.Transfer* transfer)
        {
            try
            {
                var self = (AsyncTransfer)GCHandle.FromIntPtr(transfer->user_data).Target!;
                self.Complete(transfer);
            }
            catch (Exception)
            {
                // Never unwind into libusb.
            }
        }

        private void Complete(Native.Transfer* transfer)
        {
            lock (_sync)
            {
                Status = transfer->status;
                ActualLength = transfer->actual_length;
                _transfer = null;
                // Freeing in the callback is allowed: libusb does not touch the transfer afterwards.
                Native.libusb_free_transfer(transfer);
            }
            _self.Free();
            TrySetResult();
        }

        /// <summary>
        /// Gives up on a submitted transfer that libusb did not report in time: its waiter fails, and its memory is
        /// leaked rather than freed while libusb or the operating system may still use it.
        /// </summary>
        public void Abandon()
        {
            lock (_sync)
            {
                if (!_submitted || _transfer is null) return; // reported (or never submitted)
                _abandoned = true;
            }
            TrySetException(UsbErrors.Disposed());
        }

        /// <summary>The number of bytes transferred (copied to <paramref name="input"/> for IN transfers), or the exception for the status.</summary>
        public int GetResult(Span<byte> input, int dataOffset, string operation, CancellationToken cancellationToken, bool disposed)
        {
            switch (Status)
            {
                case Native.LIBUSB_TRANSFER_COMPLETED:
                    int n = input.IsEmpty ? ActualLength : Math.Min(ActualLength, input.Length);
                    if (!input.IsEmpty) new ReadOnlySpan<byte>(Buffer + dataOffset, n).CopyTo(input);
                    return n;
                case Native.LIBUSB_TRANSFER_CANCELLED when cancellationToken.IsCancellationRequested || disposed:
                case Native.LIBUSB_TRANSFER_TIMED_OUT when cancellationToken.IsCancellationRequested:
                    throw UsbErrors.Interrupted(operation, cancellationToken, disposed);
                default:
                    // The error codes the synchronous API reports for these statuses (a timeout loses received data).
                    throw Error(Status switch
                    {
                        Native.LIBUSB_TRANSFER_TIMED_OUT => Native.LIBUSB_ERROR_TIMEOUT,
                        Native.LIBUSB_TRANSFER_STALL => Native.LIBUSB_ERROR_PIPE,
                        Native.LIBUSB_TRANSFER_OVERFLOW => Native.LIBUSB_ERROR_OVERFLOW,
                        Native.LIBUSB_TRANSFER_NO_DEVICE => Native.LIBUSB_ERROR_NO_DEVICE,
                        Native.LIBUSB_TRANSFER_ERROR or Native.LIBUSB_TRANSFER_CANCELLED => Native.LIBUSB_ERROR_IO,
                        _ => Native.LIBUSB_ERROR_OTHER,
                    }, operation);
            }
        }

        /// <summary>Frees the memory of a transfer that is over or was never submitted (not of an abandoned one).</summary>
        public void Release()
        {
            lock (_sync)
            {
                if (_abandoned) return;
                if (!_submitted && _transfer is not null)
                {
                    Native.libusb_free_transfer(_transfer);
                    _transfer = null;
                }
                if (Buffer is not null)
                {
                    NativeMemory.Free(Buffer);
                    Buffer = null;
                }
            }
        }
    }
}

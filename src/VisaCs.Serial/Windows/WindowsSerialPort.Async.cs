using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Tasks.Sources;
using VisaCs.Internal;

namespace VisaCs.Serial;

// The asynchronous transfers live in a part of the class that is not an unsafe context, where await is allowed.
internal sealed partial class WindowsSerialPort
{
    private protected override async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, Deadline deadline)
    {
        int generation = AbortGeneration;
        nint handle = Acquire();
        try
        {
            // Fires at the deadline or on the caller's cancellation, and cancels the pending ReadFile.
            using var expiry = deadline.CreateTokenSource();
            while (true)
            {
                var transfer = new OverlappedTransfer(this, handle);
                await transfer.StartAsync(buffer, read: true, deadline, generation, expiry?.Token ?? default).ConfigureAwait(false);
                int n = ReadResult(transfer.BytesTransferred, transfer.Error, expiry?.IsCancellationRequested == true, deadline);
                if (n > 0) return n;
            }
        }
        finally
        {
            _handle.DangerousRelease();
        }
    }

    private protected override async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> data, Deadline deadline)
    {
        int generation = AbortGeneration;
        nint handle = Acquire();
        try
        {
            using var expiry = deadline.CreateTokenSource();
            int offset = 0;
            while (offset < data.Length)
            {
                var transfer = new OverlappedTransfer(this, handle);
                await transfer.StartAsync(data[offset..], read: false, deadline, generation, expiry?.Token ?? default).ConfigureAwait(false);
                offset += transfer.BytesTransferred;
                CheckWrite(offset, data.Length, transfer.Error, expiry?.IsCancellationRequested == true, deadline);
            }
        }
        finally
        {
            _handle.DangerousRelease();
        }
    }

    /// <summary>
    /// One ReadFile or WriteFile of the asynchronous path. It completes through the thread pool's I/O completion port;
    /// cancellation (deadline, caller's token, Dispose) calls CancelIoEx on its own OVERLAPPED only.
    /// </summary>
    private sealed unsafe class OverlappedTransfer : IValueTaskSource
    {
        private const int Starting = 0, Started = 1, Completed = 2;
        private static readonly IOCompletionCallback CompletionCallback = OnCompletion;

        private readonly WindowsSerialPort _port;
        private readonly nint _handle;
        private ManualResetValueTaskSourceCore<bool> _source;
        private NativeOverlapped* _overlapped;
        private MemoryHandle _pin;
        private CancellationTokenRegistration _registration;
        private int _state;

        /// <param name="port">The port; it holds a reference on its handle for as long as this transfer runs.</param>
        /// <param name="handle">The raw handle.</param>
        public OverlappedTransfer(WindowsSerialPort port, nint handle)
        {
            _port = port;
            _handle = handle;
            _source.RunContinuationsAsynchronously = true;
        }

        /// <summary>Win32 error code of the transfer, 0 on success.</summary>
        public int Error { get; private set; }

        /// <summary>Bytes transferred, also when the transfer was cancelled.</summary>
        public int BytesTransferred { get; private set; }

        /// <summary>
        /// Issues the transfer. The task completes when it has finished; <see cref="Error"/> and
        /// <see cref="BytesTransferred"/> then tell the outcome. Cancelling <paramref name="cancellationToken"/>
        /// cancels it, and so do a deadline that has already passed and an Abort since <paramref name="generation"/>.
        /// </summary>
        public ValueTask StartAsync(ReadOnlyMemory<byte> buffer, bool read, Deadline deadline, int generation, CancellationToken cancellationToken)
        {
            _pin = buffer.Pin();
            try
            {
                _overlapped = _port._handle.Binding!.AllocateNativeOverlapped(CompletionCallback, this, null);
            }
            catch
            {
                _pin.Dispose();
                throw;
            }

            byte* p = (byte*)_pin.Pointer;
            bool ok = read
                ? Native.ReadFile(_handle, p, (uint)buffer.Length, null, _overlapped)
                : Native.WriteFile(_handle, p, (uint)buffer.Length, null, _overlapped);
            if (!ok)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != Native.ERROR_IO_PENDING)
                {
                    // Failed at once: no completion packet will be queued.
                    Error = error;
                    Release();
                    return ValueTask.CompletedTask;
                }
            }

            // In flight: a completion packet arrives in every case, even if the transfer has finished already.
            if (cancellationToken.CanBeCanceled)
                _registration = cancellationToken.UnsafeRegister(static s => ((OverlappedTransfer)s!).Cancel(), this);
            // Dispose and Abort cancel the transfers in progress, and this one may have started just after. A
            // deadline that has passed already (timeout 0) does not wait for the timer.
            Interlocked.MemoryBarrier();
            if (_port.IsDisposed || _port.AbortGeneration != generation || deadline.HasExpired) Cancel();
            // Whichever comes second of this point and the completion callback finishes the transfer.
            if (Interlocked.Exchange(ref _state, Started) == Completed) Finish();
            return new ValueTask(this, _source.Version);
        }

        private void Cancel() => Native.CancelIoEx(_handle, _overlapped);

        private static void OnCompletion(uint errorCode, uint numBytes, NativeOverlapped* overlapped)
        {
            var transfer = (OverlappedTransfer)ThreadPoolBoundHandle.GetNativeOverlappedState(overlapped)!;
            transfer.Error = (int)errorCode;
            transfer.BytesTransferred = (int)numBytes;
            if (Interlocked.Exchange(ref transfer._state, Completed) == Started) transfer.Finish();
        }

        private void Finish()
        {
            // Waits for a Cancel running on another thread: it uses the OVERLAPPED that is freed next.
            _registration.Dispose();
            Release();
            _source.SetResult(true);
        }

        private void Release()
        {
            _port._handle.Binding!.FreeNativeOverlapped(_overlapped);
            _overlapped = null;
            _pin.Dispose();
        }

        public void GetResult(short token) => _source.GetResult(token);

        public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _source.OnCompleted(continuation, state, token, flags);
    }
}

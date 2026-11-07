using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VisaCs.Gpib.Native;
using VisaCs.Internal;
using static VisaCs.Gpib.Native.Ni488;

namespace VisaCs.Gpib;

// Asynchronous transfers of Ni488Driver (ibrda/ibwrta/ibcmda); the remarks on the class describe the design.
//
// One transfer at a time per descriptor (UdState reserves it). The buffer stays pinned until the driver is done with
// it: until the transfer has been resynchronized (by the ibnotify callback or by the poller's ibwait with CMPL) or
// stopped with ibstop. Completion, cancellation and Dispose race for the transfer through Transfer.TryClaim; only
// the winner talks to the driver about it and releases the buffer.
//
// Per-thread error state. The library keeps ibsta/iberr/ibcnt per thread, so every status is read on the thread that
// made the call: start errors on the caller's thread, results from the callback's arguments (the normal globals are
// undefined in a callback), from AsyncIbsta/AsyncIberr/AsyncIbcnt where the library has them (linux-gpib; its
// ThreadIbcnt after ibwait describes the ibwait call), otherwise from ThreadIberr/ThreadIbcnt right after the
// resynchronizing ibwait on the poller thread. Blocking calls run whole (call and Check) on the board's worker thread.
//
// ibnotify. Each transfer arms the descriptor with mask CMPL after starting (CMPL is true when no I/O is in progress,
// so arming earlier would fire at once), with the transfer's id as reference data. The callback looks the id up in a
// static table and returns 0, so a late callback never touches released state. NI-488.2 rearms with a callback's
// return value; whether that can undo an ibnotify made by another thread while the callback returns is not
// documented. Arming therefore waits while a callback of the same descriptor runs in managed code, and arms once
// more shortly after when a callback returned just before (the gap between our return and the library's rearm is
// not observable). A watchdog re-arms transfers whose driver reports CMPL but whose callback has not come for a
// whole period; a callback without CMPL (failed rearm) or a failed ibnotify hands the transfer to the poller.
public sealed unsafe partial class Ni488Driver
{
    private const int WatchdogMs = 500;

    /// <summary>A transfer armed this soon after a callback of its descriptor returned is armed once more after this time.</summary>
    private const int CallbackGuardMs = 20;

    /// <summary>Transfers armed with ibnotify, by the reference data passed to it.</summary>
    private static readonly ConcurrentDictionary<nint, Transfer> Notified = new();
    private static long s_lastTransferId;

    private Timer? _watchdog; // guarded by _sync
    private bool _watchdogRunning; // guarded by _sync
    private int _watchdogBusy;
    private int _notifiedCount;

    private enum TransferKind
    {
        Read,
        Write,
        Command,
    }

    private static ObjectDisposedException Disposed(IOBase owner) =>
        new(owner is Board ? nameof(IGpibBoard) : nameof(IGpibDevice), "The GPIB descriptor was closed.");

    /// <summary>True if the library can carry out this kind of transfer asynchronously, and stop it.</summary>
    private static unsafe bool CanStartAsync(Ni488Api api, TransferKind kind) =>
        api.ibstop != null && kind switch
        {
            TransferKind.Read => api.ibrda != null,
            TransferKind.Write => api.ibwrta != null,
            _ => api.ibcmda != null,
        };

    /// <summary>Starts an asynchronous transfer; <paramref name="prepare"/> configures the descriptor first.</summary>
    private unsafe Task<GpibReadResult> StartTransfer(IOBase owner, TransferKind kind, Memory<byte> buffer, Action prepare,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<GpibReadResult>(cancellationToken);
        if (owner.IsDisposed) return Task.FromException<GpibReadResult>(Disposed(owner));
        var api = owner.Api;
        var state = owner.State;
        var transfer = new Transfer(owner, kind);
        if (!state.TryReserve(transfer))
        {
            return Task.FromException<GpibReadResult>(new VisaException(VisaStatus.ErrorInProgress,
                "Another asynchronous GPIB transfer is in progress on this descriptor."));
        }

        int status, error = 0;
        try
        {
            prepare();
            transfer.Pin = buffer.Pin();
            byte* p = (byte*)transfer.Pin.Pointer;
            status = kind switch
            {
                TransferKind.Read => api.ibrda(state.Ud, p, buffer.Length),
                TransferKind.Write => api.ibwrta(state.Ud, p, buffer.Length),
                _ => api.ibcmda(state.Ud, p, buffer.Length),
            };
            if ((status & ERR) != 0) error = api.ThreadIberr(); // the caller's thread made the call
        }
        catch (Exception ex)
        {
            transfer.Pin.Dispose();
            state.Release(transfer);
            return Task.FromException<GpibReadResult>(ex);
        }
        if ((status & ERR) != 0)
        {
            transfer.Pin.Dispose();
            state.Release(transfer);
            return Task.FromException<GpibReadResult>(Error(error, transfer.Operation));
        }

        state.Publish(transfer);
        if (cancellationToken.CanBeCanceled)
            transfer.Registration = cancellationToken.UnsafeRegister(static (s, token) => Cancel((Transfer)s!, token), transfer);
        if (owner.IsDisposed && TryStop(transfer, out _))
            Fail(transfer, Disposed(owner)); // Dispose ran meanwhile and may have missed it
        else
            Arm(transfer);
        return transfer.Completion.Task;
    }

    /// <summary>Has the driver report the transfer's completion: through ibnotify where possible, else by polling.</summary>
    private unsafe void Arm(Transfer transfer)
    {
        var api = transfer.Api;
        if (api.ibnotify != null)
        {
            AddNotified(transfer);
            if (transfer.IsClaimed)
            {
                RemoveNotified(transfer); // cancelled while starting
                return;
            }
            var state = transfer.State;
            var spin = new SpinWait();
            while (Volatile.Read(ref state.CallbacksRunning) != 0) spin.SpinOnce();
            if ((api.ibnotify(state.Ud, CMPL, &OnNotify, transfer.Id) & ERR) == 0)
            {
                StartWatchdog();
                if (Volatile.Read(ref state.CallbacksRunning) != 0 || state.CallbackReturnedWithin(CallbackGuardMs))
                    ScheduleRearm(transfer);
                return;
            }
            if (!RemoveNotified(transfer)) return; // ended meanwhile
        }
        Poll(transfer);
    }

    /// <summary>Arms ibnotify again for a transfer that is still pending (a lost or possibly undone arming).</summary>
    private unsafe void Rearm(Transfer transfer)
    {
        if (transfer.IsClaimed || transfer.Polled || !Notified.ContainsKey(transfer.Id) || transfer.State.Published != transfer) return;
        // A callback that is under way finds the transfer claimed, or completes it; the extra one finds nothing.
        if ((transfer.Api.ibnotify(transfer.State.Ud, CMPL, &OnNotify, transfer.Id) & ERR) != 0 && RemoveNotified(transfer))
            Poll(transfer);
    }

    private void AddNotified(Transfer transfer)
    {
        Interlocked.Increment(ref _notifiedCount);
        Notified[transfer.Id] = transfer;
    }

    private bool RemoveNotified(Transfer transfer)
    {
        if (!Notified.TryRemove(transfer.Id, out _)) return false;
        Interlocked.Decrement(ref _notifiedCount);
        return true;
    }

    /// <summary>
    /// The ibnotify callback, on a thread of the library. Its signature is NI-488.2's
    /// <c>int Callback(int ud, int ibsta, int iberr, long ibcntl, void *refData)</c>: reading the count as int is exact
    /// for every transfer this class starts and safe on every ABI (long is 32 or 64 bits, passed in a full register or
    /// stack slot). The calling convention is the platform default, as for all calls into the library.
    /// </summary>
    [UnmanagedCallersOnly]
    private static int OnNotify(int ud, int ibsta, int iberr, int ibcntl, nint refData)
    {
        try
        {
            if (Notified.TryGetValue(refData, out var transfer)) transfer.Driver.Notify(transfer, ibsta, iberr, ibcntl);
        }
        catch (Exception)
        {
            // Nothing may reach the library's thread; the watchdog finds the transfer if it is still pending.
        }
        return 0; // one callback per arming
    }

    private void Notify(Transfer transfer, int status, int error, int count)
    {
        var state = transfer.State;
        Interlocked.Increment(ref state.CallbacksRunning);
        try
        {
            if ((status & CMPL) == 0)
            {
                // Not a completion (a failed rearm or a driver error): the poller finishes the transfer.
                if (RemoveNotified(transfer)) Poll(transfer);
                return;
            }
            if (transfer.TryClaim()) Finish(transfer, status, error, count);
        }
        finally
        {
            Volatile.Write(ref state.LastCallbackReturn, Stopwatch.GetTimestamp());
            Interlocked.Decrement(ref state.CallbacksRunning);
        }
    }

    /// <summary>Completes a claimed transfer with what the driver reported once it was done with the buffer.</summary>
    private void Finish(Transfer transfer, int status, int error, int count)
    {
        Release(transfer);
        if ((status & ERR) == 0)
            transfer.Completion.TrySetResult(new GpibReadResult(count, (status & END) != 0));
        else if (transfer.Kind == TransferKind.Read && error == EABO && count > 0)
            transfer.Completion.TrySetResult(new GpibReadResult(count, false)); // partial data before a timeout, like Read
        else
            transfer.Completion.TrySetException(Error(error, transfer.Operation, count));
    }

    private void Fail(Transfer transfer, Exception error)
    {
        Release(transfer);
        transfer.Completion.TrySetException(error);
    }

    private void Release(Transfer transfer)
    {
        RemoveNotified(transfer);
        transfer.Registration.Unregister();
        transfer.Pin.Dispose();
        transfer.State.Release(transfer);
    }

    /// <summary>Claims a transfer and stops it in the driver; false if it has already ended.</summary>
    private static unsafe bool TryStop(Transfer transfer, out int count)
    {
        count = 0;
        if (!transfer.TryClaim()) return false;
        var api = transfer.Api;
        int ud = transfer.State.Ud;
        api.ibstop(ud); // aborts and resynchronizes the transfer: from now on the driver leaves the buffer alone
        count = api.HasAsyncStatus ? api.AsyncIbcnt() : api.ThreadIbcnt();
        if (!transfer.Polled && api.ibnotify != null)
            api.ibnotify(ud, 0, null, 0); // disarm; a callback already under way finds the transfer claimed
        return true;
    }

    private static void Cancel(Transfer transfer, CancellationToken cancellationToken)
    {
        if (!TryStop(transfer, out int count)) return;
        var stopped = new VisaTimeoutException($"GPIB {transfer.Operation} was canceled.") { BytesTransferred = count };
        transfer.Driver.Fail(transfer, AsyncIO.Canceled(stopped, cancellationToken));
    }

    /// <summary>Ends everything a disposed object has pending, then closes it (after its running blocking call, if any).</summary>
    private void Release(IOBase owner)
    {
        var error = Disposed(owner);
        if (owner.State.Published is { } transfer && transfer.Owner == owner && TryStop(transfer, out _)) Fail(transfer, error);
        EndServiceRequestWaits(owner, error);
        BoardWorker? worker;
        lock (_sync) _workers.TryGetValue(owner.BoardIndex, out worker);
        if (worker is not null) worker.Release(owner, error);
        else owner.Close();
    }

    private void StartWatchdog()
    {
        lock (_sync)
        {
            if (_watchdogRunning) return;
            _watchdog ??= new Timer(static s => ((Ni488Driver)s!).CheckNotified(), this, Timeout.Infinite, Timeout.Infinite);
            _watchdog.Change(WatchdogMs, WatchdogMs);
            _watchdogRunning = true;
        }
    }

    /// <summary>Recovers transfers whose completion notification was lost; stops itself when none are armed.</summary>
    private unsafe void CheckNotified()
    {
        if (Interlocked.Exchange(ref _watchdogBusy, 1) != 0) return;
        try
        {
            foreach (var (_, transfer) in Notified)
            {
                if (transfer.Driver != this || transfer.IsClaimed || transfer.ElapsedMs < WatchdogMs) continue;
                // Finished, yet not reported: arm again, which reports at once because CMPL is true.
                if ((transfer.Api.ibwait(transfer.State.Ud, 0) & CMPL) != 0) Rearm(transfer);
            }
            lock (_sync)
            {
                if (_watchdogRunning && Volatile.Read(ref _notifiedCount) == 0)
                {
                    _watchdog!.Change(Timeout.Infinite, Timeout.Infinite);
                    _watchdogRunning = false;
                }
            }
        }
        catch (Exception)
        {
            // Keep the timer; the next tick tries again.
        }
        finally
        {
            Volatile.Write(ref _watchdogBusy, 0);
        }
    }

    /// <summary>State of one unit descriptor, shared by every object using it (board descriptors are shared).</summary>
    private sealed class UdState(int ud)
    {
        private readonly Lock _configLock = new();
        private readonly Lock _transferLock = new();
        private int _timeoutCode = -1;
        private int _termChar = -2; // -1: none, -2: unknown
        private int _endOfTransfer = -1; // -1: unknown
        private Transfer? _transfer;
        private bool _published;

        public readonly int Ud = ud;

        /// <summary>ibnotify callbacks for this descriptor that are running in managed code.</summary>
        public int CallbacksRunning;

        /// <summary>When the last ibnotify callback for this descriptor returned (a Stopwatch timestamp).</summary>
        public long LastCallbackReturn;

        public bool CallbackReturnedWithin(int milliseconds)
        {
            long last = Volatile.Read(ref LastCallbackReturn);
            return last != 0 && Stopwatch.GetElapsedTime(last).TotalMilliseconds < milliseconds;
        }

        /// <summary>The asynchronous transfer the driver is carrying out, if any.</summary>
        public Transfer? Published
        {
            get
            {
                lock (_transferLock) return _published ? _transfer : null;
            }
        }

        /// <summary>Sets the descriptor's timeout (ibtmo) unless it is already set; returns the driver's status.</summary>
        public unsafe int SetTimeout(Ni488Api api, int code)
        {
            lock (_configLock)
            {
                if (code == _timeoutCode) return 0;
                int status = api.ibtmo(Ud, code);
                _timeoutCode = (status & ERR) == 0 ? code : -1;
                return status;
            }
        }

        /// <summary>Configures the read termination character (null: EOI only, 8-bit compare) unless it is already set.</summary>
        public unsafe int SetTermChar(Ni488Api api, byte? termChar)
        {
            int wanted = termChar ?? -1;
            lock (_configLock)
            {
                if (wanted == _termChar) return 0;
                _termChar = -2;
                int status = api.ibconfig(Ud, IbcEOSrd, termChar is null ? 0 : 1);
                if ((status & ERR) == 0 && termChar is { } c)
                {
                    status = api.ibconfig(Ud, IbcEOSchar, c);
                    // Compare all 8 bits: with the default 7-bit compare, binary bytes such as 0x8A end a read for '\n'.
                    if ((status & ERR) == 0) status = api.ibconfig(Ud, IbcEOScmp, 1);
                }
                if ((status & ERR) == 0) _termChar = wanted;
                return status;
            }
        }

        /// <summary>Configures whether writes assert EOI with the last byte (IbcEOT) unless it is already set.</summary>
        public unsafe int SetEndOfTransfer(Ni488Api api, bool end)
        {
            int wanted = end ? 1 : 0;
            lock (_configLock)
            {
                if (wanted == _endOfTransfer) return 0;
                int status = api.ibconfig(Ud, IbcEOT, wanted);
                _endOfTransfer = (status & ERR) == 0 ? wanted : -1;
                return status;
            }
        }

        /// <summary>Reserves the descriptor for an asynchronous transfer; false while another one is under way.</summary>
        public bool TryReserve(Transfer transfer)
        {
            lock (_transferLock)
            {
                if (_transfer is not null) return false;
                _transfer = transfer;
                _published = false;
                return true;
            }
        }

        /// <summary>Marks the reserved transfer as started in the driver.</summary>
        public void Publish(Transfer transfer)
        {
            lock (_transferLock)
                if (_transfer == transfer) _published = true;
        }

        public void Release(Transfer transfer)
        {
            lock (_transferLock)
            {
                if (_transfer != transfer) return;
                _transfer = null;
                _published = false;
            }
        }
    }

    /// <summary>An asynchronous transfer the driver is carrying out.</summary>
    private sealed class Transfer(IOBase owner, TransferKind kind)
    {
        private int _claimed;

        public readonly IOBase Owner = owner;
        public readonly TransferKind Kind = kind;
        public readonly nint Id = (nint)Interlocked.Increment(ref s_lastTransferId);
        public readonly long Started = Stopwatch.GetTimestamp();
        public readonly TaskCompletionSource<GpibReadResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Keeps the buffer in place until the driver is done with it.</summary>
        public MemoryHandle Pin;

        public CancellationTokenRegistration Registration;

        /// <summary>Completion is detected by the polling thread, not by ibnotify.</summary>
        public volatile bool Polled;

        /// <summary>When to arm ibnotify once more (a Stopwatch timestamp), for a transfer armed right after a callback.</summary>
        public long RearmAt;

        public Ni488Driver Driver => Owner.Driver;
        public Ni488Api Api => Owner.Api;
        public UdState State => Owner.State;
        public string Operation => Kind switch { TransferKind.Read => "read", TransferKind.Write => "write", _ => "send command" };
        public double ElapsedMs => Stopwatch.GetElapsedTime(Started).TotalMilliseconds;
        public bool IsClaimed => Volatile.Read(ref _claimed) != 0;

        /// <summary>Takes the right to end the transfer: exactly one of completion, cancellation and Dispose gets it.</summary>
        public bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;
    }
}

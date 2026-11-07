using System.Diagnostics;
using VisaCs.Internal;
using static VisaCs.Gpib.Native.Ni488;

namespace VisaCs.Gpib;

// The polling thread of Ni488Driver: it completes transfers the library cannot report through ibnotify, arms
// ibnotify again where a callback may have undone an arming, and checks asynchronous service request waits. It runs
// while there is work and ends after a few idle seconds.
public sealed partial class Ni488Driver
{
    private const int PollFastMs = 1, PollSlowMs = 5, FastPhaseMs = 50, IdleThreadMs = 5000;

    /// <summary>While SRQ or RQS stays signalled, a device that did not request service is polled again after this time.</summary>
    private const int SrqRepollMs = 100;

    private readonly object _pollLock = new();
    private readonly List<Transfer> _polledTransfers = []; // guarded by _pollLock
    private readonly List<Transfer> _rearms = []; // guarded by _pollLock
    private readonly List<SrqWait> _srqWaits = []; // guarded by _pollLock
    private Thread? _poller; // guarded by _pollLock
    private bool _pollRequested; // guarded by _pollLock

    private void Poll(Transfer transfer)
    {
        transfer.Polled = true;
        lock (_pollLock)
        {
            _polledTransfers.Add(transfer);
            WakePollerLocked();
        }
    }

    private void ScheduleRearm(Transfer transfer)
    {
        Volatile.Write(ref transfer.RearmAt, Stopwatch.GetTimestamp() + CallbackGuardMs * Stopwatch.Frequency / 1000);
        lock (_pollLock)
        {
            _rearms.Add(transfer);
            WakePollerLocked();
        }
    }

    private void WakePollerLocked()
    {
        _pollRequested = true;
        if (_poller is not null)
        {
            Monitor.Pulse(_pollLock);
            return;
        }
        _poller = new Thread(PollLoop) { IsBackground = true, Name = "GPIB completion poller" };
        _poller.Start();
    }

    private bool HasPollWorkLocked => _polledTransfers.Count != 0 || _rearms.Count != 0 || _srqWaits.Count != 0;

    /// <summary>The polling thread: runs while transfers, re-arms or service request waits are pending, ends when idle.</summary>
    private void PollLoop()
    {
        var transfers = new List<Transfer>();
        var rearms = new List<Transfer>();
        var waits = new List<SrqWait>();
        var boardStatus = new Dictionary<int, int>();
        while (true)
        {
            lock (_pollLock)
            {
                _polledTransfers.RemoveAll(static t => t.IsClaimed);
                _rearms.RemoveAll(static t => t.IsClaimed);
                _srqWaits.RemoveAll(static w => w.IsEnded);
                if (!HasPollWorkLocked)
                {
                    if (!_pollRequested) Monitor.Wait(_pollLock, IdleThreadMs);
                    if (!HasPollWorkLocked)
                    {
                        _poller = null;
                        return;
                    }
                }
                _pollRequested = false;
                transfers.AddRange(_polledTransfers);
                waits.AddRange(_srqWaits);
                long now = Stopwatch.GetTimestamp();
                foreach (var transfer in _rearms)
                    if (Volatile.Read(ref transfer.RearmAt) <= now) rearms.Add(transfer);
                foreach (var transfer in rearms) _rearms.Remove(transfer);
            }

            bool fast = false;
            foreach (var transfer in transfers)
            {
                try
                {
                    PollTransfer(transfer);
                }
                catch (Exception ex)
                {
                    if (TryStop(transfer, out _)) Fail(transfer, ex);
                }
                if (!transfer.IsClaimed && transfer.ElapsedMs < FastPhaseMs) fast = true;
            }
            foreach (var transfer in rearms)
            {
                try
                {
                    Rearm(transfer);
                }
                catch (Exception)
                {
                    // The watchdog arms it again later.
                }
            }
            foreach (var wait in waits)
            {
                try
                {
                    PollServiceRequest(wait, boardStatus);
                }
                catch (Exception ex)
                {
                    wait.End(ex);
                }
            }
            transfers.Clear();
            rearms.Clear();
            waits.Clear();
            boardStatus.Clear();

            lock (_pollLock)
            {
                if (!_pollRequested) Monitor.Wait(_pollLock, fast || _rearms.Count != 0 ? PollFastMs : PollSlowMs);
            }
        }
    }

    private unsafe void PollTransfer(Transfer transfer)
    {
        if (transfer.IsClaimed) return;
        var api = transfer.Api;
        int ud = transfer.State.Ud;
        int status = api.ibwait(ud, 0); // mask 0: the current status, at once
        if ((status & (CMPL | ERR)) == 0) return; // still running
        if (!transfer.TryClaim()) return; // cancelled or disposed meanwhile
        if ((status & CMPL) == 0)
        {
            // The status query failed: stop whatever may still run before releasing the buffer.
            int error = api.ThreadIberr();
            api.ibstop(ud);
            Finish(transfer, ERR, error, 0);
            return;
        }
        status = api.ibwait(ud, CMPL); // resynchronizes; returns at once as the transfer has finished
        if (api.HasAsyncStatus)
            Finish(transfer, api.AsyncIbsta(), api.AsyncIberr(), api.AsyncIbcnt());
        else
            Finish(transfer, status, api.ThreadIberr(), api.ThreadIbcnt());
    }

    /// <summary>
    /// Whether to serial-poll <paramref name="device"/> now: it signals a request (RQS from automatic serial polling, or
    /// SRQ on the bus) and it was not already polled without result during this signal within <see cref="SrqRepollMs"/>.
    /// </summary>
    private static bool ShouldPoll(Device device, int boardStatus, int deviceStatus)
    {
        bool signalled = (deviceStatus & (ERR | RQS)) == RQS || (boardStatus & (ERR | SRQI)) == SRQI;
        if (!signalled)
        {
            Volatile.Write(ref device.SrqPolledAt, 0);
            return false;
        }
        long polled = Volatile.Read(ref device.SrqPolledAt);
        return polled == 0 || Stopwatch.GetElapsedTime(polled).TotalMilliseconds >= SrqRepollMs;
    }

    /// <summary>Waits until SRQ is asserted on the owner's board, without holding a thread.</summary>
    private Task<bool> WaitForBoardRequestAsync(Board board, int timeoutMilliseconds, CancellationToken cancellationToken) =>
        WaitForSignalAsync(board, null, Deadline.FromMilliseconds(timeoutMilliseconds), cancellationToken);

    /// <summary>Like <see cref="Device.WaitForServiceRequest"/>: waits for a signal on the polling thread, then serial-polls on the board's worker thread.</summary>
    private async ValueTask<byte?> WaitForDeviceRequestAsync(Device device, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        var deadline = Deadline.FromMilliseconds(timeoutMilliseconds);
        while (true)
        {
            if (!await WaitForSignalAsync(device, device, deadline, cancellationToken).ConfigureAwait(false)) return null;
            if (await WorkerFor(device.BoardIndex).Run(device, device.PollRequest, cancellationToken).ConfigureAwait(false) is { } stb)
                return stb;
        }
    }

    /// <summary>
    /// Completes with true when the board's SRQ is asserted (<paramref name="device"/> null) or when the device should be
    /// serial-polled (see <see cref="ShouldPoll"/>), and with false when <paramref name="deadline"/> passes.
    /// </summary>
    private Task<bool> WaitForSignalAsync(IOBase owner, Device? device, Deadline deadline, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<bool>(cancellationToken);
        if (owner.IsDisposed) return Task.FromException<bool>(Disposed(owner));
        var wait = new SrqWait(owner, device, deadline);
        if (cancellationToken.CanBeCanceled)
        {
            wait.Registration = cancellationToken.UnsafeRegister(static (s, token) =>
                ((SrqWait)s!).End(AsyncIO.Canceled(new VisaTimeoutException("GPIB service request wait was canceled."), token)), wait);
        }
        lock (_pollLock)
        {
            _srqWaits.Add(wait);
            WakePollerLocked();
        }
        if (owner.IsDisposed) wait.End(Disposed(owner)); // Dispose ran meanwhile and may have missed it
        return wait.Completion.Task;
    }

    private static unsafe void PollServiceRequest(SrqWait wait, Dictionary<int, int> boardStatus)
    {
        if (wait.IsEnded) return;
        var owner = wait.Owner;
        int board = owner.BoardIndex;
        if (!boardStatus.TryGetValue(board, out int status)) boardStatus[board] = status = owner.Api.ibwait(board, 0);
        bool signalled = wait.Device is { } device
            ? ShouldPoll(device, status, owner.Api.ibwait(device.Ud, 0))
            : (status & (ERR | SRQI)) == SRQI;
        if (signalled) wait.End(true);
        else if (wait.Deadline.HasExpired) wait.End(false);
    }

    private void EndServiceRequestWaits(IOBase owner, Exception error)
    {
        List<SrqWait> waits;
        lock (_pollLock) waits = _srqWaits.FindAll(w => w.Owner == owner);
        foreach (var wait in waits) wait.End(error);
    }

    /// <summary>An asynchronous wait for a service request signal, checked by the polling thread.</summary>
    private sealed class SrqWait(IOBase owner, Device? device, Deadline deadline)
    {
        private int _ended;

        public readonly IOBase Owner = owner;

        /// <summary>The device whose request is awaited, or null for a board wait (SRQ only).</summary>
        public readonly Device? Device = device;

        public readonly Deadline Deadline = deadline;
        public readonly TaskCompletionSource<bool> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration;

        public bool IsEnded => Volatile.Read(ref _ended) != 0;

        public void End(bool signalled)
        {
            if (TryEnd()) Completion.TrySetResult(signalled);
        }

        public void End(Exception error)
        {
            if (TryEnd()) Completion.TrySetException(error);
        }

        private bool TryEnd()
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0) return false;
            Registration.Unregister();
            return true;
        }
    }
}

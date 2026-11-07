using System.Runtime.InteropServices;

namespace VisaCs.Internal;

/// <summary>
/// Waits for file descriptors to become ready on behalf of asynchronous operations (Linux and macOS). One
/// background thread blocks in poll() for all of them, so a pending read or USB transfer holds no thread of its own.
/// Callers use non-blocking descriptors: try the operation, and on EAGAIN await <see cref="WaitAsync"/>.
/// </summary>
/// <remarks>
/// This file is compiled into each library that needs it, so each of those libraries has its own poll thread.
/// Cancel pending waits on a descriptor before closing it; a wait on a closed descriptor completes with POLLNVAL.
/// </remarks>
internal sealed unsafe class UnixPoller
{
    private static readonly Lock CreateSync = new();
    private static UnixPoller? _shared;

    private readonly Lock _sync = new();
    private readonly List<Waiter> _waiters = new();
    private readonly int _wakeRead;
    private readonly int _wakeWrite;
    private int _wakePending;

    /// <summary>The poller of this library, started on first use.</summary>
    public static UnixPoller Shared
    {
        get
        {
            if (Volatile.Read(ref _shared) is { } poller) return poller;
            lock (CreateSync) return _shared ??= new UnixPoller();
        }
    }

    private UnixPoller()
    {
        NativeLibraries.EnsureRegistered();
        int* fds = stackalloc int[2];
        if (Libc.CreatePipe(fds) != 0)
            throw new VisaException(VisaStatus.ErrorSystemError, $"pipe failed (errno {Marshal.GetLastPInvokeError()}).");
        _wakeRead = fds[0];
        _wakeWrite = fds[1];
        // Named after the library: each library that links this file has its own poll thread.
        new Thread(Run) { IsBackground = true, Name = $"{typeof(UnixPoller).Assembly.GetName().Name} poll" }.Start();
    }

    private sealed class Waiter(int fd, short events) : TaskCompletionSource<short>(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        public readonly int Fd = fd;
        public readonly short Events = events;
        public CancellationTokenRegistration Registration;
    }

    /// <summary>
    /// Completes when <paramref name="fd"/> reports one of <paramref name="events"/>, an error or a hang-up, and
    /// returns the reported events. Cancelling the token cancels the wait (the task then ends canceled).
    /// </summary>
    public Task<short> WaitAsync(int fd, short events, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<short>(cancellationToken);
        var waiter = new Waiter(fd, events);
        if (cancellationToken.CanBeCanceled)
        {
            waiter.Registration = cancellationToken.UnsafeRegister(static (state, token) =>
            {
                var (poller, w) = ((UnixPoller, Waiter))state!;
                lock (poller._sync) poller._waiters.Remove(w);
                w.TrySetCanceled(token);
                poller.Wake();
            }, (this, waiter));
        }
        lock (_sync)
        {
            // The token may have fired during registration; a canceled waiter must not be listed.
            if (!waiter.Task.IsCompleted) _waiters.Add(waiter);
        }
        Wake();
        return waiter.Task;
    }

    private void Wake()
    {
        if (Interlocked.Exchange(ref _wakePending, 1) != 0) return;
        byte one = 1;
        Libc.write(_wakeWrite, &one, 1);
    }

    private void Run()
    {
        var fds = new Libc.PollFd[16];
        var active = new List<Waiter>();
        byte* drain = stackalloc byte[64];
        while (true)
        {
            active.Clear();
            lock (_sync) active.AddRange(_waiters);
            int count = active.Count + 1;
            if (fds.Length < count) fds = new Libc.PollFd[Math.Max(count, fds.Length * 2)];
            fds[0] = new Libc.PollFd { fd = _wakeRead, events = Libc.POLLIN };
            for (int i = 0; i < active.Count; i++)
                fds[i + 1] = new Libc.PollFd { fd = active[i].Fd, events = active[i].Events };

            int rc;
            fixed (Libc.PollFd* p = fds) rc = Libc.poll(p, (nuint)count, -1);
            if (rc < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                if (errno == Libc.EINTR) continue;
                // Not expected with valid arguments; fail the current waiters rather than spin.
                var error = new VisaException(VisaStatus.ErrorSystemError, $"poll failed (errno {errno}).");
                foreach (var w in active) Complete(w, error);
                continue;
            }

            if (fds[0].revents != 0)
            {
                // Drain first, then allow the next wake byte. Clearing first would let a Wake() in between write a
                // byte that this read swallows while the flag stays set, and every later Wake() would be lost.
                // Waiters added before the flag is cleared are in the next snapshot of the list.
                Libc.read(_wakeRead, drain, 64);
                Volatile.Write(ref _wakePending, 0);
            }
            for (int i = 0; i < active.Count; i++)
            {
                short revents = fds[i + 1].revents;
                if (revents != 0) Complete(active[i], revents);
            }
        }
    }

    private void Complete(Waiter waiter, short revents)
    {
        lock (_sync)
        {
            if (!_waiters.Remove(waiter)) return;
        }
        waiter.Registration.Unregister();
        waiter.TrySetResult(revents);
    }

    private void Complete(Waiter waiter, Exception error)
    {
        lock (_sync)
        {
            if (!_waiters.Remove(waiter)) return;
        }
        waiter.Registration.Unregister();
        waiter.TrySetException(error);
    }
}

using VisaCs.Internal;

namespace VisaCs.Gpib;

// The board worker threads of Ni488Driver: they run the blocking calls that have no asynchronous form.
public sealed partial class Ni488Driver
{
    private readonly Dictionary<int, BoardWorker> _workers = []; // guarded by _sync

    private BoardWorker WorkerFor(int board)
    {
        lock (_sync)
        {
            if (!_workers.TryGetValue(board, out var worker)) _workers[board] = worker = new BoardWorker($"GPIB{board} worker");
            return worker;
        }
    }

    /// <summary>
    /// Runs the blocking calls of one board (serial poll, device clear, trigger, and transfers when the library has no
    /// asynchronous calls) in order on one thread, which starts on demand and ends after a few idle seconds.
    /// </summary>
    private sealed class BoardWorker(string name)
    {
        private readonly object _lock = new();
        private readonly LinkedList<WorkItem> _queue = new(); // guarded by _lock
        private Thread? _thread; // guarded by _lock
        private WorkItem? _running; // guarded by _lock

        /// <summary>Queues a call. Cancelling removes it while queued; once running it ends when the driver returns.</summary>
        public Task<T> Run<T>(IOBase owner, Func<T> call, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<T>(cancellationToken);
            var item = new WorkItem<T>(this, owner, call, cancellationToken);
            if (cancellationToken.CanBeCanceled)
                item.Registration = cancellationToken.UnsafeRegister(static (s, token) => ((WorkItem)s!).Cancel(token), item);
            lock (_lock)
            {
                if (owner.IsDisposed)
                {
                    if (item.TryDrop()) item.Fail(Disposed(owner));
                    return item.Completion.Task;
                }
                item.Node = _queue.AddLast(item);
                if (_thread is null)
                {
                    _thread = new Thread(Loop) { IsBackground = true, Name = name };
                    _thread.Start();
                }
                else
                {
                    Monitor.Pulse(_lock);
                }
            }
            return item.Completion.Task;
        }

        /// <summary>Fails the owner's queued calls and closes it, after its running call if there is one.</summary>
        public void Release(IOBase owner, Exception error)
        {
            List<WorkItem>? dropped = null;
            bool deferred = false;
            lock (_lock)
            {
                for (var node = _queue.First; node is not null;)
                {
                    var next = node.Next;
                    if (node.Value.Owner == owner && node.Value.TryDrop())
                    {
                        _queue.Remove(node);
                        (dropped ??= []).Add(node.Value);
                    }
                    node = next;
                }
                if (_running is { } running && running.Owner == owner)
                {
                    running.AfterRun += owner.Close;
                    deferred = true;
                }
            }
            if (dropped is not null)
                foreach (var item in dropped) item.Fail(error);
            if (!deferred) owner.Close();
        }

        public void Remove(WorkItem item)
        {
            lock (_lock)
                if (item.Node is { List: not null } node) _queue.Remove(node);
        }

        private void Loop()
        {
            while (true)
            {
                WorkItem item;
                lock (_lock)
                {
                    var node = _queue.First;
                    while (node is null)
                    {
                        if (!Monitor.Wait(_lock, IdleThreadMs) && _queue.First is null)
                        {
                            _thread = null;
                            return;
                        }
                        node = _queue.First;
                    }
                    _queue.RemoveFirst();
                    item = node.Value;
                    if (!item.TryStart()) continue;
                    _running = item;
                }

                item.Execute();
                Action? afterRun;
                lock (_lock)
                {
                    _running = null;
                    afterRun = item.AfterRun;
                }
                try
                {
                    afterRun?.Invoke();
                }
                catch (Exception)
                {
                    // A deferred close that fails has nobody left to report to.
                }
            }
        }
    }

    private abstract class WorkItem(BoardWorker worker, IOBase owner, CancellationToken cancellationToken)
    {
        private const int Queued = 0, Running = 1, Ended = 2;
        private int _state;

        public readonly IOBase Owner = owner;
        protected readonly CancellationToken Token = cancellationToken;
        public LinkedListNode<WorkItem>? Node; // guarded by the worker's lock
        public CancellationTokenRegistration Registration;

        /// <summary>A close waiting for this call to return; guarded by the worker's lock.</summary>
        public Action? AfterRun;

        public bool TryStart() => Interlocked.CompareExchange(ref _state, Running, Queued) == Queued;

        public bool TryDrop() => Interlocked.CompareExchange(ref _state, Ended, Queued) == Queued;

        public abstract void Execute();

        public abstract void Fail(Exception error);

        public void Cancel(CancellationToken token)
        {
            if (!TryDrop()) return; // running: it ends when the driver returns
            worker.Remove(this);
            Fail(AsyncIO.Canceled(new VisaTimeoutException("The GPIB operation was canceled."), token));
        }
    }

    private sealed class WorkItem<T>(BoardWorker worker, IOBase owner, Func<T> call, CancellationToken cancellationToken)
        : WorkItem(worker, owner, cancellationToken)
    {
        public readonly TaskCompletionSource<T> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Execute()
        {
            try
            {
                T result = call();
                Registration.Unregister();
                Completion.TrySetResult(result); // the call completed: its result wins over a late cancellation
            }
            catch (Exception ex)
            {
                Registration.Unregister();
                Completion.TrySetException(Token.IsCancellationRequested ? AsyncIO.Canceled(ex, Token) : ex);
            }
        }

        public override void Fail(Exception error)
        {
            Registration.Unregister();
            Completion.TrySetException(error);
        }
    }
}

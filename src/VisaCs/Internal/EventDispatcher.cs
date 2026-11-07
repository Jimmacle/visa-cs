using System.Collections.Concurrent;

namespace VisaCs.Internal;

/// <summary>
/// Runs a session's event handlers on the thread pool, one at a time and in the order the events were posted.
/// Exceptions thrown by a handler are caught and discarded so that one faulty handler does not stop delivery.
/// </summary>
internal sealed class EventDispatcher
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private bool _running;

    public void Post(Action action)
    {
        _queue.Enqueue(action);
        if (!Interlocked.Exchange(ref _running, true))
            ThreadPool.UnsafeQueueUserWorkItem(static s => ((EventDispatcher)s!).Drain(), this);
    }

    private void Drain()
    {
        while (true)
        {
            while (_queue.TryDequeue(out var action))
            {
                try { action(); }
                catch { /* user handler exceptions must not stop the dispatcher */ }
            }
            Volatile.Write(ref _running, false);
            if (_queue.IsEmpty || Interlocked.Exchange(ref _running, true)) return;
        }
    }
}

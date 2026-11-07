using System.Diagnostics.CodeAnalysis;
using VisaCs.Internal;

namespace VisaCs;

// Events (viEnableEvent, viDisableEvent, viDiscardEvents, viWaitOnEvent and the handler mechanism).
public abstract partial class VisaSession
{
    private readonly Lock _eventSync = new();
    private readonly ChangeSignal _eventChanged = new();
    private readonly Dictionary<EventType, EventMechanism> _enabledEvents = new();
    private readonly LinkedList<VisaEventArgs> _eventQueue = new();
    private readonly List<VisaEventArgs> _suspendedEvents = new();
    private readonly EventDispatcher _dispatcher = new();

    /// <summary>
    /// How many events the queue mechanism keeps for <see cref="WaitOnEvent"/> (VI_ATTR_MAX_QUEUE_LENGTH); the same limit
    /// applies to events held by <see cref="EventMechanism.SuspendHandler"/>. When the queue is full, newly arriving
    /// events are dropped. At least 1; default 50.
    /// </summary>
    public int MaximumQueueLength
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 50;

    /// <summary>
    /// Raised for every event delivered through the handler mechanism, including service requests (after
    /// <see cref="ServiceRequest"/>). Requires <see cref="EnableEvent"/> with <see cref="EventMechanism.Handler"/>
    /// (or <see cref="EventMechanism.All"/>).
    /// </summary>
    /// <remarks>
    /// Handlers run on a thread-pool thread, one at a time and in the order the events arrived, never on the thread
    /// that enabled the event. An exception thrown by a handler is caught and discarded. Events that were already
    /// dispatched may still be delivered shortly after <see cref="DisableEvent"/> or <see cref="Dispose"/>. A handler
    /// may call the session's methods.
    /// </remarks>
    public event EventHandler<VisaEventArgs>? VisaEvent;

    /// <summary>
    /// Raised for service requests delivered through the handler mechanism. Requires
    /// <c>EnableEvent(EventType.ServiceRequest, EventMechanism.Handler)</c> (or <see cref="EventMechanism.All"/>). Handlers
    /// run as described for <see cref="VisaEvent"/>.
    /// </summary>
    public event EventHandler<ServiceRequestEventArgs>? ServiceRequest;

    /// <summary>Event types this session can deliver; other types throw VI_ERROR_INV_EVENT in <see cref="EnableEvent"/>.</summary>
    public virtual IReadOnlyCollection<EventType> SupportedEventTypes => Array.Empty<EventType>();

    /// <summary>
    /// Enables an event type (viEnableEvent) for <paramref name="mechanism"/>: <see cref="EventMechanism.Queue"/>
    /// (for <see cref="WaitOnEvent"/>), <see cref="EventMechanism.Handler"/> (for <see cref="ServiceRequest"/> and
    /// <see cref="VisaEvent"/>), both (<see cref="EventMechanism.All"/>, the default), or
    /// <see cref="EventMechanism.SuspendHandler"/>. Enabling the handler mechanism delivers events held by
    /// <see cref="EventMechanism.SuspendHandler"/>. The first enable of a type may talk to the device (for example
    /// VXI-11 opens its interrupt channel). Unsupported types throw VI_ERROR_INV_EVENT, invalid mechanisms
    /// VI_ERROR_INV_MECH.
    /// </summary>
    public void EnableEvent(EventType eventType, EventMechanism mechanism = EventMechanism.All) =>
        AsyncIO.Complete(EnableEventAsync(eventType, mechanism, CancellationToken.None, async: false));

    /// <summary>Enables an event type for the queue and handler mechanisms; see <see cref="EnableEvent"/>.</summary>
    public Task EnableEventAsync(EventType eventType, CancellationToken cancellationToken = default) =>
        EnableEventAsync(eventType, EventMechanism.All, cancellationToken, async: true).AsTask();

    /// <summary>Enables an event type (viEnableEvent) without blocking; see <see cref="EnableEvent"/>.</summary>
    public Task EnableEventAsync(EventType eventType, EventMechanism mechanism, CancellationToken cancellationToken = default) =>
        EnableEventAsync(eventType, mechanism, cancellationToken, async: true).AsTask();

    private ValueTask EnableEventAsync(EventType eventType, EventMechanism mechanism, CancellationToken cancellationToken, bool async) =>
        RunAsync((eventType, mechanism), static (s, a, ct, async) => s.EnableEventCoreAsync(a.eventType, a.mechanism, ct, async),
            cancellationToken, async);

    private async ValueTask EnableEventCoreAsync(EventType eventType, EventMechanism mechanism, CancellationToken cancellationToken, bool async)
    {
        if (eventType == EventType.AllEnabled || !SupportedEventTypes.Contains(eventType))
            throw new VisaException(VisaStatus.ErrorInvalidEvent, $"{eventType} is not supported by {ResourceName}.");
        if (mechanism == EventMechanism.None || (mechanism & ~(EventMechanism.All | EventMechanism.SuspendHandler)) != 0 ||
            (mechanism.HasFlag(EventMechanism.Handler) && mechanism.HasFlag(EventMechanism.SuspendHandler)))
            throw new VisaException(VisaStatus.ErrorInvalidMechanism);

        bool first;
        List<VisaEventArgs>? resumed = null;
        lock (_eventSync)
        {
            first = !_enabledEvents.TryGetValue(eventType, out var current);
            var updated = current | mechanism;
            if (mechanism.HasFlag(EventMechanism.Handler))
            {
                updated &= ~EventMechanism.SuspendHandler;
                resumed = _suspendedEvents.Where(e => e.EventType == eventType).ToList();
                _suspendedEvents.RemoveAll(e => e.EventType == eventType);
            }
            else if (mechanism.HasFlag(EventMechanism.SuspendHandler))
            {
                updated &= ~EventMechanism.Handler;
            }
            _enabledEvents[eventType] = updated;
        }

        if (first)
        {
            try
            {
                await OnEventEnabledAsync(eventType, cancellationToken, async).ConfigureAwait(false);
            }
            catch
            {
                lock (_eventSync) _enabledEvents.Remove(eventType);
                throw;
            }
        }

        if (resumed is not null)
            foreach (var e in resumed) Dispatch(e);
    }

    /// <summary>
    /// Disables an event type for <paramref name="mechanism"/> (viDisableEvent); by default for every mechanism.
    /// <see cref="EventType.AllEnabled"/> disables every enabled type. Events already queued stay in the queue until
    /// <see cref="DiscardEvents"/>; waiters in <see cref="WaitOnEvent"/> for a type that is no longer queued get
    /// VI_ERROR_NENABLED.
    /// </summary>
    public void DisableEvent(EventType eventType, EventMechanism mechanism = EventMechanism.All | EventMechanism.SuspendHandler) =>
        AsyncIO.Complete(DisableEventAsync(eventType, mechanism, CancellationToken.None, async: false));

    /// <summary>Disables an event type for every mechanism without blocking; see <see cref="DisableEvent"/>.</summary>
    public Task DisableEventAsync(EventType eventType, CancellationToken cancellationToken = default) =>
        DisableEventAsync(eventType, EventMechanism.All | EventMechanism.SuspendHandler, cancellationToken, async: true).AsTask();

    /// <summary>Disables an event type (viDisableEvent) without blocking; see <see cref="DisableEvent"/>.</summary>
    public Task DisableEventAsync(EventType eventType, EventMechanism mechanism, CancellationToken cancellationToken = default) =>
        DisableEventAsync(eventType, mechanism, cancellationToken, async: true).AsTask();

    private ValueTask DisableEventAsync(EventType eventType, EventMechanism mechanism, CancellationToken cancellationToken, bool async) =>
        RunAsync((eventType, mechanism), static async (s, a, ct, async) =>
        {
            var disabled = new List<EventType>();
            lock (s._eventSync)
            {
                var types = a.eventType == EventType.AllEnabled ? s._enabledEvents.Keys.ToList() : [a.eventType];
                foreach (var type in types)
                {
                    if (!s._enabledEvents.TryGetValue(type, out var current)) continue;
                    var updated = current & ~a.mechanism;
                    if (updated == EventMechanism.None)
                    {
                        s._enabledEvents.Remove(type);
                        disabled.Add(type);
                    }
                    else
                    {
                        s._enabledEvents[type] = updated;
                    }
                }
                s._eventChanged.Pulse();
            }
            foreach (var type in disabled)
            {
                try { await s.OnEventDisabledAsync(type, ct, async).ConfigureAwait(false); } catch { /* best effort */ }
            }
        }, cancellationToken, async);

    /// <summary>
    /// Discards pending events of a type (viDiscardEvents): those in the queue (<see cref="EventMechanism.Queue"/>)
    /// and those held for handlers (<see cref="EventMechanism.SuspendHandler"/>). <see cref="EventType.AllEnabled"/>
    /// matches every type.
    /// </summary>
    public void DiscardEvents(EventType eventType, EventMechanism mechanism = EventMechanism.All | EventMechanism.SuspendHandler)
    {
        ThrowIfDisposed();
        lock (_eventSync)
        {
            bool Matches(VisaEventArgs e) => eventType == EventType.AllEnabled || e.EventType == eventType;
            if (mechanism.HasFlag(EventMechanism.Queue))
            {
                var node = _eventQueue.First;
                while (node is not null)
                {
                    var next = node.Next;
                    if (Matches(node.Value)) _eventQueue.Remove(node);
                    node = next;
                }
            }
            if (mechanism.HasFlag(EventMechanism.SuspendHandler)) _suspendedEvents.RemoveAll(Matches);
        }
    }

    /// <summary>
    /// Takes the oldest queued event of a type (viWaitOnEvent), waiting up to <paramref name="timeoutMilliseconds"/>
    /// (-1: no limit) for one to arrive. <see cref="EventType.AllEnabled"/> takes an event of any type. Throws
    /// <see cref="VisaTimeoutException"/> when none arrives in time, and VI_ERROR_NENABLED when the type is not enabled
    /// for <see cref="EventMechanism.Queue"/> (also when it is disabled during the wait).
    /// </summary>
    public VisaEventArgs WaitOnEvent(EventType eventType, int timeoutMilliseconds) =>
        AsyncIO.Complete(WaitOnEventAsync(eventType, timeoutMilliseconds, CancellationToken.None, async: false))
        ?? throw NoEvent(eventType, timeoutMilliseconds);

    /// <summary>Takes a queued event like <see cref="WaitOnEvent"/>, but returns false instead of throwing on timeout.</summary>
    public bool TryWaitOnEvent(EventType eventType, int timeoutMilliseconds, [NotNullWhen(true)] out VisaEventArgs? eventArgs)
    {
        eventArgs = AsyncIO.Complete(WaitOnEventAsync(eventType, timeoutMilliseconds, CancellationToken.None, async: false));
        return eventArgs is not null;
    }

    /// <summary>Takes a queued event without holding a thread while it waits; see <see cref="WaitOnEvent"/>.</summary>
    public async Task<VisaEventArgs> WaitOnEventAsync(EventType eventType, int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        await WaitOnEventAsync(eventType, timeoutMilliseconds, cancellationToken, async: true).ConfigureAwait(false)
        ?? throw NoEvent(eventType, timeoutMilliseconds);

    /// <summary>
    /// Takes a queued event without holding a thread while it waits; returns null instead of throwing on timeout.
    /// See <see cref="WaitOnEvent"/>.
    /// </summary>
    public async Task<VisaEventArgs?> TryWaitOnEventAsync(EventType eventType, int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        await WaitOnEventAsync(eventType, timeoutMilliseconds, cancellationToken, async: true).ConfigureAwait(false);

    private static VisaTimeoutException NoEvent(EventType eventType, int timeoutMilliseconds) =>
        new($"No {eventType} event occurred within {timeoutMilliseconds} ms.");

    /// <summary>Waits for a queued event; returns null on timeout.</summary>
    private ValueTask<VisaEventArgs?> WaitOnEventAsync(EventType eventType, int timeoutMilliseconds, CancellationToken cancellationToken, bool async) =>
        RunAsync((eventType, timeoutMilliseconds), static async (s, a, ct, async) =>
        {
            var deadline = Deadline.FromMilliseconds(a.timeoutMilliseconds).WithCancellation(ct);
            while (true)
            {
                Task next;
                lock (s._eventSync)
                {
                    s.ThrowIfDisposed();
                    if (a.eventType != EventType.AllEnabled &&
                        (!s._enabledEvents.TryGetValue(a.eventType, out var mechanism) || !mechanism.HasFlag(EventMechanism.Queue)))
                        throw new VisaException(VisaStatus.ErrorNotEnabled, $"{a.eventType} is not enabled for the queue mechanism.");
                    for (var node = s._eventQueue.First; node is not null; node = node.Next)
                    {
                        if (a.eventType == EventType.AllEnabled || node.Value.EventType == a.eventType)
                        {
                            s._eventQueue.Remove(node);
                            return node.Value;
                        }
                    }
                    next = s._eventChanged.Next;
                }
                if (!await ChangeSignal.WaitAsync(next, deadline, async).ConfigureAwait(false))
                {
                    ct.ThrowIfCancellationRequested();
                    return (VisaEventArgs?)null;
                }
            }
        }, cancellationToken, async);

    /// <summary>Called by transports when an event occurs. Delivers it to the enabled mechanisms.</summary>
    private protected void RaiseEvent(VisaEventArgs e)
    {
        if (IsDisposed) return;
        EventMechanism mechanism;
        lock (_eventSync)
        {
            if (!_enabledEvents.TryGetValue(e.EventType, out mechanism)) return;
            if (mechanism.HasFlag(EventMechanism.Queue))
            {
                if (_eventQueue.Count < MaximumQueueLength) _eventQueue.AddLast(e);
                _eventChanged.Pulse();
            }
            if (mechanism.HasFlag(EventMechanism.SuspendHandler) && _suspendedEvents.Count < MaximumQueueLength)
                _suspendedEvents.Add(e);
        }
        if (mechanism.HasFlag(EventMechanism.Handler)) Dispatch(e);
    }

    private void Dispatch(VisaEventArgs e)
    {
        _dispatcher.Post(() =>
        {
            if (e is ServiceRequestEventArgs srq) ServiceRequest?.Invoke(this, srq);
            VisaEvent?.Invoke(this, e);
        });
    }

    /// <summary>Returns true if the event type is enabled for any mechanism.</summary>
    private protected bool IsEventEnabled(EventType type)
    {
        lock (_eventSync) return _enabledEvents.ContainsKey(type);
    }

    /// <summary>On disposal: disables every event type, empties the queue and wakes waiters. Returns the types that were enabled.</summary>
    private EventType[] DisableAllEvents()
    {
        lock (_eventSync)
        {
            var enabled = _enabledEvents.Keys.ToArray();
            _enabledEvents.Clear();
            _eventQueue.Clear();
            _suspendedEvents.Clear();
            _eventChanged.Pulse();
            return enabled;
        }
    }

    /// <summary>
    /// Hook: start whatever machinery produces this event type (e.g. an SRQ channel). Receives the caller's token.
    /// The default calls the overload without a token.
    /// </summary>
    private protected virtual ValueTask OnEventEnabledAsync(EventType eventType, CancellationToken cancellationToken, bool async) =>
        OnEventEnabledAsync(eventType, async);

    /// <summary>Hook for transports that do not observe cancellation; see the overload with a token.</summary>
    private protected virtual ValueTask OnEventEnabledAsync(EventType eventType, bool async) => default;

    /// <summary>
    /// Hook: stop the machinery for this event type. Receives the caller's token (none on disposal). The default calls
    /// the overload without a token.
    /// </summary>
    private protected virtual ValueTask OnEventDisabledAsync(EventType eventType, CancellationToken cancellationToken, bool async) =>
        OnEventDisabledAsync(eventType, async);

    /// <summary>Hook for transports that do not observe cancellation; see the overload with a token.</summary>
    private protected virtual ValueTask OnEventDisabledAsync(EventType eventType, bool async) => default;
}

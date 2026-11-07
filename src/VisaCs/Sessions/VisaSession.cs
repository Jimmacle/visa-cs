using VisaCs.Internal;

namespace VisaCs;

/// <summary>
/// Base class of every VISA session: resource identity, timeout, locking (viLock/viUnlock), events
/// (viEnableEvent/viWaitOnEvent/handlers) and generic attribute access (viGetAttribute/viSetAttribute).
/// </summary>
/// <remarks>
/// <para>
/// <b>Synchronous and asynchronous forms.</b> Operations that talk to the device come in pairs such as
/// <c>Query</c>/<c>QueryAsync</c>. The asynchronous form holds no thread while it waits and takes a
/// <see cref="CancellationToken"/> as its last parameter. Synchronous calls cannot be cancelled; they end at their
/// timeout.
/// </para>
/// <para>
/// <b>Thread safety.</b> Every member may be called from any thread. Operations that talk to the device run one at
/// a time per session: a call waits until the running one has finished (without a time limit, but an asynchronous
/// call can be cancelled while it waits, which does not disturb the running one). Waiting calls are not guaranteed to
/// run in the order they were made. Locking, event and attribute members do not wait for a running operation.
/// Settings such as <see cref="Timeout"/> or the termination character are read when each transfer starts: change
/// them between operations, not during one.
/// </para>
/// <para>
/// <b>Errors.</b> Failures throw <see cref="VisaException"/> with the VISA status code in
/// <see cref="VisaException.Status"/>. A timeout throws <see cref="VisaTimeoutException"/>, whose
/// <see cref="VisaTimeoutException.BytesTransferred"/> tells how much of a read had arrived. An operation on a
/// resource that another session has locked fails at once with VI_ERROR_RSRC_LOCKED. Cancelling the token of an
/// asynchronous call throws <see cref="OperationCanceledException"/>. After a timeout or a cancellation the session
/// stays open, but the device may still send (the rest of) its response: call <c>Clear()</c> before the next
/// command. Invalid arguments throw the usual <see cref="ArgumentException"/>s.
/// </para>
/// <para>
/// <b>Locking.</b> Locks (<see cref="LockExclusive"/>, <see cref="LockShared"/>) nest: each needs a matching
/// <see cref="Unlock"/>. They are tracked per process by canonical resource name, so every session of the process
/// that opened the same name, through any resource manager, is excluded. Another process, or a different spelling of
/// the same instrument (a host name instead of its IP address), is excluded only where the protocol also locks the
/// device itself (VXI-11 exclusive locks, HiSLIP).
/// </para>
/// <para>
/// <b>Disposal.</b> <see cref="Dispose"/> (also <see cref="Close"/> and <see cref="DisposeAsync()"/>) releases the
/// session's locks, disables its events and closes the connection. It does not wait for a running operation: that
/// operation fails with a <see cref="VisaException"/> (usually VI_ERROR_CONN_LOST), and calls made afterwards throw
/// VI_ERROR_INV_OBJECT. Disposing a session twice does nothing.
/// </para>
/// </remarks>
public abstract partial class VisaSession : IDisposable, IAsyncDisposable
{
    private bool _disposed;

    /// <summary>Serializes I/O on this session. Not reentrant: public operations enter once.</summary>
    private protected readonly IOGate IOGate = new();

    private protected VisaSession(ResourceManager manager, ResourceName resource)
    {
        ResourceManager = manager;
        Resource = resource;
        TimeoutMilliseconds = Math.Max(-1, manager.Options.DefaultTimeoutMilliseconds);
    }

    /// <summary>The resource manager that opened this session.</summary>
    public ResourceManager ResourceManager { get; }

    /// <summary>
    /// The parsed resource name. For USB resources opened without an interface number, it names the interface that
    /// was claimed once the session is open.
    /// </summary>
    public ResourceName Resource { get; private protected set; }

    /// <summary>Canonical resource name, e.g. <c>TCPIP0::192.168.1.5::inst0::INSTR</c> (VI_ATTR_RSRC_NAME).</summary>
    public string ResourceName => Resource.Canonical;

    /// <summary>The hardware interface (VI_ATTR_INTF_TYPE).</summary>
    public HardwareInterfaceType HardwareInterfaceType => Resource.InterfaceType;

    /// <summary>The board number of the interface, e.g. 0 for <c>GPIB0</c> (VI_ATTR_INTF_NUM).</summary>
    public int HardwareInterfaceNumber => Resource.Board;

    /// <summary>The resource class (VI_ATTR_RSRC_CLASS, where it is the string <c>INSTR</c>, <c>SOCKET</c>, ...).</summary>
    public ResourceClass ResourceClass => Resource.ResourceClass;

    /// <summary>Arbitrary data for the application; not used by the library (VI_ATTR_USER_DATA).</summary>
    public object? UserData { get; set; }

    /// <summary>True once the session has been closed.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed);

    /// <summary>
    /// I/O timeout in milliseconds for each transfer (VI_ATTR_TMO_VALUE): -1 waits without limit, 0 fails at once
    /// unless data is already there. Initially <see cref="ResourceManagerOptions.DefaultTimeoutMilliseconds"/>
    /// (2000 by default). Values below -1 throw <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    public int TimeoutMilliseconds
    {
        get;
        set
        {
            if (value < -1) throw new ArgumentOutOfRangeException(nameof(value), "Use -1 for an infinite timeout.");
            field = value;
        }
    }

    /// <summary>
    /// <see cref="TimeoutMilliseconds"/> as a <see cref="TimeSpan"/>; <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
    /// for no limit. Other negative values are treated as zero.
    /// </summary>
    public TimeSpan Timeout
    {
        get => TimeoutMilliseconds < 0 ? System.Threading.Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(TimeoutMilliseconds);
        set => TimeoutMilliseconds = value == System.Threading.Timeout.InfiniteTimeSpan
            ? -1
            : (int)Math.Clamp(value.TotalMilliseconds, 0, int.MaxValue);
    }

    /// <summary>A deadline <see cref="TimeoutMilliseconds"/> from now that also expires when the caller cancels.</summary>
    private protected Deadline NewDeadline(CancellationToken cancellationToken = default) =>
        Deadline.FromMilliseconds(TimeoutMilliseconds).WithCancellation(cancellationToken);

    /// <summary>Key used for process-wide lock bookkeeping.</summary>
    internal string LockKey => Resource.Canonical;

    /// <inheritdoc/>
    public override string ToString() => $"{GetType().Name}({ResourceName})";

    // ------------------------------------------------------------------ running operations

    /// <summary>
    /// Runs a public operation that talks to the device: waits for the session's I/O gate, checks that the session is
    /// open and not locked by another session, runs <paramref name="operation"/> and maps its errors (see
    /// <see cref="TryMapError"/>). While it runs, cancelling <paramref name="cancellationToken"/> also calls
    /// <see cref="OnOperationCanceled"/>.
    /// </summary>
    private protected async ValueTask<TResult> ExecuteAsync<TState, TResult>(TState state,
        Func<VisaSession, TState, CancellationToken, bool, ValueTask<TResult>> operation, CancellationToken cancellationToken, bool async)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using (await IOGate.EnterAsync(async, cancellationToken).ConfigureAwait(false))
            {
                EnsureAccess();
                // Registered only now: cancelling a call that still waits for the gate must not abort the running one.
                using var registration = RegisterCancellation(cancellationToken);
                return await operation(this, state, cancellationToken, async).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (TryMapError(ex, cancellationToken, out var mapped))
        {
            throw mapped;
        }
    }

    /// <summary><see cref="ExecuteAsync{TState, TResult}"/> for operations without a result.</summary>
    private protected async ValueTask ExecuteAsync<TState>(TState state,
        Func<VisaSession, TState, CancellationToken, bool, ValueTask> operation, CancellationToken cancellationToken, bool async) =>
        await ExecuteAsync((operation, state), static async (s, a, ct, async) =>
        {
            await a.operation(s, a.state, ct, async).ConfigureAwait(false);
            return true;
        }, cancellationToken, async).ConfigureAwait(false);

    /// <summary>
    /// Runs an operation that must not wait for the I/O gate (locking, events, protocol side channels): checks that
    /// the session is open and maps errors like <see cref="ExecuteAsync{TState, TResult}"/>.
    /// </summary>
    private protected async ValueTask<TResult> RunAsync<TState, TResult>(TState state,
        Func<VisaSession, TState, CancellationToken, bool, ValueTask<TResult>> operation, CancellationToken cancellationToken, bool async)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await operation(this, state, cancellationToken, async).ConfigureAwait(false);
        }
        catch (Exception ex) when (TryMapError(ex, cancellationToken, out var mapped))
        {
            throw mapped;
        }
    }

    /// <summary><see cref="RunAsync{TState, TResult}"/> for operations without a result.</summary>
    private protected async ValueTask RunAsync<TState>(TState state,
        Func<VisaSession, TState, CancellationToken, bool, ValueTask> operation, CancellationToken cancellationToken, bool async) =>
        await RunAsync((operation, state), static async (s, a, ct, async) =>
        {
            await a.operation(s, a.state, ct, async).ConfigureAwait(false);
            return true;
        }, cancellationToken, async).ConfigureAwait(false);

    /// <summary>
    /// Synchronous form of <see cref="ExecuteAsync{TState, TResult}"/> for operations that only exist as blocking calls:
    /// waits for the I/O gate, checks access and maps errors.
    /// </summary>
    private protected TResult Execute<TResult>(Func<TResult> operation)
    {
        try
        {
            using (IOGate.Enter())
            {
                EnsureAccess();
                return operation();
            }
        }
        catch (Exception ex) when (TryMapError(ex, CancellationToken.None, out var mapped))
        {
            throw mapped;
        }
    }

    /// <summary><see cref="Execute{TResult}"/> for operations without a result.</summary>
    private protected void Execute(Action operation) => Execute(() =>
    {
        operation();
        return true;
    });

    /// <summary>
    /// The exception a public operation throws for <paramref name="error"/>: <see cref="OperationCanceledException"/>
    /// when the caller's token ended it (inside the library cancellation looks like a timeout), the error itself for
    /// VISA and cancellation exceptions, and a <see cref="VisaException"/> for anything else. Returns false when
    /// the error should propagate unchanged.
    /// </summary>
    private protected static bool TryMapError(Exception error, CancellationToken cancellationToken, out Exception mapped)
    {
        if (error is VisaException or OperationCanceledException)
        {
            mapped = cancellationToken.IsCancellationRequested ? AsyncIO.Canceled(error, cancellationToken) : error;
            return !ReferenceEquals(mapped, error);
        }
        mapped = VisaException.From(error);
        return true;
    }

    /// <summary>
    /// Called (on the thread that cancels) when the token of an operation in progress is cancelled. The
    /// operation's deadline expires at the same moment; protocols that can also abort the transfer on the device
    /// (VXI-11 device_abort) override this to do so.
    /// </summary>
    private protected virtual void OnOperationCanceled()
    {
    }

    private protected CancellationTokenRegistration RegisterCancellation(CancellationToken cancellationToken) =>
        cancellationToken.CanBeCanceled
            ? cancellationToken.UnsafeRegister(static s => ((VisaSession)s!).NotifyCanceled(), this)
            : default;

    private void NotifyCanceled()
    {
        try { OnOperationCanceled(); } catch { /* best effort */ }
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Connects to the resource. Called once by the resource manager.</summary>
    internal async ValueTask OpenAsync(int connectTimeoutMs, CancellationToken cancellationToken, bool async)
    {
        try
        {
            await OpenCoreAsync(connectTimeoutMs, cancellationToken, async).ConfigureAwait(false);
        }
        catch (Exception ex) when (TryMapError(ex, cancellationToken, out var mapped))
        {
            throw mapped;
        }
    }

    private protected abstract ValueTask OpenCoreAsync(int connectTimeoutMs, CancellationToken cancellationToken, bool async);

    /// <summary>Releases the transport. Network protocols may say goodbye to the device first.</summary>
    private protected abstract ValueTask CloseCoreAsync(bool async);

    /// <summary>Closes the session (viClose); the same as <see cref="Dispose"/>.</summary>
    public void Close() => Dispose();

    /// <summary>Closes the session (viClose) without blocking; the same as <see cref="DisposeAsync()"/>.</summary>
    public ValueTask CloseAsync() => DisposeAsync();

    /// <summary>
    /// Closes the session (viClose): releases its locks, disables its events and closes the connection. See the
    /// type's remarks for what happens to an operation that is still running.
    /// </summary>
    public void Dispose() => AsyncIO.Complete(DisposeAsync(async: false));

    /// <summary>Closes the session like <see cref="Dispose"/>, without blocking while network protocols say goodbye.</summary>
    public ValueTask DisposeAsync() => DisposeAsync(async: true);

    private async ValueTask DisposeAsync(bool async)
    {
        if (Interlocked.Exchange(ref _disposed, true)) return;
        try
        {
            foreach (var type in ResourceLockTable.Instance.ReleaseAll(LockKey, this))
            {
                try { await ReleaseDeviceLockAsync(type, CancellationToken.None, async).ConfigureAwait(false); } catch { /* best effort */ }
            }
            foreach (var type in DisableAllEvents())
            {
                try { await OnEventDisabledAsync(type, CancellationToken.None, async).ConfigureAwait(false); } catch { /* best effort */ }
            }
            await CloseCoreAsync(async).ConfigureAwait(false);
        }
        finally
        {
            ResourceManager.Unregister(this);
            GC.SuppressFinalize(this);
        }
    }

    private protected void ThrowIfDisposed()
    {
        if (IsDisposed) throw new VisaException(VisaStatus.ErrorInvalidObject, "The session is closed.");
    }

    /// <summary>Throws if the session is closed or another session holds a lock on the resource.</summary>
    private protected void EnsureAccess()
    {
        ThrowIfDisposed();
        ResourceLockTable.Instance.CheckAccess(LockKey, this);
    }
}

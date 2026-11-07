namespace VisaCs.Internal;

/// <summary>
/// Process-wide VISA lock state per resource (viLock / viUnlock semantics): nested exclusive and shared
/// locks, shared lock access keys, and access checks for operations from sessions that do not hold the lock.
/// Protocols that support device-side locking (VXI-11, HiSLIP) additionally lock the device itself.
/// Entries are keyed by the canonical resource name, ignoring case.
/// </summary>
internal sealed class ResourceLockTable
{
    public static ResourceLockTable Instance { get; } = new();

    private readonly Lock _sync = new();
    private readonly ChangeSignal _changed = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private int _keyCounter;

    private sealed class Entry
    {
        public VisaSession? ExclusiveOwner;
        public int ExclusiveCount;
        public string? SharedKey;
        public readonly Dictionary<VisaSession, int> SharedHolders = new(ReferenceEqualityComparer.Instance);
        public bool IsEmpty => ExclusiveOwner is null && SharedHolders.Count == 0;
    }

    public readonly record struct AcquireResult(bool FirstForSession, string? Key);

    /// <summary>Acquires a lock, waiting until the deadline for other sessions to release theirs.</summary>
    public async ValueTask<AcquireResult> AcquireAsync(string resource, VisaSession session, LockType type, string? requestedKey,
        Deadline deadline, bool async)
    {
        while (true)
        {
            Task next;
            lock (_sync)
            {
                // Checked under the table lock: Dispose marks the session closed before it releases its locks, so
                // a waiter either acquires before that release (which then drops the lock again) or stops here.
                if (session.IsDisposed) throw new VisaException(VisaStatus.ErrorInvalidObject, "The session is closed.");
                if (!_entries.TryGetValue(resource, out var e))
                {
                    e = new Entry();
                    _entries[resource] = e;
                }
                if (TryAcquire(e, session, type, requestedKey) is { } result) return result;
                if (e.IsEmpty) _entries.Remove(resource);
                next = _changed.Next;
            }
            if (!await ChangeSignal.WaitAsync(next, deadline, async).ConfigureAwait(false))
                throw new VisaTimeoutException($"The {(type == LockType.Exclusive ? "exclusive" : "shared")} lock on {resource} could not be acquired: the resource is locked by another session.");
        }
    }

    private AcquireResult? TryAcquire(Entry e, VisaSession session, LockType type, string? requestedKey)
    {
        if (type == LockType.Exclusive)
        {
            if (ReferenceEquals(e.ExclusiveOwner, session))
            {
                e.ExclusiveCount++;
                return new AcquireResult(false, null);
            }
            bool otherShared = e.SharedHolders.Keys.Any(s => !ReferenceEquals(s, session));
            if (e.ExclusiveOwner is null && !otherShared)
            {
                e.ExclusiveOwner = session;
                e.ExclusiveCount = 1;
                return new AcquireResult(true, null);
            }
            return null;
        }

        if (e.SharedHolders.TryGetValue(session, out int count))
        {
            if (requestedKey is not null && !string.Equals(requestedKey, e.SharedKey, StringComparison.Ordinal))
                throw new VisaException(VisaStatus.ErrorInvalidAccessKey, "The session already holds a shared lock with a different access key.");
            e.SharedHolders[session] = count + 1;
            return new AcquireResult(false, e.SharedKey);
        }
        bool exclusiveByOther = e.ExclusiveOwner is not null && !ReferenceEquals(e.ExclusiveOwner, session);
        if (exclusiveByOther) return null;
        if (e.SharedKey is null)
        {
            e.SharedKey = string.IsNullOrEmpty(requestedKey) ? GenerateKey() : requestedKey;
            e.SharedHolders[session] = 1;
            return new AcquireResult(true, e.SharedKey);
        }
        if (requestedKey is not null && string.Equals(requestedKey, e.SharedKey, StringComparison.Ordinal))
        {
            e.SharedHolders[session] = 1;
            return new AcquireResult(true, e.SharedKey);
        }
        return null;
    }

    private string GenerateKey() => $"VisaCs-{Environment.ProcessId}-{Interlocked.Increment(ref _keyCounter)}-{Guid.NewGuid():N}"[..40];

    /// <summary>Releases one level of lock. Returns the type released and whether it was the last level.</summary>
    public (LockType Type, bool Last) Release(string resource, VisaSession session)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(resource, out var e))
                throw new VisaException(VisaStatus.ErrorSessionNotLocked);
            try
            {
                if (ReferenceEquals(e.ExclusiveOwner, session))
                {
                    if (--e.ExclusiveCount > 0) return (LockType.Exclusive, false);
                    e.ExclusiveOwner = null;
                    return (LockType.Exclusive, true);
                }
                if (e.SharedHolders.TryGetValue(session, out int count))
                {
                    if (count > 1)
                    {
                        e.SharedHolders[session] = count - 1;
                        return (LockType.Shared, false);
                    }
                    e.SharedHolders.Remove(session);
                    if (e.SharedHolders.Count == 0) e.SharedKey = null;
                    return (LockType.Shared, true);
                }
                throw new VisaException(VisaStatus.ErrorSessionNotLocked);
            }
            finally
            {
                if (e.IsEmpty) _entries.Remove(resource);
                _changed.Pulse();
            }
        }
    }

    /// <summary>Drops every lock the session holds (on close). Returns the lock types that were held.</summary>
    public List<LockType> ReleaseAll(string resource, VisaSession session)
    {
        var released = new List<LockType>();
        lock (_sync)
        {
            if (!_entries.TryGetValue(resource, out var e)) return released;
            if (ReferenceEquals(e.ExclusiveOwner, session))
            {
                e.ExclusiveOwner = null;
                e.ExclusiveCount = 0;
                released.Add(LockType.Exclusive);
            }
            if (e.SharedHolders.Remove(session))
            {
                if (e.SharedHolders.Count == 0) e.SharedKey = null;
                released.Add(LockType.Shared);
            }
            if (e.IsEmpty) _entries.Remove(resource);
            _changed.Pulse();
        }
        return released;
    }

    /// <summary>Throws VI_ERROR_RSRC_LOCKED if another session holds a lock that excludes this session.</summary>
    public void CheckAccess(string resource, VisaSession session)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(resource, out var e)) return;
            if (e.ExclusiveOwner is not null && !ReferenceEquals(e.ExclusiveOwner, session))
                throw new VisaException(VisaStatus.ErrorResourceLocked, $"{resource} is locked exclusively by another session.");
            if (e.SharedHolders.Count > 0 && !e.SharedHolders.ContainsKey(session) && !ReferenceEquals(e.ExclusiveOwner, session))
                throw new VisaException(VisaStatus.ErrorResourceLocked, $"{resource} is locked by a shared lock this session does not hold.");
        }
    }

    public ResourceLockState GetState(string resource)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(resource, out var e)) return ResourceLockState.NoLock;
            if (e.ExclusiveOwner is not null) return ResourceLockState.ExclusiveLock;
            return e.SharedHolders.Count > 0 ? ResourceLockState.SharedLock : ResourceLockState.NoLock;
        }
    }
}

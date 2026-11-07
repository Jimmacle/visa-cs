using VisaCs.Internal;

namespace VisaCs;

// Locking (viLock/viUnlock). The rules are in the type's remarks.
public abstract partial class VisaSession
{
    /// <summary>
    /// Lock state of the resource as this process sees it (VI_ATTR_RSRC_LOCK_STATE); locks held by other processes
    /// are not reported.
    /// </summary>
    public ResourceLockState ResourceLockState => ResourceLockTable.Instance.GetState(LockKey);

    /// <summary>
    /// Acquires an exclusive lock (viLock with VI_EXCLUSIVE_LOCK), waiting up to <paramref name="timeoutMilliseconds"/>
    /// (-1: no limit) for other sessions to release theirs. Other sessions cannot use the resource until the
    /// matching <see cref="Unlock"/>. Throws <see cref="VisaTimeoutException"/> when the lock is not granted in time.
    /// </summary>
    public void LockExclusive(int timeoutMilliseconds) =>
        AsyncIO.Complete(LockAsync(LockType.Exclusive, timeoutMilliseconds, null, CancellationToken.None, async: false));

    /// <summary>Acquires an exclusive lock without holding a thread while it waits; see <see cref="LockExclusive"/>.</summary>
    public Task LockExclusiveAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        LockAsync(LockType.Exclusive, timeoutMilliseconds, null, cancellationToken, async: true).AsTask();

    /// <summary>
    /// Acquires a shared lock (viLock with VI_SHARED_LOCK) and returns its access key. Sessions that pass the same key
    /// share the lock; all others are excluded. With <paramref name="requestedKey"/> null, the first holder gets a
    /// generated key and later sessions must present it; while a shared lock is held, a session that passes no key or
    /// a different key waits up to <paramref name="timeoutMilliseconds"/> (-1: no limit) and then throws
    /// <see cref="VisaTimeoutException"/>. A session that already holds the shared lock and asks again with a
    /// different key gets VI_ERROR_INV_ACCESS_KEY.
    /// </summary>
    public string LockShared(int timeoutMilliseconds, string? requestedKey = null) =>
        AsyncIO.Complete(LockAsync(LockType.Shared, timeoutMilliseconds, requestedKey, CancellationToken.None, async: false))!;

    /// <summary>Acquires a shared lock with a generated or the existing key; see <see cref="LockShared"/>.</summary>
    public Task<string> LockSharedAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        LockSharedAsync(timeoutMilliseconds, null, cancellationToken);

    /// <summary>Acquires a shared lock without holding a thread while it waits; see <see cref="LockShared"/>.</summary>
    public async Task<string> LockSharedAsync(int timeoutMilliseconds, string? requestedKey, CancellationToken cancellationToken = default) =>
        (await LockAsync(LockType.Shared, timeoutMilliseconds, requestedKey, cancellationToken, async: true).ConfigureAwait(false))!;

    private ValueTask<string?> LockAsync(LockType type, int timeoutMilliseconds, string? requestedKey,
        CancellationToken cancellationToken, bool async) =>
        RunAsync((type, timeoutMilliseconds, requestedKey), static async (s, a, ct, async) =>
        {
            var deadline = Deadline.FromMilliseconds(a.timeoutMilliseconds).WithCancellation(ct);
            var result = await ResourceLockTable.Instance.AcquireAsync(s.LockKey, s, a.type, a.requestedKey, deadline, async).ConfigureAwait(false);
            if (result.FirstForSession)
            {
                try
                {
                    await s.AcquireDeviceLockAsync(a.type, result.Key, a.timeoutMilliseconds, deadline, async).ConfigureAwait(false);
                }
                catch
                {
                    try { ResourceLockTable.Instance.Release(s.LockKey, s); } catch (VisaException) { /* already released by Dispose */ }
                    throw;
                }
            }
            return result.Key;
        }, cancellationToken, async);

    /// <summary>
    /// Releases one level of lock (viUnlock). Releasing the last level of a VXI-11 or HiSLIP lock also unlocks the
    /// device. Throws VI_ERROR_SESN_NLOCKED if the session holds no lock.
    /// </summary>
    public void Unlock() => AsyncIO.Complete(UnlockAsync(CancellationToken.None, async: false));

    /// <summary>
    /// Releases one level of lock without blocking; see <see cref="Unlock"/>. The lock is released in this process at
    /// once; cancelling only stops waiting for the device's reply to the unlock request.
    /// </summary>
    public Task UnlockAsync(CancellationToken cancellationToken = default) => UnlockAsync(cancellationToken, async: true).AsTask();

    private ValueTask UnlockAsync(CancellationToken cancellationToken, bool async) =>
        RunAsync(0, static async (s, _, ct, async) =>
        {
            var (type, last) = ResourceLockTable.Instance.Release(s.LockKey, s);
            if (last) await s.ReleaseDeviceLockAsync(type, ct, async).ConfigureAwait(false);
        }, cancellationToken, async);

    /// <summary>Hook for protocols with device-side locking. Called when this session first acquires a lock type.</summary>
    private protected virtual ValueTask AcquireDeviceLockAsync(LockType type, string? key, int timeoutMilliseconds, Deadline deadline, bool async) =>
        default;

    /// <summary>
    /// Hook for protocols with device-side locking. Called when this session fully releases a lock type, with the
    /// caller's token (none when the session is being disposed). The default calls the overload without a token.
    /// </summary>
    private protected virtual ValueTask ReleaseDeviceLockAsync(LockType type, CancellationToken cancellationToken, bool async) =>
        ReleaseDeviceLockAsync(type, async);

    /// <summary>Hook for protocols with device-side locking that do not observe cancellation; see the overload with a token.</summary>
    private protected virtual ValueTask ReleaseDeviceLockAsync(LockType type, bool async) => default;
}

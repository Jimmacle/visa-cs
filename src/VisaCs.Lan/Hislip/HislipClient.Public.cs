using VisaCs.Internal;

namespace VisaCs.Lan;

/// <summary>Remote/local control requests of HiSLIP AsyncRemoteLocalControl (the same codes as VISA's VI_GPIB_REN_*).</summary>
public enum HislipRemoteLocalMode : byte
{
    /// <summary>Disable remote (deassert REN).</summary>
    DisableRemote = 0,
    /// <summary>Enable remote (assert REN).</summary>
    EnableRemote = 1,
    /// <summary>Disable remote and go to local.</summary>
    DisableRemoteGoToLocal = 2,
    /// <summary>Enable remote and go to remote.</summary>
    EnableRemoteGoToRemote = 3,
    /// <summary>Enable remote and lock out local.</summary>
    EnableRemoteLocalLockout = 4,
    /// <summary>Enable remote, go to remote and lock out local.</summary>
    EnableRemoteGoToRemoteLocalLockout = 5,
    /// <summary>Go to local without changing the remote enable state.</summary>
    GoToLocal = 6,
}

/// <summary>The server's lock state (AsyncLockInfo).</summary>
/// <param name="ExclusiveLockGranted">Whether some client holds the exclusive lock.</param>
/// <param name="ClientsHoldingLocks">How many clients hold a lock (exclusive or shared).</param>
public readonly record struct HislipLockInfo(bool ExclusiveLockGranted, int ClientsHoldingLocks);

/// <summary>Result of a message-based read (<see cref="HislipClient.Read"/>, <see cref="VicpClient.Read"/>).</summary>
/// <param name="Count">Number of bytes stored in the buffer.</param>
/// <param name="End">True when the last of these bytes ended a message (END).</param>
public readonly record struct LanReadResult(int Count, bool End);

public sealed partial class HislipClient
{
    /// <summary>The default HiSLIP port (4880).</summary>
    public const int DefaultPort = 4880;

    /// <summary>
    /// Opens both channels to <paramref name="host"/>, applies <paramref name="security"/> (TLS and SASL authentication
    /// as its <see cref="HislipSecurityOptions.Encryption"/> mode and the server require) and announces
    /// <paramref name="maximumMessageSize"/>. The timeout covers all of it.
    /// </summary>
    /// <param name="host">Host name or IP address (also the name checked against the server certificate).</param>
    /// <param name="port">The HiSLIP port.</param>
    /// <param name="subAddress">The HiSLIP sub-address, e.g. <c>hislip0</c>.</param>
    /// <param name="timeoutMilliseconds">Time for the whole connection setup (-1: no limit).</param>
    /// <param name="security">Encryption policy and credentials; null for the defaults (encrypt only when the server requires it).</param>
    /// <param name="maximumMessageSize">The largest message this client asks the server to send, in bytes.</param>
    public static HislipClient Connect(string host, int port, string subAddress, int timeoutMilliseconds, HislipSecurityOptions? security = null,
        ulong maximumMessageSize = 1024 * 1024) =>
        AsyncIO.Complete(ConnectAsync(host, port, subAddress, Deadline.FromMilliseconds(timeoutMilliseconds), maximumMessageSize, security,
            async: false));

    /// <summary>Opens a HiSLIP connection; see <see cref="Connect"/>.</summary>
    public static ValueTask<HislipClient> ConnectAsync(string host, int port, string subAddress, int timeoutMilliseconds,
        HislipSecurityOptions? security = null, ulong maximumMessageSize = 1024 * 1024, CancellationToken cancellationToken = default) =>
        LanCall.RunAsync(ConnectAsync(host, port, subAddress, LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken), maximumMessageSize,
            security, async: true), cancellationToken);

    /// <summary>
    /// Writes <paramref name="data"/> as Data messages, the last one DataEND when <paramref name="end"/>. In synchronized
    /// mode a new message makes the rest of an unread response stale: it is discarded.
    /// </summary>
    public void Write(ReadOnlyMemory<byte> data, bool end, int timeoutMilliseconds) =>
        AsyncIO.Complete(WriteCoreAsync(data, end, Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>Writes data; see <see cref="Write"/>.</summary>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, bool end, int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        LanCall.RunAsync(WriteCoreAsync(data, end, LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken), async: true), cancellationToken);

    /// <summary>
    /// Reads response data into <paramref name="buffer"/>: returns as soon as some bytes arrived, at most up to the end
    /// of the current message. A read that times out keeps its place: the next one continues the same response.
    /// </summary>
    public LanReadResult Read(Memory<byte> buffer, int timeoutMilliseconds) =>
        AsyncIO.Complete(ReadCoreAsync(buffer, Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>Reads response data; see <see cref="Read"/>.</summary>
    public ValueTask<LanReadResult> ReadAsync(Memory<byte> buffer, int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        LanCall.RunAsync(ReadCoreAsync(buffer, LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken), async: true), cancellationToken);

    /// <summary>Sends a Trigger message (in order with the data).</summary>
    public void Trigger(int timeoutMilliseconds) => AsyncIO.Complete(TriggerCoreAsync(Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>Sends a Trigger message.</summary>
    public ValueTask TriggerAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        LanCall.RunAsync(TriggerCoreAsync(LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken), async: true), cancellationToken);

    /// <summary>Reads the status byte (AsyncStatusQuery).</summary>
    public byte ReadStatusByte(int timeoutMilliseconds) =>
        AsyncIO.Complete(ReadStatusByteCoreAsync(Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>Reads the status byte.</summary>
    public ValueTask<byte> ReadStatusByteAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        LanCall.RunAsync(ReadStatusByteCoreAsync(LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken), async: true), cancellationToken);

    /// <summary>
    /// Performs the device clear transaction, which discards unread data. <paramref name="requestOverlap"/> requests
    /// overlapped (true) or synchronized (false) mode, or keeps the server's choice (null); read <see cref="OverlapMode"/>
    /// afterwards.
    /// </summary>
    public void DeviceClear(int timeoutMilliseconds, bool? requestOverlap = null) =>
        AsyncIO.Complete(DeviceClearCoreAsync(requestOverlap, Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>Performs the device clear transaction; see <see cref="DeviceClear"/>.</summary>
    public ValueTask DeviceClearAsync(int timeoutMilliseconds, bool? requestOverlap = null, CancellationToken cancellationToken = default) =>
        LanCall.RunAsync(DeviceClearCoreAsync(requestOverlap, LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken), async: true),
            cancellationToken);

    /// <summary>Sends a remote/local control request.</summary>
    public void RemoteLocal(HislipRemoteLocalMode mode, int timeoutMilliseconds) =>
        AsyncIO.Complete(RemoteLocalCoreAsync(mode, Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>Sends a remote/local control request.</summary>
    public ValueTask RemoteLocalAsync(HislipRemoteLocalMode mode, int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        LanCall.RunAsync(RemoteLocalCoreAsync(mode, LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken), async: true), cancellationToken);

    /// <summary>
    /// Requests the exclusive lock (<paramref name="sharedLockKey"/> null or empty) or a shared lock with that key. The
    /// server waits up to <paramref name="timeoutMilliseconds"/> (-1: no limit); a lock it cannot grant in time throws
    /// <see cref="VisaTimeoutException"/>. A lock granted after the request was abandoned is released again.
    /// </summary>
    public void Lock(string? sharedLockKey, int timeoutMilliseconds) =>
        AsyncIO.Complete(LockCoreAsync(sharedLockKey ?? "", timeoutMilliseconds, Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>Requests a lock; see <see cref="Lock"/>.</summary>
    public ValueTask LockAsync(string? sharedLockKey, int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        LanCall.RunAsync(LockCoreAsync(sharedLockKey ?? "", timeoutMilliseconds, LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken),
            async: true), cancellationToken);

    /// <summary>Releases this client's lock (VI_ERROR_SESN_NLOCKED when it holds none).</summary>
    public void Unlock(int timeoutMilliseconds) => AsyncIO.Complete(UnlockCoreAsync(Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>Releases this client's lock.</summary>
    public ValueTask UnlockAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        LanCall.RunAsync(UnlockCoreAsync(LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken), async: true), cancellationToken);

    /// <summary>Queries the server's lock state (AsyncLockInfo).</summary>
    public HislipLockInfo GetLockInfo(int timeoutMilliseconds) =>
        AsyncIO.Complete(LockInfoCoreAsync(Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>Queries the server's lock state.</summary>
    public ValueTask<HislipLockInfo> GetLockInfoAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        LanCall.RunAsync(LockInfoCoreAsync(LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken), async: true), cancellationToken);

    /// <summary>Reads the server's HiSLIP 2.0 descriptors (VI_ERROR_NSUP_OPER with a HiSLIP 1.0 server).</summary>
    public IReadOnlyList<HislipDescriptor> GetDescriptors(int timeoutMilliseconds) =>
        AsyncIO.Complete(GetDescriptorsCoreAsync(Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>Reads the server's HiSLIP 2.0 descriptors.</summary>
    public ValueTask<IReadOnlyList<HislipDescriptor>> GetDescriptorsAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        LanCall.RunAsync(GetDescriptorsCoreAsync(LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken), async: true), cancellationToken);

    /// <summary>
    /// Announces the largest message, in bytes, this client wants to receive (AsyncMaximumMessageSize) and updates
    /// <see cref="ServerMaximumMessageSize"/> from the reply.
    /// </summary>
    public void SetMaximumMessageSize(ulong size, int timeoutMilliseconds) =>
        AsyncIO.Complete(SetMaximumMessageSizeCoreAsync(size, Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>Announces the largest message this client wants to receive; see <see cref="SetMaximumMessageSize"/>.</summary>
    public ValueTask SetMaximumMessageSizeAsync(ulong size, int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        LanCall.RunAsync(SetMaximumMessageSizeCoreAsync(size, LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken), async: true),
            cancellationToken);

    /// <summary>
    /// Starts encryption on the open connection (HiSLIP 2.0 Establish Secure Connection: TLS on both channels, then SASL
    /// authentication), whatever the <see cref="HislipSecurityOptions.Encryption"/> mode. Does nothing when already
    /// encrypted. VI_ERROR_RSRC_BUSY (unread data on the server) changes nothing; any other failure, including a timeout
    /// or cancellation, closes the connection.
    /// </summary>
    public void EstablishSecureConnection(HislipSecurityOptions security, int timeoutMilliseconds) =>
        AsyncIO.Complete(EstablishSecureConnectionCoreAsync(security, Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>Starts encryption on the open connection; see <see cref="EstablishSecureConnection"/>.</summary>
    public ValueTask EstablishSecureConnectionAsync(HislipSecurityOptions security, int timeoutMilliseconds,
        CancellationToken cancellationToken = default) =>
        LanCall.RunAsync(EstablishSecureConnectionCoreAsync(security, LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken), async: true),
            cancellationToken);

    /// <summary>
    /// Ends encryption (End Secure Connection), allowed only when the server does not make it mandatory. Does nothing
    /// when not encrypted. Failures other than VI_ERROR_RSRC_BUSY close the connection.
    /// </summary>
    public void EndSecureConnection(int timeoutMilliseconds) =>
        AsyncIO.Complete(EndSecureConnectionCoreAsync(Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>Ends encryption; see <see cref="EndSecureConnection"/>.</summary>
    public ValueTask EndSecureConnectionAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        LanCall.RunAsync(EndSecureConnectionCoreAsync(LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken), async: true), cancellationToken);
}

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using VisaCs.Internal;

namespace VisaCs.Protocols.Rpc;

/// <summary>ONC RPC v2 (RFC 5531) constants and message helpers.</summary>
internal static class OncRpc
{
    public const uint RpcVersion = 2;
    public const uint MsgCall = 0;
    public const uint MsgReply = 1;
    public const uint MsgAccepted = 0;
    public const uint MsgDenied = 1;

    public const uint AcceptSuccess = 0;
    public const uint AcceptProgUnavail = 1;
    public const uint AcceptProgMismatch = 2;
    public const uint AcceptProcUnavail = 3;
    public const uint AcceptGarbageArgs = 4;
    public const uint AcceptSystemErr = 5;

    public const uint AuthNone = 0;

    public const uint LastFragment = 0x80000000;
    public const int MaxRecordSize = 256 * 1024 * 1024;

    public const uint PortmapperProgram = 100000;
    public const uint PortmapperVersion = 2;
    public const uint PortmapperProcGetPort = 3;
    public const uint ProtocolTcp = 6;

    public static void WriteCallHeader(XdrWriter w, uint xid, uint program, uint version, uint procedure)
    {
        w.WriteUInt32(xid);
        w.WriteUInt32(MsgCall);
        w.WriteUInt32(RpcVersion);
        w.WriteUInt32(program);
        w.WriteUInt32(version);
        w.WriteUInt32(procedure);
        w.WriteUInt32(AuthNone); // credentials
        w.WriteUInt32(0);
        w.WriteUInt32(AuthNone); // verifier
        w.WriteUInt32(0);
    }

    public static void WriteAcceptedReplyHeader(XdrWriter w, uint xid, uint acceptStatus)
    {
        w.WriteUInt32(xid);
        w.WriteUInt32(MsgReply);
        w.WriteUInt32(MsgAccepted);
        w.WriteUInt32(AuthNone);
        w.WriteUInt32(0);
        w.WriteUInt32(acceptStatus);
    }

    /// <summary>
    /// Parses a reply. Returns null if the xid does not match (a stale reply), otherwise a reader positioned
    /// at the procedure results. Throws on RPC-level errors.
    /// </summary>
    public static XdrReader? ParseReply(byte[] message, int length, uint expectedXid)
    {
        var r = new XdrReader(message, 0, length);
        uint xid = r.ReadUInt32();
        if (xid != expectedXid) return null;
        if (r.ReadUInt32() != MsgReply)
            throw new VisaException(VisaStatus.ErrorIO, "RPC: expected a reply message.");
        uint replyStatus = r.ReadUInt32();
        if (replyStatus == MsgDenied)
        {
            uint rejectStatus = r.ReadUInt32();
            throw new VisaException(VisaStatus.ErrorIO,
                rejectStatus == 0 ? "RPC call rejected: RPC version mismatch." : "RPC call rejected: authentication error.");
        }
        r.ReadUInt32();     // verifier flavor
        r.ReadOpaqueSpan(); // verifier body
        uint acceptStatus = r.ReadUInt32();
        return acceptStatus switch
        {
            AcceptSuccess => r,
            AcceptProgUnavail => throw new VisaException(VisaStatus.ErrorResourceNotFound, "RPC program unavailable."),
            AcceptProgMismatch => throw new VisaException(VisaStatus.ErrorResourceNotFound, "RPC program version mismatch."),
            AcceptProcUnavail => throw new VisaException(VisaStatus.ErrorOperationNotSupported, "RPC procedure unavailable."),
            AcceptGarbageArgs => throw new VisaException(VisaStatus.ErrorInvalidParameter, "RPC server could not decode the arguments."),
            _ => throw new VisaException(VisaStatus.ErrorIO, $"RPC system error (accept status {acceptStatus})."),
        };
    }

    private static int _xidSeed = Environment.TickCount;

    public static uint NewXidBase() => unchecked((uint)Interlocked.Add(ref _xidSeed, 0x10000));
}

/// <summary>
/// ONC RPC client over TCP with record marking. Thread-safe: calls are serialized, and a call waits for the one in
/// progress no longer than its own deadline. Every call takes a <c>bool async</c>: false blocks the calling thread,
/// true holds none while waiting for the reply.
/// </summary>
/// <remarks>
/// A call that times out while waiting for its reply leaves the connection usable: the late reply is recognized by
/// its xid and discarded. A call whose request was only partly sent, or a malformed record, closes the connection,
/// because the server would otherwise read the next request as the rest of the broken one; later calls then fail
/// with VI_ERROR_CONN_LOST.
/// </remarks>
internal sealed class RpcTcpClient : IDisposable
{
    private const int ReceiveBufferSize = 4096;

    private readonly Socket _socket;
    private readonly uint _program;
    private readonly uint _version;
    private readonly XdrWriter _writer = new(1024);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private uint _xid = OncRpc.NewXidBase();
    private byte[] _receive = new byte[ReceiveBufferSize];
    private volatile bool _closed;
    private volatile string? _closedReason;

    private RpcTcpClient(Socket socket, uint program, uint version)
    {
        _socket = socket;
        _program = program;
        _version = version;
    }

    public static async ValueTask<RpcTcpClient> ConnectAsync(string host, int port, uint program, uint version, Deadline deadline, bool async) =>
        new(await NetUtil.ConnectAsync(host, port, deadline, async).ConfigureAwait(false), program, version);

    public static async ValueTask<RpcTcpClient> ConnectAsync(IPEndPoint endPoint, uint program, uint version, Deadline deadline, bool async) =>
        new(await NetUtil.ConnectAsync(endPoint, deadline, async).ConfigureAwait(false), program, version);

    public IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    public IPEndPoint RemoteEndPoint => (IPEndPoint)_socket.RemoteEndPoint!;

    public Socket Socket => _socket;

    /// <summary>True while a call is in progress.</summary>
    public bool IsBusy => _gate.CurrentCount == 0;

    /// <summary>Performs a call and returns a reader over a private copy of the results.</summary>
    public async ValueTask<XdrReader> CallAsync(uint procedure, Action<XdrWriter>? writeArgs, Deadline deadline, bool async)
    {
        await _gate.EnterOrThrowAsync(deadline, async, "the RPC call in progress").ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            uint xid = unchecked(++_xid);
            _writer.Reset();
            _writer.WriteUInt32(0); // record mark placeholder
            OncRpc.WriteCallHeader(_writer, xid, _program, _version, procedure);
            writeArgs?.Invoke(_writer);
            _writer.PatchUInt32(0, OncRpc.LastFragment | (uint)(_writer.Length - 4));
            try
            {
                await NetUtil.SendAllAsync(_socket, _writer.Buffer.AsMemory(0, _writer.Length), deadline, async).ConfigureAwait(false);
            }
            catch (VisaException ex) when (NetUtil.MayHaveSentPart(ex))
            {
                Close("a request was only partly sent");
                if (ex is not VisaTimeoutException timeout) throw;
                throw new VisaTimeoutException("The RPC request was only partly sent before the timeout; the connection was closed.", ex)
                    { BytesTransferred = timeout.BytesTransferred };
            }

            while (true)
            {
                int length = await ReceiveRecordAsync(deadline, async).ConfigureAwait(false);
                var record = _receive;
                var reader = OncRpc.ParseReply(record, length, xid);
                if (reader is null) continue; // stale reply from an earlier call that timed out
                // The reader owns this buffer now; large ones are not kept for the life of the connection.
                _receive = new byte[ReceiveBufferSize];
                return reader;
            }
        }
        catch (ObjectDisposedException ex)
        {
            throw new VisaException(VisaStatus.ErrorConnectionLost, ClosedMessage, ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ClosedMessage => _closedReason is { } reason ? $"The RPC connection was closed: {reason}." : "The RPC connection is closed.";

    private void ThrowIfClosed()
    {
        if (_closed) throw new VisaException(VisaStatus.ErrorConnectionLost, ClosedMessage);
    }

    /// <summary>Closes the connection; later calls fail with VI_ERROR_CONN_LOST giving <paramref name="reason"/>.</summary>
    private void Close(string? reason)
    {
        if (reason is not null) _closedReason ??= reason;
        _closed = true;
        NetUtil.SafeClose(_socket);
    }

    // Record reassembly state. Kept in fields so that a timeout in the middle of a record
    // does not desynchronize the stream: the next call resumes where this one stopped.
    private readonly byte[] _mark = new byte[4];
    private int _markFilled;
    private bool _inFragment;
    private int _fragmentRemaining;
    private bool _fragmentIsLast;
    private int _recordLength;

    private async ValueTask<int> ReceiveRecordAsync(Deadline deadline, bool async)
    {
        while (true)
        {
            if (!_inFragment)
            {
                while (_markFilled < 4)
                    _markFilled += await NetUtil.ReceiveSomeAsync(_socket, _mark.AsMemory(_markFilled), deadline, async).ConfigureAwait(false);
                _markFilled = 0;
                uint mark = BinaryPrimitives.ReadUInt32BigEndian(_mark);
                _fragmentRemaining = (int)(mark & 0x7FFFFFFF);
                _fragmentIsLast = (mark & OncRpc.LastFragment) != 0;
                long total = (long)_recordLength + _fragmentRemaining;
                if (total > OncRpc.MaxRecordSize)
                {
                    Close("the server sent an oversized RPC record");
                    throw new VisaException(VisaStatus.ErrorIO, "RPC record too large; the connection was closed.");
                }
                if (_receive.Length < total)
                    Array.Resize(ref _receive, (int)Math.Max(_receive.Length * 2L, total));
                _inFragment = true;
            }
            while (_fragmentRemaining > 0)
            {
                int n = await NetUtil.ReceiveSomeAsync(_socket, _receive.AsMemory(_recordLength, _fragmentRemaining), deadline, async)
                    .ConfigureAwait(false);
                _recordLength += n;
                _fragmentRemaining -= n;
            }
            _inFragment = false;
            if (_fragmentIsLast)
            {
                int length = _recordLength;
                _recordLength = 0;
                return length;
            }
        }
    }

    public void Dispose() => Close(null);
}

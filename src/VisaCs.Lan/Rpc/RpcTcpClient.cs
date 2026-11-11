using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using VisaCs.Internal;

namespace VisaCs.Lan.Rpc;

/// <summary>
/// ONC RPC client over TCP with record marking, for one program and version. Thread-safe: calls are serialized, and
/// a call waits for the one in progress no longer than its own timeout.
/// </summary>
/// <remarks>
/// <para>
/// Timeouts are in milliseconds; -1 waits without limit. A timeout throws <see cref="VisaTimeoutException"/>, other
/// failures <see cref="VisaException"/>, and cancelling the token of an asynchronous call
/// <see cref="OperationCanceledException"/>. RPC-level rejections map to VI_ERROR_RSRC_NFOUND (program unavailable or
/// version mismatch), VI_ERROR_NSUP_OPER (procedure unavailable), VI_ERROR_INV_PARAMETER (garbage arguments) and
/// VI_ERROR_IO.
/// </para>
/// <para>
/// A call that times out while waiting for its reply leaves the connection usable: the late reply is recognized by
/// its xid and discarded. A call whose request was only partly sent, or a malformed record, closes the connection,
/// because the server would otherwise read the next request as the rest of the broken one; later calls then fail
/// with VI_ERROR_CONN_LOST.
/// </para>
/// </remarks>
public sealed class RpcTcpClient : IDisposable
{
    private const int ReceiveBufferSize = 4096;

    private readonly Socket _socket;
    private readonly XdrWriter _writer = new(1024);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private uint _xid = OncRpc.NewXidBase();
    private byte[] _receive = new byte[ReceiveBufferSize];
    private volatile bool _closed;
    private volatile string? _closedReason;

    private RpcTcpClient(Socket socket, uint program, uint version)
    {
        _socket = socket;
        Program = program;
        Version = version;
    }

    /// <summary>Connects to <paramref name="host"/>:<paramref name="port"/> (IPv4 addresses first) for calls to one RPC program.</summary>
    public static RpcTcpClient Connect(string host, int port, uint program, uint version, int timeoutMilliseconds) =>
        AsyncIO.Complete(ConnectAsync(host, port, program, version, Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>Connects to <paramref name="host"/>:<paramref name="port"/>; see <see cref="Connect(string, int, uint, uint, int)"/>.</summary>
    public static ValueTask<RpcTcpClient> ConnectAsync(string host, int port, uint program, uint version, int timeoutMilliseconds,
        CancellationToken cancellationToken = default) =>
        LanCall.RunAsync(ConnectAsync(host, port, program, version, LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken), async: true),
            cancellationToken);

    /// <summary>Connects to <paramref name="endPoint"/> for calls to one RPC program.</summary>
    public static RpcTcpClient Connect(IPEndPoint endPoint, uint program, uint version, int timeoutMilliseconds) =>
        AsyncIO.Complete(ConnectAsync(endPoint, program, version, Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>Connects to <paramref name="endPoint"/>; see <see cref="Connect(IPEndPoint, uint, uint, int)"/>.</summary>
    public static ValueTask<RpcTcpClient> ConnectAsync(IPEndPoint endPoint, uint program, uint version, int timeoutMilliseconds,
        CancellationToken cancellationToken = default) =>
        LanCall.RunAsync(ConnectAsync(endPoint, program, version, LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken), async: true),
            cancellationToken);

    internal static async ValueTask<RpcTcpClient> ConnectAsync(string host, int port, uint program, uint version, Deadline deadline, bool async) =>
        new(await NetUtil.ConnectAsync(host, port, deadline, async).ConfigureAwait(false), program, version);

    internal static async ValueTask<RpcTcpClient> ConnectAsync(IPEndPoint endPoint, uint program, uint version, Deadline deadline, bool async) =>
        new(await NetUtil.ConnectAsync(endPoint, deadline, async).ConfigureAwait(false), program, version);

    /// <summary>The RPC program number calls go to.</summary>
    public uint Program { get; }

    /// <summary>The program version calls go to.</summary>
    public uint Version { get; }

    /// <summary>The local end of the connection.</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    /// <summary>The server's end of the connection.</summary>
    public IPEndPoint RemoteEndPoint => (IPEndPoint)_socket.RemoteEndPoint!;

    internal Socket Socket => _socket;

    /// <summary>True while a call is in progress.</summary>
    public bool IsBusy => _gate.CurrentCount == 0;

    /// <summary>
    /// Calls <paramref name="procedure"/>: <paramref name="writeArguments"/> writes the arguments (null for none), and
    /// the result is a reader over a private copy of the results.
    /// </summary>
    public XdrReader Call(uint procedure, Action<XdrWriter>? writeArguments, int timeoutMilliseconds) =>
        AsyncIO.Complete(CallAsync(procedure, writeArguments, Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>Calls <paramref name="procedure"/> without holding a thread; see <see cref="Call"/>.</summary>
    public ValueTask<XdrReader> CallAsync(uint procedure, Action<XdrWriter>? writeArguments, int timeoutMilliseconds,
        CancellationToken cancellationToken = default) =>
        LanCall.RunAsync(CallAsync(procedure, writeArguments, LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken), async: true),
            cancellationToken);

    /// <summary>Performs a call and returns a reader over a private copy of the results.</summary>
    internal async ValueTask<XdrReader> CallAsync(uint procedure, Action<XdrWriter>? writeArgs, Deadline deadline, bool async)
    {
        await _gate.EnterOrThrowAsync(deadline, async, "the RPC call in progress").ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            uint xid = unchecked(++_xid);
            _writer.Reset();
            _writer.WriteUInt32(0); // record mark placeholder
            OncRpc.WriteCallHeader(_writer, xid, Program, Version, procedure);
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
                throw LanErrors.Timeout("The RPC request was only partly sent before the timeout; the connection was closed.", ex,
                    timeout.BytesTransferred);
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
            throw LanErrors.ConnectionLost(ClosedMessage, ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ClosedMessage => _closedReason is { } reason ? $"The RPC connection was closed: {reason}." : "The RPC connection is closed.";

    private void ThrowIfClosed()
    {
        if (_closed) throw LanErrors.ConnectionLost(ClosedMessage);
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
                    throw LanErrors.IO("RPC record too large; the connection was closed.");
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

    /// <summary>Closes the connection.</summary>
    public void Dispose() => Close(null);
}

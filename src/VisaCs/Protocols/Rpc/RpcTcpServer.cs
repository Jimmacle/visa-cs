using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using VisaCs.Internal;

namespace VisaCs.Protocols.Rpc;

/// <summary>Context of an incoming RPC call on the server side.</summary>
internal sealed class RpcCallContext
{
    public required uint Procedure { get; init; }
    public required RpcServerConnection Connection { get; init; }
}

/// <summary>A client connection accepted by <see cref="RpcTcpServer"/>. Holds per-connection state.</summary>
internal sealed class RpcServerConnection
{
    internal RpcServerConnection(Socket socket) => Socket = socket;

    public Socket Socket { get; }

    public IPEndPoint RemoteEndPoint => (IPEndPoint)Socket.RemoteEndPoint!;

    public IPEndPoint LocalEndPoint => (IPEndPoint)Socket.LocalEndPoint!;

    /// <summary>Arbitrary per-connection state owned by the handler.</summary>
    public object? State { get; set; }
}

/// <summary>
/// Minimal ONC RPC server over TCP. Used for the VXI-11 interrupt (SRQ) channel, which requires the VISA client to
/// act as an RPC server, and by the simulator. With <c>blockingHandlers</c> (the simulator, whose handlers wait for
/// data) every connection gets a thread; otherwise accepting and serving are asynchronous and hold no thread while
/// idle.
/// </summary>
internal sealed class RpcTcpServer : IDisposable
{
    /// <summary>Handles a call. Return the accept status; write results to the writer on success.</summary>
    public delegate uint Handler(RpcCallContext context, XdrReader args, XdrWriter results);

    private readonly TcpListener _listener;
    private readonly uint _program;
    private readonly uint _version;
    private readonly Handler _handler;
    private readonly List<Socket> _connections = new();
    private readonly bool _blockingHandlers;
    private volatile bool _disposed;

    public RpcTcpServer(IPAddress bindAddress, int port, uint program, uint version, Handler handler, bool blockingHandlers = true)
    {
        _listener = new TcpListener(bindAddress, port);
        _program = program;
        _version = version;
        _handler = handler;
        _blockingHandlers = blockingHandlers;
    }

    /// <summary>Raised when a connection closes (for cleanup of per-connection state).</summary>
    public event Action<RpcServerConnection>? ConnectionClosed;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    public void Start()
    {
        _listener.Start();
        if (_blockingHandlers) new Thread(() => AsyncIO.Complete(AcceptLoopAsync(async: false))) { IsBackground = true, Name = $"RPC server {_program:X}" }.Start();
        else _ = AcceptLoopAsync(async: true).AsTask();
    }

    private async ValueTask AcceptLoopAsync(bool async)
    {
        while (!_disposed)
        {
            Socket socket;
            try
            {
                socket = async ? await _listener.AcceptSocketAsync().ConfigureAwait(false) : _listener.AcceptSocket();
            }
            catch
            {
                if (_disposed) return;
                continue;
            }
            socket.NoDelay = true;
            lock (_connections)
            {
                if (_disposed)
                {
                    NetUtil.SafeClose(socket);
                    return;
                }
                _connections.Add(socket);
            }
            if (async) _ = ServeAsync(socket, async: true).AsTask();
            else new Thread(() => AsyncIO.Complete(ServeAsync(socket, async: false))) { IsBackground = true, Name = "RPC connection" }.Start();
        }
    }

    private async ValueTask ServeAsync(Socket socket, bool async)
    {
        var connection = new RpcServerConnection(socket);
        var writer = new XdrWriter(1024);
        byte[] buffer = new byte[4096];
        var header = new byte[4];
        try
        {
            while (!_disposed)
            {
                // Read one record.
                long total = 0;
                while (true)
                {
                    await ReceiveExactAsync(socket, header, async).ConfigureAwait(false);
                    uint mark = BinaryPrimitives.ReadUInt32BigEndian(header);
                    int fragment = (int)(mark & 0x7FFFFFFF);
                    if (total + fragment > OncRpc.MaxRecordSize) return;
                    if (buffer.Length < total + fragment) Array.Resize(ref buffer, (int)Math.Max(buffer.Length * 2L, total + fragment));
                    await ReceiveExactAsync(socket, buffer.AsMemory((int)total, fragment), async).ConfigureAwait(false);
                    total += fragment;
                    if ((mark & OncRpc.LastFragment) != 0) break;
                }

                var reader = new XdrReader(buffer, 0, (int)total);
                uint xid = reader.ReadUInt32();
                if (reader.ReadUInt32() != OncRpc.MsgCall) continue;
                uint rpcVersion = reader.ReadUInt32();
                uint program = reader.ReadUInt32();
                uint version = reader.ReadUInt32();
                uint procedure = reader.ReadUInt32();
                reader.ReadUInt32(); reader.ReadOpaqueSpan(); // credentials
                reader.ReadUInt32(); reader.ReadOpaqueSpan(); // verifier

                writer.Reset();
                writer.WriteUInt32(0);
                if (rpcVersion != OncRpc.RpcVersion)
                {
                    writer.WriteUInt32(xid);
                    writer.WriteUInt32(OncRpc.MsgReply);
                    writer.WriteUInt32(OncRpc.MsgDenied);
                    writer.WriteUInt32(0); // RPC_MISMATCH
                    writer.WriteUInt32(OncRpc.RpcVersion);
                    writer.WriteUInt32(OncRpc.RpcVersion);
                }
                else if (program != _program)
                {
                    OncRpc.WriteAcceptedReplyHeader(writer, xid, OncRpc.AcceptProgUnavail);
                }
                else if (version != _version)
                {
                    OncRpc.WriteAcceptedReplyHeader(writer, xid, OncRpc.AcceptProgMismatch);
                    writer.WriteUInt32(_version);
                    writer.WriteUInt32(_version);
                }
                else if (procedure == 0)
                {
                    OncRpc.WriteAcceptedReplyHeader(writer, xid, OncRpc.AcceptSuccess);
                }
                else
                {
                    OncRpc.WriteAcceptedReplyHeader(writer, xid, OncRpc.AcceptSuccess);
                    uint status;
                    try
                    {
                        status = _handler(new RpcCallContext { Procedure = procedure, Connection = connection }, reader, writer);
                    }
                    catch (VisaException)
                    {
                        status = OncRpc.AcceptGarbageArgs;
                    }
                    if (status != OncRpc.AcceptSuccess)
                    {
                        // Discard any partial results; the reply carries only the status.
                        writer.Reset();
                        writer.WriteUInt32(0);
                        OncRpc.WriteAcceptedReplyHeader(writer, xid, status);
                    }
                }
                writer.PatchUInt32(0, OncRpc.LastFragment | (uint)(writer.Length - 4));
                if (async) await socket.SendAsync(writer.Buffer.AsMemory(0, writer.Length)).ConfigureAwait(false);
                else socket.Send(writer.WrittenSpan);
            }
        }
        catch
        {
            // Connection closed or malformed input.
        }
        finally
        {
            lock (_connections) _connections.Remove(socket);
            try { socket.Dispose(); } catch { /* ignore */ }
            try { ConnectionClosed?.Invoke(connection); } catch { /* ignore */ }
        }
    }

    private static async ValueTask ReceiveExactAsync(Socket socket, Memory<byte> buffer, bool async)
    {
        // Accepted sockets are blocking: a synchronous receive simply waits.
        while (!buffer.IsEmpty)
        {
            int n = async ? await socket.ReceiveAsync(buffer).ConfigureAwait(false) : socket.Receive(buffer.Span);
            if (n <= 0) throw new SocketException((int)SocketError.ConnectionReset);
            buffer = buffer[n..];
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _listener.Stop(); } catch { /* ignore */ }
        lock (_connections)
        {
            foreach (var s in _connections) NetUtil.SafeClose(s);
            _connections.Clear();
        }
    }
}

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using VisaCs.Internal;

namespace VisaCs.Lan.Rpc;

/// <summary>An incoming call on an <see cref="RpcTcpServer"/>.</summary>
public sealed class RpcCallContext
{
    internal RpcCallContext(uint procedure, RpcServerConnection connection)
    {
        Procedure = procedure;
        Connection = connection;
    }

    /// <summary>The procedure number being called.</summary>
    public uint Procedure { get; }

    /// <summary>The connection the call arrived on.</summary>
    public RpcServerConnection Connection { get; }
}

/// <summary>A client connection accepted by <see cref="RpcTcpServer"/>.</summary>
public sealed class RpcServerConnection
{
    private readonly Socket _socket;

    internal RpcServerConnection(Socket socket) => _socket = socket;

    /// <summary>The client's end of the connection.</summary>
    public IPEndPoint RemoteEndPoint => (IPEndPoint)_socket.RemoteEndPoint!;

    /// <summary>The server's end of the connection.</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    /// <summary>Arbitrary per-connection state owned by the handler.</summary>
    public object? State { get; set; }
}

/// <summary>
/// Handles a call to an <see cref="RpcTcpServer"/>: reads the arguments from <paramref name="arguments"/>, writes the
/// results to <paramref name="results"/> and returns <see cref="RpcAcceptStatus.Success"/>, or returns another status
/// (anything written is then discarded). A <see cref="LanException"/> thrown while reading the arguments (malformed
/// XDR) answers <see cref="RpcAcceptStatus.GarbageArguments"/>.
/// </summary>
public delegate RpcAcceptStatus RpcCallHandler(RpcCallContext context, XdrReader arguments, XdrWriter results);

/// <summary>
/// Minimal ONC RPC server over TCP for one program and version (procedure 0, the null procedure, is answered
/// automatically). Used for the VXI-11 interrupt (SRQ) channel, where the instrument calls the client back.
/// </summary>
/// <remarks>
/// By default accepting and serving are asynchronous and hold no thread while idle; the handler then must not block.
/// With <c>blockingHandlers</c> every connection gets its own thread, so a handler may wait (calls on one connection
/// are still handled one at a time).
/// </remarks>
public sealed class RpcTcpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly uint _program;
    private readonly uint _version;
    private readonly RpcCallHandler _handler;
    private readonly List<Socket> _connections = new();
    private readonly bool _blockingHandlers;
    private volatile bool _disposed;

    /// <summary>
    /// Creates a server listening on <paramref name="bindAddress"/>:<paramref name="port"/> (port 0 picks a free port)
    /// once <see cref="Start"/> is called.
    /// </summary>
    public RpcTcpServer(IPAddress bindAddress, int port, uint program, uint version, RpcCallHandler handler, bool blockingHandlers = false)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);
        ArgumentNullException.ThrowIfNull(handler);
        _listener = new TcpListener(bindAddress, port);
        _program = program;
        _version = version;
        _handler = handler;
        _blockingHandlers = blockingHandlers;
    }

    /// <summary>Raised when a connection closes (for cleanup of per-connection state).</summary>
    public event Action<RpcServerConnection>? ConnectionClosed;

    /// <summary>The TCP port the server listens on (valid after <see cref="Start"/>).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The address and port the server listens on (valid after <see cref="Start"/>).</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>Starts listening and serving calls.</summary>
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
                if (reader.ReadUInt32() != OncRpc.CallMessage) continue;
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
                    writer.WriteUInt32(OncRpc.ReplyMessage);
                    writer.WriteUInt32(OncRpc.MsgDenied);
                    writer.WriteUInt32(0); // RPC_MISMATCH
                    writer.WriteUInt32(OncRpc.RpcVersion);
                    writer.WriteUInt32(OncRpc.RpcVersion);
                }
                else if (program != _program)
                {
                    OncRpc.WriteAcceptedReplyHeader(writer, xid, RpcAcceptStatus.ProgramUnavailable);
                }
                else if (version != _version)
                {
                    OncRpc.WriteAcceptedReplyHeader(writer, xid, RpcAcceptStatus.ProgramMismatch);
                    writer.WriteUInt32(_version);
                    writer.WriteUInt32(_version);
                }
                else if (procedure == 0)
                {
                    OncRpc.WriteAcceptedReplyHeader(writer, xid, RpcAcceptStatus.Success);
                }
                else
                {
                    OncRpc.WriteAcceptedReplyHeader(writer, xid, RpcAcceptStatus.Success);
                    RpcAcceptStatus status;
                    try
                    {
                        status = _handler(new RpcCallContext(procedure, connection), reader, writer);
                    }
                    catch (LanException)
                    {
                        status = RpcAcceptStatus.GarbageArguments;
                    }
                    if (status != RpcAcceptStatus.Success)
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

    /// <summary>Stops listening and closes all connections.</summary>
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

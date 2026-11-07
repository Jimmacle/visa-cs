using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using VisaCs.Internal;

namespace VisaCs.Simulation;

/// <summary>Base for simple one-thread-per-connection TCP servers.</summary>
public abstract class TcpInstrumentServerBase : IDisposable
{
    private readonly TcpListener _listener;
    private readonly List<Socket> _clients = new();
    private volatile bool _disposed;

    /// <summary>Creates a TCP server for an instrument. Port 0 picks a free port.</summary>
    protected TcpInstrumentServerBase(SimulatedInstrument device, IPAddress? bindAddress, int port)
    {
        Device = device;
        _listener = new TcpListener(bindAddress ?? IPAddress.Loopback, port);
    }

    /// <summary>The simulated instrument.</summary>
    protected SimulatedInstrument Device { get; }

    /// <summary>True after the server was stopped.</summary>
    protected bool IsDisposed => _disposed;

    /// <summary>The TCP port the server listens on (valid after Start).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Number of accepted connections.</summary>
    public int ConnectionCount
    {
        get
        {
            lock (_clients) return _clients.Count;
        }
    }

    /// <summary>Starts listening.</summary>
    public void Start()
    {
        _listener.Start();
        new Thread(() =>
        {
            while (!_disposed)
            {
                Socket socket;
                try { socket = _listener.AcceptSocket(); }
                catch { if (_disposed) return; continue; }
                socket.NoDelay = true;
                lock (_clients) _clients.Add(socket);
                new Thread(() =>
                {
                    try { Serve(socket); }
                    catch { /* connection closed */ }
                    finally
                    {
                        lock (_clients) _clients.Remove(socket);
                        NetUtil.SafeClose(socket);
                    }
                }) { IsBackground = true, Name = GetType().Name }.Start();
            }
        }) { IsBackground = true, Name = GetType().Name + " accept" }.Start();
    }

    /// <summary>Handles one accepted connection until it closes.</summary>
    protected abstract void Serve(Socket socket);

    /// <summary>Stops the server and closes its connections.</summary>
    public virtual void Dispose()
    {
        _disposed = true;
        try { _listener.Stop(); } catch { /* ignore */ }
        lock (_clients)
        {
            foreach (var c in _clients) NetUtil.SafeClose(c);
            _clients.Clear();
        }
    }
}

/// <summary>A raw TCP (SCPI-RAW, port 5025 style) server: newline-terminated messages.</summary>
public sealed class SocketInstrumentServer : TcpInstrumentServerBase
{
    /// <summary>Creates a raw socket server for an instrument. Port 0 picks a free port.</summary>
    public SocketInstrumentServer(SimulatedInstrument device, IPAddress? bindAddress = null, int port = 0)
        : base(device, bindAddress, port)
    {
    }

    /// <inheritdoc/>
    protected override void Serve(Socket socket)
    {
        var pending = new List<byte>();
        var buffer = new byte[65536];
        while (!IsDisposed)
        {
            int n = socket.Receive(buffer);
            if (n <= 0) return;
            pending.AddRange(buffer.AsSpan(0, n));
            while (TryExtractMessage(pending, out var message))
            {
                foreach (var response in Device.Process(message))
                    socket.Send([.. response, (byte)'\n']);
            }
        }
    }

    /// <summary>Finds a newline-terminated message, skipping over IEEE 488.2 definite-length blocks.</summary>
    private static bool TryExtractMessage(List<byte> pending, out byte[] message)
    {
        int i = 0;
        while (i < pending.Count)
        {
            byte b = pending[i];
            if (b == (byte)'#' && i + 1 < pending.Count && pending[i + 1] is >= (byte)'1' and <= (byte)'9')
            {
                int digits = pending[i + 1] - '0';
                if (i + 2 + digits > pending.Count) break;
                int length = 0;
                for (int d = 0; d < digits; d++) length = length * 10 + (pending[i + 2 + d] - '0');
                i += 2 + digits + length;
                continue;
            }
            if (b == (byte)'\n')
            {
                message = pending.GetRange(0, i + 1).ToArray();
                pending.RemoveRange(0, i + 1);
                return true;
            }
            i++;
        }
        message = Array.Empty<byte>();
        return false;
    }
}

/// <summary>A Teledyne LeCroy VICP server (8-byte header framing).</summary>
public sealed class VicpInstrumentServer : TcpInstrumentServerBase
{
    private const byte OpData = 0x80, OpRemote = 0x40, OpClear = 0x10, OpSrq = 0x08, OpSerialPoll = 0x04, OpEoi = 0x01;
    private readonly List<Socket> _srqTargets = new();

    /// <summary>Creates a VICP server for an instrument. Port 0 picks a free port.</summary>
    public VicpInstrumentServer(SimulatedInstrument device, IPAddress? bindAddress = null, int port = 0)
        : base(device, bindAddress, port)
    {
        Device.ServiceRequested += OnServiceRequested;
    }

    /// <summary>True while the client holds the instrument in remote state.</summary>
    public bool IsRemote { get; private set; }

    private static void SendBlock(Socket socket, byte operation, ReadOnlySpan<byte> data)
    {
        var packet = new byte[8 + data.Length];
        packet[0] = operation;
        packet[1] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), (uint)data.Length);
        data.CopyTo(packet.AsSpan(8));
        lock (socket) socket.Send(packet);
    }

    /// <inheritdoc/>
    protected override void Serve(Socket socket)
    {
        lock (_srqTargets) _srqTargets.Add(socket);
        try
        {
            var input = new List<byte>();
            var header = new byte[8];
            while (!IsDisposed)
            {
                SyncProtocolIO.ReceiveExact(socket, header);
                byte op = header[0];
                int length = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
                var payload = new byte[length];
                SyncProtocolIO.ReceiveExact(socket, payload);
                IsRemote = (op & OpRemote) != 0;
                Device.IsRemote = IsRemote;

                if ((op & OpClear) != 0)
                {
                    input.Clear();
                    Device.DeviceClear();
                    continue;
                }
                if ((op & OpSerialPoll) != 0)
                {
                    SendBlock(socket, OpData | OpEoi, [Device.GetStatusByte(false)]);
                    continue;
                }
                input.AddRange(payload);
                if ((op & OpEoi) != 0 && input.Count > 0)
                {
                    var message = input.ToArray();
                    input.Clear();
                    foreach (var response in Device.Process(message))
                        SendBlock(socket, OpData | OpEoi, [.. response, (byte)'\n']);
                }
            }
        }
        finally
        {
            lock (_srqTargets) _srqTargets.Remove(socket);
        }
    }

    private void OnServiceRequested(byte stb)
    {
        List<Socket> targets;
        lock (_srqTargets) targets = _srqTargets.ToList();
        foreach (var s in targets)
        {
            try { SendBlock(s, OpSrq, "1"u8); } catch { /* closing */ }
        }
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        Device.ServiceRequested -= OnServiceRequested;
        base.Dispose();
    }
}

using System.Net;
using System.Net.Sockets;
using VisaCs.Internal;
using VisaCs.Protocols.Rpc;
using VisaCs.Protocols.Vxi11;

namespace VisaCs.Simulation;

/// <summary>
/// A VXI-11 server for a <see cref="SimulatedInstrument"/>: portmapper (TCP and UDP), core channel,
/// abort channel and interrupt channel (service requests are sent back to the client's RPC server).
/// </summary>
public sealed class Vxi11InstrumentServer : IDisposable
{
    private readonly SimulatedInstrument _device;
    private readonly IPAddress _bindAddress;
    private readonly int _requestedPortmapperPort;
    private readonly object _sync = new();
    private readonly Dictionary<int, Link> _links = new();
    private RpcTcpServer? _portmapper;
    private RpcTcpServer? _core;
    private RpcTcpServer? _abort;
    private Socket? _udp;
    private int _nextLinkId = 1;
    private int? _lockOwner;
    private volatile bool _disposed;

    private sealed class Link
    {
        public required int Id { get; init; }
        public required RpcServerConnection Connection { get; init; }
        public string DeviceName { get; init; } = "";
        public List<byte> Input { get; } = new();
        public Queue<byte[]> Output { get; } = new();
        public byte[]? Current;
        public int CurrentOffset;
        public byte[]? SrqHandle;
        public bool AbortRequested;
        public Task Pending = Task.CompletedTask;
        public int Generation;
    }

    private sealed class ConnectionState
    {
        public List<int> Links { get; } = new();
        public IPEndPoint? InterruptEndPoint;
        public RpcTcpClient? Interrupt;
    }

    /// <summary>Creates a VXI-11 server for an instrument. Port 0 for the portmapper picks a free port.</summary>
    public Vxi11InstrumentServer(SimulatedInstrument device, IPAddress? bindAddress = null, int portmapperPort = 0)
    {
        _device = device;
        _bindAddress = bindAddress ?? IPAddress.Loopback;
        _requestedPortmapperPort = portmapperPort;
        _device.ServiceRequested += OnServiceRequested;
    }

    /// <summary>Largest write accepted per device_write, reported in create_link. Small values exercise chunking.</summary>
    public uint MaxReceiveSize { get; set; } = 1024 * 1024;

    /// <summary>Port of the portmapper (111 when run as a real service).</summary>
    public int PortmapperPort => _portmapper?.Port ?? 0;
    /// <summary>Port of the core channel.</summary>
    public int CorePort => _core?.Port ?? 0;
    /// <summary>Port of the abort channel.</summary>
    public int AbortPort => _abort?.Port ?? 0;
    /// <summary>True while a link holds the instrument in remote state.</summary>
    public bool IsRemote { get; private set; }

    /// <summary>Number of links currently open.</summary>
    public int LinkCount
    {
        get
        {
            lock (_sync) return _links.Count;
        }
    }

    /// <summary>Starts the portmapper and the core and abort channels.</summary>
    public void Start()
    {
        _core = new RpcTcpServer(_bindAddress, 0, Vxi11Constants.CoreProgram, Vxi11Constants.Version, HandleCore);
        _core.ConnectionClosed += OnConnectionClosed;
        _core.Start();
        _abort = new RpcTcpServer(_bindAddress, 0, Vxi11Constants.AbortProgram, Vxi11Constants.Version, HandleAbort);
        _abort.Start();
        // The UDP portmapper shares the TCP port number. With a free TCP port picked by the system, the same UDP
        // port may be taken: try other ports in that case.
        for (int attempt = 0; ; attempt++)
        {
            _portmapper = new RpcTcpServer(_bindAddress, _requestedPortmapperPort, OncRpc.PortmapperProgram, OncRpc.PortmapperVersion, HandlePortmapper);
            _portmapper.Start();
            _udp = new Socket(_bindAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                _udp.Bind(new IPEndPoint(_bindAddress, _portmapper.Port));
                // On macOS, disposing a UDP socket waits for a blocked receive, which nothing else wakes: let it time out.
                _udp.ReceiveTimeout = 250;
                break;
            }
            catch (SocketException) when (_requestedPortmapperPort == 0 && attempt < 20)
            {
                _udp.Dispose();
                _portmapper.Dispose();
            }
        }
        new Thread(UdpLoop) { IsBackground = true, Name = "VXI-11 sim UDP portmapper" }.Start();
    }

    private uint PortFor(uint program) => program switch
    {
        Vxi11Constants.CoreProgram => (uint)CorePort,
        Vxi11Constants.AbortProgram => (uint)AbortPort,
        _ => 0,
    };

    private uint HandlePortmapper(RpcCallContext context, XdrReader args, XdrWriter results)
    {
        if (context.Procedure != OncRpc.PortmapperProcGetPort) return OncRpc.AcceptProcUnavail;
        uint program = args.ReadUInt32();
        args.ReadUInt32();
        args.ReadUInt32();
        args.ReadUInt32();
        results.WriteUInt32(PortFor(program));
        return OncRpc.AcceptSuccess;
    }

    private void UdpLoop()
    {
        var buffer = new byte[1500];
        var writer = new XdrWriter();
        while (!_disposed)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int n;
            try
            {
                n = _udp!.ReceiveFrom(buffer, ref from);
            }
            catch
            {
                if (_disposed) return;
                continue;
            }
            try
            {
                var r = new XdrReader(buffer, 0, n);
                uint xid = r.ReadUInt32();
                if (r.ReadUInt32() != OncRpc.MsgCall) continue;
                r.ReadUInt32();
                uint program = r.ReadUInt32();
                r.ReadUInt32();
                uint procedure = r.ReadUInt32();
                r.ReadUInt32(); r.ReadOpaqueSpan();
                r.ReadUInt32(); r.ReadOpaqueSpan();
                if (program != OncRpc.PortmapperProgram || procedure != OncRpc.PortmapperProcGetPort) continue;
                uint target = r.ReadUInt32();
                writer.Reset();
                OncRpc.WriteAcceptedReplyHeader(writer, xid, OncRpc.AcceptSuccess);
                writer.WriteUInt32(PortFor(target));
                _udp!.SendTo(writer.WrittenSpan, from);
            }
            catch
            {
                // Ignore malformed datagrams.
            }
        }
    }

    private static ConnectionState StateOf(RpcServerConnection connection) =>
        (ConnectionState)(connection.State ??= new ConnectionState());

    private uint HandleCore(RpcCallContext context, XdrReader args, XdrWriter results)
    {
        switch (context.Procedure)
        {
            case Vxi11Constants.ProcCreateLink:
            {
                args.ReadInt32(); // client id
                bool lockDevice = args.ReadBool();
                uint lockTimeout = args.ReadUInt32();
                string device = args.ReadString();
                if (!device.StartsWith("inst", StringComparison.OrdinalIgnoreCase) && !device.StartsWith("gpib", StringComparison.OrdinalIgnoreCase))
                {
                    results.WriteInt32(Vxi11Constants.ErrDeviceNotAccessible);
                    results.WriteInt32(0);
                    results.WriteUInt32((uint)AbortPort);
                    results.WriteUInt32(MaxReceiveSize);
                    return OncRpc.AcceptSuccess;
                }
                Link link;
                lock (_sync)
                {
                    link = new Link { Id = _nextLinkId++, Connection = context.Connection, DeviceName = device };
                    _links[link.Id] = link;
                    StateOf(context.Connection).Links.Add(link.Id);
                }
                int error = lockDevice ? AcquireLock(link.Id, true, lockTimeout) : 0;
                results.WriteInt32(error);
                results.WriteInt32(link.Id);
                results.WriteUInt32((uint)AbortPort);
                results.WriteUInt32(MaxReceiveSize);
                return OncRpc.AcceptSuccess;
            }

            case Vxi11Constants.ProcDeviceWrite:
            {
                int lid = args.ReadInt32();
                args.ReadUInt32(); // io timeout
                uint lockTimeout = args.ReadUInt32();
                int flags = args.ReadInt32();
                var data = args.ReadOpaqueSpan().ToArray();
                if (!TryGetLink(lid, out var link)) return Error(results, Vxi11Constants.ErrInvalidLinkIdentifier, extraWords: 1);
                int lockError = CheckLock(lid, flags, lockTimeout);
                if (lockError != 0) return Error(results, lockError, extraWords: 1);
                if (data.Length > MaxReceiveSize) return Error(results, Vxi11Constants.ErrParameter, extraWords: 1);

                lock (_sync)
                {
                    link.Input.AddRange(data);
                    if ((flags & Vxi11Constants.FlagEnd) != 0)
                    {
                        // Like a real instrument, accept the message now and execute it in the background.
                        var message = link.Input.ToArray();
                        link.Input.Clear();
                        int generation = link.Generation;
                        link.Pending = link.Pending.ContinueWith(_ =>
                        {
                            var responses = _device.Process(message);
                            lock (_sync)
                            {
                                if (link.Generation != generation) return;
                                foreach (var response in responses) link.Output.Enqueue([.. response, (byte)'\n']);
                                Monitor.PulseAll(_sync);
                            }
                        }, TaskScheduler.Default);
                    }
                }
                results.WriteInt32(0);
                results.WriteUInt32((uint)data.Length);
                return OncRpc.AcceptSuccess;
            }

            case Vxi11Constants.ProcDeviceRead:
            {
                int lid = args.ReadInt32();
                uint requestSize = args.ReadUInt32();
                uint ioTimeout = args.ReadUInt32();
                uint lockTimeout = args.ReadUInt32();
                int flags = args.ReadInt32();
                byte termChar = (byte)args.ReadInt32();
                if (!TryGetLink(lid, out var link)) return ReadError(results, Vxi11Constants.ErrInvalidLinkIdentifier);
                int lockError = CheckLock(lid, flags, lockTimeout);
                if (lockError != 0) return ReadError(results, lockError);

                lock (_sync)
                {
                    var deadline = Deadline.FromMilliseconds(ioTimeout >= int.MaxValue ? -1 : (int)ioTimeout);
                    link.AbortRequested = false;
                    while (link.Current is null && link.Output.Count == 0)
                    {
                        if (link.AbortRequested)
                        {
                            link.AbortRequested = false;
                            return ReadError(results, Vxi11Constants.ErrAbort);
                        }
                        int remaining = deadline.RemainingMilliseconds;
                        if (remaining == 0) return ReadError(results, Vxi11Constants.ErrIoTimeout);
                        Monitor.Wait(_sync, remaining < 0 ? 1000 : Math.Min(remaining, 1000));
                    }
                    link.Current ??= link.Output.Dequeue();
                    int available = link.Current.Length - link.CurrentOffset;
                    int count = (int)Math.Min(requestSize, (uint)available);
                    int reason = 0;
                    if ((flags & Vxi11Constants.FlagTermCharSet) != 0)
                    {
                        int index = Array.IndexOf(link.Current, termChar, link.CurrentOffset, count);
                        if (index >= 0)
                        {
                            count = index - link.CurrentOffset + 1;
                            reason |= Vxi11Constants.ReasonChr;
                        }
                    }
                    var chunk = link.Current.AsSpan(link.CurrentOffset, count).ToArray();
                    link.CurrentOffset += count;
                    if (link.CurrentOffset >= link.Current.Length)
                    {
                        reason |= Vxi11Constants.ReasonEnd;
                        link.Current = null;
                        link.CurrentOffset = 0;
                    }
                    if (count == requestSize) reason |= Vxi11Constants.ReasonRequestCount;
                    results.WriteInt32(0);
                    results.WriteInt32(reason);
                    results.WriteOpaque(chunk);
                }
                return OncRpc.AcceptSuccess;
            }

            case Vxi11Constants.ProcDeviceReadStb:
            {
                int lid = ReadGeneric(args);
                if (!TryGetLink(lid, out var link)) return Error(results, Vxi11Constants.ErrInvalidLinkIdentifier, extraWords: 1);
                bool mav;
                lock (_sync) mav = link.Current is not null || link.Output.Count > 0;
                results.WriteInt32(0);
                results.WriteUInt32(_device.GetStatusByte(mav));
                return OncRpc.AcceptSuccess;
            }

            case Vxi11Constants.ProcDeviceTrigger:
            {
                if (!TryGetLink(ReadGeneric(args), out _)) return Error(results, Vxi11Constants.ErrInvalidLinkIdentifier);
                _device.Trigger();
                results.WriteInt32(0);
                return OncRpc.AcceptSuccess;
            }

            case Vxi11Constants.ProcDeviceClear:
            {
                if (!TryGetLink(ReadGeneric(args), out var link)) return Error(results, Vxi11Constants.ErrInvalidLinkIdentifier);
                lock (_sync)
                {
                    link.Generation++;
                    link.Input.Clear();
                    link.Output.Clear();
                    link.Current = null;
                    link.CurrentOffset = 0;
                }
                _device.DeviceClear();
                results.WriteInt32(0);
                return OncRpc.AcceptSuccess;
            }

            case Vxi11Constants.ProcDeviceRemote:
            case Vxi11Constants.ProcDeviceLocal:
            {
                if (!TryGetLink(ReadGeneric(args), out _)) return Error(results, Vxi11Constants.ErrInvalidLinkIdentifier);
                IsRemote = context.Procedure == Vxi11Constants.ProcDeviceRemote;
                _device.IsRemote = IsRemote;
                results.WriteInt32(0);
                return OncRpc.AcceptSuccess;
            }

            case Vxi11Constants.ProcDeviceLock:
            {
                int lid = args.ReadInt32();
                int flags = args.ReadInt32();
                uint lockTimeout = args.ReadUInt32();
                if (!TryGetLink(lid, out _)) return Error(results, Vxi11Constants.ErrInvalidLinkIdentifier);
                results.WriteInt32(AcquireLock(lid, (flags & Vxi11Constants.FlagWaitLock) != 0, lockTimeout));
                return OncRpc.AcceptSuccess;
            }

            case Vxi11Constants.ProcDeviceUnlock:
            {
                int lid = args.ReadInt32();
                lock (_sync)
                {
                    if (_lockOwner != lid)
                    {
                        results.WriteInt32(Vxi11Constants.ErrNoLockHeld);
                        return OncRpc.AcceptSuccess;
                    }
                    _lockOwner = null;
                    Monitor.PulseAll(_sync);
                }
                results.WriteInt32(0);
                return OncRpc.AcceptSuccess;
            }

            case Vxi11Constants.ProcDeviceEnableSrq:
            {
                int lid = args.ReadInt32();
                bool enable = args.ReadBool();
                var handle = args.ReadOpaqueSpan().ToArray();
                if (!TryGetLink(lid, out var link)) return Error(results, Vxi11Constants.ErrInvalidLinkIdentifier);
                lock (_sync) link.SrqHandle = enable ? handle : null;
                results.WriteInt32(0);
                return OncRpc.AcceptSuccess;
            }

            case Vxi11Constants.ProcCreateIntrChan:
            {
                uint address = args.ReadUInt32();
                uint port = args.ReadUInt32();
                args.ReadUInt32();
                args.ReadUInt32();
                args.ReadUInt32();
                var state = StateOf(context.Connection);
                lock (_sync)
                {
                    if (state.InterruptEndPoint is not null)
                    {
                        results.WriteInt32(Vxi11Constants.ErrChannelAlreadyEstablished);
                        return OncRpc.AcceptSuccess;
                    }
                    var ip = new IPAddress([(byte)(address >> 24), (byte)(address >> 16), (byte)(address >> 8), (byte)address]);
                    state.InterruptEndPoint = new IPEndPoint(ip, (int)port);
                }
                results.WriteInt32(0);
                return OncRpc.AcceptSuccess;
            }

            case Vxi11Constants.ProcDestroyIntrChan:
            {
                var state = StateOf(context.Connection);
                RpcTcpClient? client;
                lock (_sync)
                {
                    if (state.InterruptEndPoint is null)
                    {
                        results.WriteInt32(Vxi11Constants.ErrChannelNotEstablished);
                        return OncRpc.AcceptSuccess;
                    }
                    client = state.Interrupt;
                    state.Interrupt = null;
                    state.InterruptEndPoint = null;
                }
                client?.Dispose();
                results.WriteInt32(0);
                return OncRpc.AcceptSuccess;
            }

            case Vxi11Constants.ProcDeviceDocmd:
                results.WriteInt32(Vxi11Constants.ErrOperationNotSupported);
                results.WriteOpaque(ReadOnlySpan<byte>.Empty);
                return OncRpc.AcceptSuccess;

            case Vxi11Constants.ProcDestroyLink:
            {
                int lid = args.ReadInt32();
                lock (_sync)
                {
                    if (!_links.Remove(lid))
                    {
                        results.WriteInt32(Vxi11Constants.ErrInvalidLinkIdentifier);
                        return OncRpc.AcceptSuccess;
                    }
                    StateOf(context.Connection).Links.Remove(lid);
                    if (_lockOwner == lid) _lockOwner = null;
                    Monitor.PulseAll(_sync);
                }
                results.WriteInt32(0);
                return OncRpc.AcceptSuccess;
            }

            default:
                return OncRpc.AcceptProcUnavail;
        }
    }

    private static int ReadGeneric(XdrReader args)
    {
        int lid = args.ReadInt32();
        args.ReadInt32();
        args.ReadUInt32();
        args.ReadUInt32();
        return lid;
    }

    private static uint Error(XdrWriter results, int error, int extraWords = 0)
    {
        results.WriteInt32(error);
        for (int i = 0; i < extraWords; i++) results.WriteUInt32(0);
        return OncRpc.AcceptSuccess;
    }

    private static uint ReadError(XdrWriter results, int error)
    {
        results.WriteInt32(error);
        results.WriteInt32(0);
        results.WriteOpaque(ReadOnlySpan<byte>.Empty);
        return OncRpc.AcceptSuccess;
    }

    private bool TryGetLink(int lid, out Link link)
    {
        lock (_sync) return _links.TryGetValue(lid, out link!);
    }

    private int CheckLock(int lid, int flags, uint lockTimeout)
    {
        lock (_sync)
        {
            if (_lockOwner is null || _lockOwner == lid) return 0;
            if ((flags & Vxi11Constants.FlagWaitLock) == 0) return Vxi11Constants.ErrDeviceLocked;
            var deadline = Deadline.FromMilliseconds((int)Math.Min(lockTimeout, int.MaxValue));
            while (_lockOwner is not null && _lockOwner != lid)
            {
                int remaining = deadline.RemainingMilliseconds;
                if (remaining == 0) return Vxi11Constants.ErrDeviceLocked;
                Monitor.Wait(_sync, remaining);
            }
            return 0;
        }
    }

    private int AcquireLock(int lid, bool wait, uint lockTimeout)
    {
        lock (_sync)
        {
            var deadline = Deadline.FromMilliseconds(wait ? (int)Math.Min(lockTimeout, int.MaxValue) : 0);
            while (_lockOwner is not null && _lockOwner != lid)
            {
                // A link destroyed while waiting must not end up owning the lock.
                if (_disposed || !_links.ContainsKey(lid)) return Vxi11Constants.ErrInvalidLinkIdentifier;
                int remaining = deadline.RemainingMilliseconds;
                if (remaining == 0) return Vxi11Constants.ErrDeviceLocked;
                Monitor.Wait(_sync, remaining);
            }
            _lockOwner = lid;
            return 0;
        }
    }

    private uint HandleAbort(RpcCallContext context, XdrReader args, XdrWriter results)
    {
        if (context.Procedure != Vxi11Constants.ProcDeviceAbort) return OncRpc.AcceptProcUnavail;
        int lid = args.ReadInt32();
        lock (_sync)
        {
            if (!_links.TryGetValue(lid, out var link))
            {
                results.WriteInt32(Vxi11Constants.ErrInvalidLinkIdentifier);
                return OncRpc.AcceptSuccess;
            }
            link.AbortRequested = true;
            Monitor.PulseAll(_sync);
        }
        results.WriteInt32(0);
        return OncRpc.AcceptSuccess;
    }

    private void OnConnectionClosed(RpcServerConnection connection)
    {
        if (connection.State is not ConnectionState state) return;
        lock (_sync)
        {
            foreach (int lid in state.Links)
            {
                _links.Remove(lid);
                if (_lockOwner == lid) _lockOwner = null;
            }
            state.Links.Clear();
            Monitor.PulseAll(_sync);
        }
        state.Interrupt?.Dispose();
    }

    private void OnServiceRequested(byte stb)
    {
        List<(ConnectionState State, byte[] Handle)> targets;
        lock (_sync)
        {
            targets = _links.Values
                .Where(l => l.SrqHandle is not null && l.Connection.State is ConnectionState { InterruptEndPoint: not null })
                .Select(l => ((ConnectionState)l.Connection.State!, l.SrqHandle!))
                .ToList();
        }
        foreach (var (state, handle) in targets)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    RpcTcpClient client;
                    lock (_sync)
                    {
                        if (state.InterruptEndPoint is null) return;
                        client = state.Interrupt ??= SyncProtocolIO.ConnectRpc(state.InterruptEndPoint, Vxi11Constants.InterruptProgram,
                            Vxi11Constants.Version, 2000);
                    }
                    client.Call(Vxi11Constants.ProcDeviceIntrSrq, w => w.WriteOpaque(handle), Deadline.FromMilliseconds(2000));
                }
                catch
                {
                    // The client went away.
                }
            });
        }
    }

    /// <summary>Stops the server and closes its connections.</summary>
    public void Dispose()
    {
        _disposed = true;
        _device.ServiceRequested -= OnServiceRequested;
        _portmapper?.Dispose();
        _core?.Dispose();
        _abort?.Dispose();
        try { _udp?.Dispose(); } catch { /* ignore */ }
        lock (_sync) Monitor.PulseAll(_sync);
    }
}

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VisaCs.Discovery;

namespace VisaCs.Simulation;

/// <summary>
/// Runs one simulated instrument behind VXI-11, HiSLIP, raw socket and VICP servers at once,
/// and produces matching resource names and <see cref="ResourceManagerOptions"/>.
/// </summary>
public sealed class InstrumentSimulator : IDisposable
{
    /// <summary>Creates a simulator serving one instrument over VXI-11, HiSLIP, a raw socket and VICP. Port 0 picks a free port.</summary>
    public InstrumentSimulator(SimulatedInstrument? instrument = null, IPAddress? bindAddress = null,
        int portmapperPort = 0, int hislipPort = 0, int socketPort = 0, int vicpPort = 0)
    {
        Instrument = instrument ?? new SimulatedInstrument();
        BindAddress = bindAddress ?? IPAddress.Loopback;
        Vxi11 = new Vxi11InstrumentServer(Instrument, BindAddress, portmapperPort);
        Hislip = new HislipInstrumentServer(Instrument, BindAddress, hislipPort);
        Socket = new SocketInstrumentServer(Instrument, BindAddress, socketPort);
        Vicp = new VicpInstrumentServer(Instrument, BindAddress, vicpPort);
    }

    /// <summary>The simulated instrument shared by all servers.</summary>
    public SimulatedInstrument Instrument { get; }
    /// <summary>The address the servers listen on.</summary>
    public IPAddress BindAddress { get; }
    /// <summary>The VXI-11 server (portmapper, core and abort channels).</summary>
    public Vxi11InstrumentServer Vxi11 { get; }
    /// <summary>The HiSLIP server.</summary>
    public HislipInstrumentServer Hislip { get; }
    /// <summary>The raw socket server.</summary>
    public SocketInstrumentServer Socket { get; }
    /// <summary>The LeCroy VICP server.</summary>
    public VicpInstrumentServer Vicp { get; }

    private string Host => BindAddress.Equals(IPAddress.Any) ? "127.0.0.1" : BindAddress.ToString();

    /// <summary>VISA resource name of the VXI-11 instrument.</summary>
    public string Vxi11Resource => $"TCPIP0::{Host}::inst0::INSTR";
    /// <summary>VISA resource name of the HiSLIP instrument.</summary>
    public string HislipResource => $"TCPIP0::{Host}::hislip0,{Hislip.Port}::INSTR";
    /// <summary>VISA resource name of the raw socket.</summary>
    public string SocketResource => $"TCPIP0::{Host}::{Socket.Port}::SOCKET";
    /// <summary>VISA resource name of the VICP instrument.</summary>
    public string VicpResource => $"VICP0::{Host}::INSTR";

    /// <summary>Starts all servers; returns this simulator.</summary>
    public InstrumentSimulator Start()
    {
        Vxi11.Start();
        Hislip.Start();
        Socket.Start();
        Vicp.Start();
        return this;
    }

    /// <summary>Options pointing the resource manager at the simulator's ports, with LAN discovery disabled.</summary>
    public ResourceManagerOptions CreateOptions() => new()
    {
        Vxi11PortmapperPort = Vxi11.PortmapperPort,
        VicpPort = Vicp.Port,
        HislipPort = Hislip.Port,
        EnableLanDiscovery = false,
        EnableMdnsDiscovery = false,
        EnableUsb = false,
        EnableSerial = false,
        EnableGpib = false,
    };

    /// <summary>Stops the server and closes its connections.</summary>
    public void Dispose()
    {
        Vxi11.Dispose();
        Hislip.Dispose();
        Socket.Dispose();
        Vicp.Dispose();
    }
}

/// <summary>
/// A unicast DNS-SD responder answering PTR queries for configured services. It does not join the
/// multicast group; point <see cref="ResourceManagerOptions.MdnsEndPoint"/> at <see cref="EndPoint"/>.
/// </summary>
public sealed class MdnsTestResponder : IDisposable
{
    private readonly Socket _socket;
    private volatile bool _disposed;

    /// <summary>Creates an mDNS responder bound to a free local port.</summary>
    public MdnsTestResponder()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        // On macOS, disposing a UDP socket waits for a blocked receive, which nothing else wakes: let it time out.
        _socket.ReceiveTimeout = 250;
    }

    /// <summary>The endpoint queries must be sent to.</summary>
    public IPEndPoint EndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    /// <summary>Services to announce: service type (e.g. <c>_hislip._tcp</c>), instance name, host, port, address and TXT records.</summary>
    public List<(string ServiceType, string Instance, string Host, int Port, IPAddress Address, Dictionary<string, string> Text)> Services { get; } = new();

    /// <summary>Starts answering queries; returns this responder.</summary>
    public MdnsTestResponder Start()
    {
        new Thread(Loop) { IsBackground = true, Name = "mDNS test responder" }.Start();
        return this;
    }

    private void Loop()
    {
        var buffer = new byte[1500];
        while (!_disposed)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int n;
            try { n = _socket.ReceiveFrom(buffer, ref from); }
            catch { if (_disposed) return; continue; }
            try
            {
                var questions = ParseQuestions(buffer.AsSpan(0, n).ToArray());
                var answers = Services.Where(s => questions.Contains(s.ServiceType + ".local", StringComparer.OrdinalIgnoreCase)).ToList();
                if (answers.Count == 0) continue;
                _socket.SendTo(BuildResponse(answers), from);
            }
            catch
            {
                // Ignore malformed queries.
            }
        }
    }

    private static List<string> ParseQuestions(byte[] message)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(4));
        int offset = 12;
        var result = new List<string>();
        for (int i = 0; i < count; i++)
        {
            result.Add(MdnsDiscovery.ReadName(message, ref offset));
            offset += 4;
        }
        return result;
    }

    private static byte[] BuildResponse(List<(string ServiceType, string Instance, string Host, int Port, IPAddress Address, Dictionary<string, string> Text)> services)
    {
        var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        header.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x8400);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], (ushort)(services.Count * 4));
        ms.Write(header);
        foreach (var s in services)
        {
            string type = s.ServiceType + ".local";
            string instance = s.Instance + "." + type;
            string host = s.Host + ".local";

            WriteRecord(ms, type, 12, w => MdnsDiscovery.WriteName(w, instance));
            WriteRecord(ms, instance, 33, w =>
            {
                Span<byte> srv = stackalloc byte[6];
                srv.Clear();
                BinaryPrimitives.WriteUInt16BigEndian(srv[4..], (ushort)s.Port);
                w.Write(srv);
                MdnsDiscovery.WriteName(w, host);
            });
            WriteRecord(ms, instance, 16, w =>
            {
                foreach (var (k, v) in s.Text)
                {
                    var entry = Encoding.UTF8.GetBytes($"{k}={v}");
                    w.WriteByte((byte)entry.Length);
                    w.Write(entry);
                }
                if (s.Text.Count == 0) w.WriteByte(0);
            });
            WriteRecord(ms, host, 1, w => w.Write(s.Address.GetAddressBytes()));
        }
        return ms.ToArray();
    }

    private static void WriteRecord(MemoryStream ms, string name, ushort type, Action<Stream> writeData)
    {
        MdnsDiscovery.WriteName(ms, name);
        var data = new MemoryStream();
        writeData(data);
        Span<byte> fixedPart = stackalloc byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart, type);
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart[2..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(fixedPart[4..], 120);
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart[8..], (ushort)data.Length);
        ms.Write(fixedPart);
        ms.Write(data.ToArray());
    }

    /// <summary>Stops the server and closes its connections.</summary>
    public void Dispose()
    {
        _disposed = true;
        _socket.Dispose();
    }
}

using System.Globalization;
using System.Net;
using System.Text;
using VisaCs;
using VisaCs.Simulation;

// visashell: a small command line front end for VisaCs.
var options = new ResourceManagerOptions();
var positional = new List<string>();
int? timeout = null;
var simulatorPorts = new Dictionary<string, int>();
IPAddress bind = IPAddress.Loopback;

for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value.");
    switch (a)
    {
        case "--timeout": timeout = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--portmapper-port": options.Vxi11PortmapperPort = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--vicp-port": options.VicpPort = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--no-lan": options.EnableLanDiscovery = false; options.EnableMdnsDiscovery = false; break;
        case "--probe": options.LanDiscoveryTargets.Add(IPAddress.Parse(Next())); break;
        case "--bind": bind = IPAddress.Parse(Next()); break;
        case "--portmapper" or "--hislip" or "--socket" or "--vicp": simulatorPorts[a[2..]] = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "-h" or "--help": positional.Clear(); positional.Add("help"); i = args.Length; break;
        default: positional.Add(a); break;
    }
}

string command = positional.Count > 0 ? positional[0].ToLowerInvariant() : "help";
string Arg(int index, string name) => positional.Count > index ? positional[index] : throw new ArgumentException($"Missing {name}.");
string Rest(int index) => string.Join(' ', positional.Skip(index));

try
{
    switch (command)
    {
        case "list":
        case "find":
        {
            using var rm = new ResourceManager(options);
            var found = rm.FindResources(positional.Count > 1 ? positional[1] : "?*");
            if (found.Count == 0) Console.WriteLine("No resources found.");
            foreach (var r in found)
            {
                string extra = string.Join("  ", new[] { r.Alias is null ? null : $"alias: {r.Alias}", r.Description }.Where(s => !string.IsNullOrEmpty(s)));
                Console.WriteLine(extra.Length > 0 ? $"{r.ResourceName,-50} {extra}" : r.ResourceName);
            }
            return 0;
        }
        case "parse":
        {
            using var rm = new ResourceManager(options);
            var p = rm.Parse(Arg(1, "resource name"));
            Console.WriteLine($"Canonical:  {p.Canonical}");
            Console.WriteLine($"Interface:  {p.InterfaceType} (board {p.Board})");
            Console.WriteLine($"Class:      {p.ResourceClass}");
            Console.WriteLine($"Type:       {p.GetType().Name}");
            switch (p)
            {
                case TcpipInstrumentResourceName t:
                {
                    string port = t.HislipPort?.ToString(CultureInfo.InvariantCulture) ?? "default";
                    Console.WriteLine($"Host:       {t.Host}");
                    Console.WriteLine($"Device:     {t.DeviceName} ({(t.IsHislip ? "HiSLIP, port " + port : "VXI-11")})");
                    break;
                }
                case TcpipSocketResourceName t:
                    Console.WriteLine($"Host:       {t.Host}");
                    Console.WriteLine($"Port:       {t.Port}");
                    break;
                case UsbResourceName u:
                    Console.WriteLine($"Vendor:     0x{u.VendorId:X4}");
                    Console.WriteLine($"Product:    0x{u.ProductId:X4}");
                    Console.WriteLine($"Serial:     {u.SerialNumber}");
                    Console.WriteLine($"Interface:  {(u.InterfaceNumber is { } n ? n.ToString(CultureInfo.InvariantCulture) : "first USBTMC interface")}");
                    break;
                case SerialResourceName a:
                    Console.WriteLine($"Port:       {a.PortName}");
                    break;
                case GpibInstrumentResourceName g:
                    Console.WriteLine($"Primary:    {g.PrimaryAddress}");
                    Console.WriteLine($"Secondary:  {(g.SecondaryAddress is { } s ? s.ToString(CultureInfo.InvariantCulture) : "none")}");
                    break;
                case VicpResourceName v:
                    Console.WriteLine($"Host:       {v.Host}");
                    break;
            }
            return 0;
        }
        case "query":
        {
            using var rm = new ResourceManager(options);
            using var session = Open(rm, Arg(1, "resource name"));
            Console.WriteLine(session.Query(Rest(2)));
            return 0;
        }
        case "write":
        {
            using var rm = new ResourceManager(options);
            using var session = Open(rm, Arg(1, "resource name"));
            session.WriteLine(Rest(2));
            return 0;
        }
        case "read":
        {
            using var rm = new ResourceManager(options);
            using var session = Open(rm, Arg(1, "resource name"));
            Console.WriteLine(session.ReadLine());
            return 0;
        }
        case "info":
        {
            using var rm = new ResourceManager(options);
            using var session = Open(rm, Arg(1, "resource name"));
            Console.WriteLine($"{session.GetType().Name} {session.ResourceName}");
            PrintAttributes(session);
            return 0;
        }
        case "shell":
        {
            using var rm = new ResourceManager(options);
            using var session = Open(rm, Arg(1, "resource name"));
            RunShell(session);
            return 0;
        }
        case "simulate":
        {
            using var sim = new InstrumentSimulator(null, bind,
                simulatorPorts.GetValueOrDefault("portmapper"), simulatorPorts.GetValueOrDefault("hislip"),
                simulatorPorts.GetValueOrDefault("socket"), simulatorPorts.GetValueOrDefault("vicp")).Start();
            Console.WriteLine("Simulated instrument running. Resources:");
            Console.WriteLine($"  VXI-11  {sim.Vxi11Resource}   (portmapper port {sim.Vxi11.PortmapperPort})");
            Console.WriteLine($"  HiSLIP  {sim.HislipResource}");
            Console.WriteLine($"  Socket  {sim.SocketResource}");
            Console.WriteLine($"  VICP    {sim.VicpResource}   (VICP port {sim.Vicp.Port})");
            if (sim.Vxi11.PortmapperPort != 111 || sim.Vicp.Port != 1861)
            {
                Console.WriteLine();
                Console.WriteLine($"Clients need: --portmapper-port {sim.Vxi11.PortmapperPort} --vicp-port {sim.Vicp.Port}");
            }
            Console.WriteLine("Press Enter (or Ctrl+C) to stop.");
            if (Console.ReadLine() is null) Thread.Sleep(Timeout.Infinite); // no console input: run until killed
            return 0;
        }
        default:
            PrintHelp();
            return command == "help" ? 0 : 1;
    }
}
catch (VisaException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    PrintHelp();
    return 1;
}

MessageBasedSession Open(ResourceManager rm, string resource)
{
    var session = rm.Open(resource);
    if (timeout is { } t) session.TimeoutMilliseconds = t;
    return session;
}

static void PrintAttributes(VisaSession session)
{
    foreach (var attribute in session.SupportedAttributes.OrderBy(a => VisaAttributeNames.GetName(a)))
    {
        string value;
        try
        {
            var v = session.GetAttribute(attribute);
            value = v is byte b && attribute == VisaAttribute.TerminationCharacter ? $"0x{b:X2}" : Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        }
        catch (VisaException ex)
        {
            value = $"<{ex.StatusName}>";
        }
        Console.WriteLine($"  {VisaAttributeNames.GetName(attribute),-36} {value}");
    }
}

static void RunShell(MessageBasedSession session)
{
    Console.WriteLine($"Connected to {session.ResourceName} ({session.GetType().Name}). Type :help for commands.");
    session.ServiceRequest += (_, e) => Console.WriteLine($"\n[SRQ{(e.StatusByte is { } stb ? $" STB=0x{stb:X2}" : "")}]");
    while (true)
    {
        Console.Write("> ");
        var line = Console.ReadLine();
        if (line is null) return;
        line = line.Trim();
        if (line.Length == 0) continue;
        try
        {
            if (!line.StartsWith(':'))
            {
                string head = line.Split(' ', 2)[0];
                if (head.EndsWith('?')) Console.WriteLine(session.Query(line));
                else session.WriteLine(line);
                continue;
            }
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0].ToLowerInvariant())
            {
                case ":quit" or ":exit" or ":q":
                    return;
                case ":read":
                    Console.WriteLine(session.ReadLine());
                    break;
                case ":stb":
                    Console.WriteLine($"0x{session.ReadStatusByte():X2}");
                    break;
                case ":clear":
                    session.Clear();
                    break;
                case ":trigger":
                    session.AssertTrigger();
                    break;
                case ":timeout":
                    if (parts.Length > 1) session.TimeoutMilliseconds = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    Console.WriteLine($"{session.TimeoutMilliseconds} ms");
                    break;
                case ":termchar":
                    if (parts.Length > 1)
                    {
                        session.TerminationCharacterEnabled = parts[1] is not ("off" or "none");
                        if (session.TerminationCharacterEnabled && parts[1] != "on")
                            session.TerminationCharacter = parts[1] switch { "\\n" or "lf" => (byte)'\n', "\\r" or "cr" => (byte)'\r', _ => (byte)parts[1][0] };
                    }
                    Console.WriteLine($"enabled={session.TerminationCharacterEnabled} char=0x{session.TerminationCharacter:X2}");
                    break;
                case ":block":
                {
                    var data = session.QueryBinaryBlock(line[(parts[0].Length + 1)..]);
                    Console.WriteLine($"{data.Length} bytes: {Convert.ToHexString(data.AsSpan(0, Math.Min(32, data.Length)))}{(data.Length > 32 ? "..." : "")}");
                    break;
                }
                case ":attr":
                    if (parts.Length == 1) PrintAttributes(session);
                    else if (VisaAttributeNames.TryParse(parts[1], out var attribute))
                    {
                        if (parts.Length > 2) session.SetAttribute(attribute, parts[2]);
                        Console.WriteLine(session.GetAttribute(attribute));
                    }
                    else Console.WriteLine($"Unknown attribute {parts[1]}");
                    break;
                case ":lock":
                    session.LockExclusive(session.TimeoutMilliseconds);
                    Console.WriteLine("locked");
                    break;
                case ":unlock":
                    session.Unlock();
                    Console.WriteLine("unlocked");
                    break;
                case ":srq":
                    session.EnableEvent(EventType.ServiceRequest, EventMechanism.Handler);
                    Console.WriteLine("Service requests will be printed.");
                    break;
                case ":local":
                    session.ControlRen(RemoteLocalMode.DeassertGoToLocal);
                    break;
                case ":remote":
                    session.ControlRen(RemoteLocalMode.AssertAddress);
                    break;
                default:
                    Console.WriteLine("""
                        Lines whose first word ends in '?' are queries; anything else is written.
                          :read              read a response
                          :stb               read the status byte
                          :clear             device clear
                          :trigger           assert trigger
                          :timeout [ms]      show or set the timeout
                          :termchar [c|off]  show or set the read termination character
                          :block <cmd>       query an IEEE 488.2 binary block
                          :attr [NAME [val]] list, read or set attributes (VI_ATTR_...)
                          :lock / :unlock    exclusive lock
                          :srq               print service requests as they arrive
                          :remote / :local   remote/local control
                          :quit
                        """);
                    break;
            }
        }
        catch (VisaException ex)
        {
            Console.WriteLine(ex.Message);
        }
    }
}

static void PrintHelp()
{
    Console.WriteLine("""
        visashell - VISA command line (VisaCs)

        Usage:
          visashell list [expression]            find resources (default "?*")
          visashell parse <resource>             show how a resource name is parsed
          visashell query <resource> <command>   write a command and print the response
          visashell write <resource> <command>
          visashell read <resource>
          visashell info <resource>              open and print all attributes
          visashell shell <resource>             interactive session
          visashell simulate                     run a simulated instrument (VXI-11, HiSLIP, socket, VICP)

        Options:
          --timeout <ms>            I/O timeout
          --portmapper-port <n>     VXI-11 portmapper port (default 111)
          --vicp-port <n>           VICP port (default 1861)
          --no-lan                  skip LAN discovery in list
          --probe <ip>              also probe this address for VXI-11 instruments
          simulate: --bind <ip> --portmapper <n> --hislip <n> --socket <n> --vicp <n>

        Examples:
          visashell list "USB?*INSTR"
          visashell query TCPIP::192.168.1.20::INSTR "*IDN?"
          visashell query "TCPIP::192.168.1.20::hislip0::INSTR" "MEAS:VOLT:DC?"
          visashell shell ASRL3::INSTR
        """);
}

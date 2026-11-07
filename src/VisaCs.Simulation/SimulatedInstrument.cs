using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace VisaCs.Simulation;

/// <summary>
/// A simple IEEE 488.2 / SCPI instrument model shared by all simulated transports.
/// Responses are returned per message (without terminator); transports add their own framing.
/// </summary>
/// <remarks>
/// Supported commands: *IDN? *RST *CLS *ESE[?] *ESR? *SRE[?] *STB? *OPC[?] *TRG *WAI *TST?,
/// ECHO? &lt;text&gt;, MEAS:VOLT?, SYST:ERR?, TRIG:COUNT?, SRQ (sets status bit 0),
/// DATA:BIN? n, DATA:BIN:INDEF? n, DATA:FLOAT? n, DATA:ASCII? n, DATA:LARGE? n,
/// DATA:STORE #&lt;block&gt;, DATA:STORE?, DELAY ms, DELAY? ms, plus anything handled by <see cref="CustomHandler"/>.
/// </remarks>
public class SimulatedInstrument
{
    private readonly Lock _sync = new();
    private readonly Queue<string> _errors = new();
    private byte _ese;
    private byte _sre;
    private byte _esr;
    private byte _userStatus;
    private bool _srqAsserted;
    private byte[] _stored = Array.Empty<byte>();
    private int _triggerCount;
    private int _clearCount;

    /// <summary>Response to *IDN?.</summary>
    public string Identity { get; set; } = "VisaCs,Simulated Instrument,SIM0001,1.0.0";

    /// <summary>Value returned by MEAS:VOLT?.</summary>
    public double Voltage { get; set; } = 1.2345;

    /// <summary>Number of triggers received.</summary>
    public int TriggerCount => Volatile.Read(ref _triggerCount);

    /// <summary>Number of device clears received.</summary>
    public int ClearCount => Volatile.Read(ref _clearCount);

    /// <summary>True while the instrument is in remote state.</summary>
    public bool IsRemote { get; set; }

    /// <summary>True while local lockout is in effect.</summary>
    public bool LocalLockout { get; set; }

    /// <summary>Every command received, in order.</summary>
    public ConcurrentQueue<string> CommandLog { get; } = new();

    /// <summary>Raised when the request-service bit of the status byte becomes set. Argument: status byte.</summary>
    public event Action<byte>? ServiceRequested;

    /// <summary>Raised when the device is triggered.</summary>
    public event Action? Triggered;

    /// <summary>Handles commands the model does not know. Return null for no response.</summary>
    public Func<string, string?>? CustomHandler { get; set; }

    /// <summary>Computes the IEEE 488.2 status byte. <paramref name="messageAvailable"/> sets MAV (bit 4).</summary>
    public byte GetStatusByte(bool messageAvailable)
    {
        lock (_sync) return StatusByteLocked(messageAvailable);
    }

    private byte StatusByteLocked(bool messageAvailable)
    {
        byte stb = (byte)(_userStatus & 0x0F);
        if (messageAvailable) stb |= 0x10;
        if ((_esr & _ese) != 0) stb |= 0x20;
        if ((stb & _sre & 0xBF) != 0) stb |= 0x40;
        return stb;
    }

    /// <summary>Processes one program message and returns the responses it generates.</summary>
    public List<byte[]> Process(ReadOnlySpan<byte> message)
    {
        var responses = new List<byte[]>();
        string text = Encoding.Latin1.GetString(message);

        const string storePrefix = "DATA:STORE ";
        if (text.StartsWith(storePrefix, StringComparison.OrdinalIgnoreCase))
        {
            CommandLog.Enqueue("DATA:STORE");
            var block = ParseBlock(message[storePrefix.Length..]);
            lock (_sync) _stored = block;
            return responses;
        }

        foreach (var raw in text.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string command = raw.Trim('\r', ' ', '\t');
            if (command.Length == 0) continue;
            CommandLog.Enqueue(command);
            var response = Execute(command);
            if (response is not null) responses.Add(response);
        }
        CheckServiceRequest();
        return responses;
    }

    private static byte[] ParseBlock(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2 || data[0] != '#') return data.ToArray();
        int digits = data[1] - '0';
        if (digits == 0) return data[2..].TrimEnd((byte)'\n').ToArray();
        int length = int.Parse(Encoding.ASCII.GetString(data.Slice(2, digits)), CultureInfo.InvariantCulture);
        return data.Slice(2 + digits, Math.Min(length, data.Length - 2 - digits)).ToArray();
    }

    /// <summary>Formats bytes as an IEEE 488.2 definite-length block (<c>#&lt;n&gt;&lt;length&gt;&lt;data&gt;</c>).</summary>
    public static byte[] Block(ReadOnlySpan<byte> data)
    {
        string length = data.Length.ToString(CultureInfo.InvariantCulture);
        var header = Encoding.ASCII.GetBytes($"#{length.Length}{length}");
        var result = new byte[header.Length + data.Length];
        header.CopyTo(result, 0);
        data.CopyTo(result.AsSpan(header.Length));
        return result;
    }

    private static byte[] Text(string s) => Encoding.Latin1.GetBytes(s);

    private static int IntArg(string arg) =>
        (int)double.Parse(arg.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private byte[]? Execute(string command)
    {
        int space = command.IndexOf(' ');
        string header = (space < 0 ? command : command[..space]).ToUpperInvariant();
        string arg = space < 0 ? "" : command[(space + 1)..].Trim();

        // Commands that block must not hold the lock.
        if (header == "DELAY")
        {
            Thread.Sleep(IntArg(arg));
            return null;
        }
        if (header == "DELAY?")
        {
            Thread.Sleep(IntArg(arg));
            return Text("DONE");
        }

        lock (_sync)
        {
            switch (header)
            {
                case "*IDN?": return Text(Identity);
                case "*RST":
                    _ese = _sre = _esr = _userStatus = 0;
                    _errors.Clear();
                    return null;
                case "*CLS":
                    _esr = 0;
                    _userStatus = 0;
                    _errors.Clear();
                    _srqAsserted = false;
                    return null;
                case "*ESE": _ese = (byte)IntArg(arg); return null;
                case "*ESE?": return Text(_ese.ToString(CultureInfo.InvariantCulture));
                case "*ESR?":
                {
                    byte value = _esr;
                    _esr = 0;
                    return Text(value.ToString(CultureInfo.InvariantCulture));
                }
                case "*SRE": _sre = (byte)IntArg(arg); return null;
                case "*SRE?": return Text(_sre.ToString(CultureInfo.InvariantCulture));
                case "*STB?": return Text(StatusByteLocked(false).ToString(CultureInfo.InvariantCulture));
                case "*OPC": _esr |= 0x01; return null;
                case "*OPC?": return Text("1");
                case "*TRG": TriggerLocked(); return null;
                case "*WAI": return null;
                case "*TST?": return Text("0");
                case "ECHO?": return Text(arg);
                case "MEAS:VOLT?": return Text(Voltage.ToString("+0.00000000E+00;-0.00000000E+00", CultureInfo.InvariantCulture));
                case "SYST:ERR?": return Text(_errors.Count > 0 ? _errors.Dequeue() : "+0,\"No error\"");
                case "SYST:REM": IsRemote = true; return null;
                case "SYST:LOC": IsRemote = false; return null;
                case "TRIG:COUNT?": return Text(_triggerCount.ToString(CultureInfo.InvariantCulture));
                case "SRQ": _userStatus |= 0x01; return null;
                case "DATA:BIN?":
                {
                    var data = new byte[IntArg(arg)];
                    for (int i = 0; i < data.Length; i++) data[i] = (byte)i;
                    return Block(data);
                }
                case "DATA:BIN:INDEF?":
                {
                    var data = new byte[IntArg(arg) + 2];
                    data[0] = (byte)'#';
                    data[1] = (byte)'0';
                    for (int i = 2; i < data.Length; i++) data[i] = (byte)('a' + (i - 2) % 26);
                    return data;
                }
                case "DATA:FLOAT?":
                {
                    int n = IntArg(arg);
                    var data = new byte[n * 4];
                    for (int i = 0; i < n; i++) BitConverter.TryWriteBytes(data.AsSpan(i * 4), i * 0.5f);
                    return Block(data);
                }
                case "DATA:ASCII?":
                    return Text(string.Join(",", Enumerable.Range(0, IntArg(arg)).Select(i => (i * 0.5).ToString("E6", CultureInfo.InvariantCulture))));
                case "DATA:LARGE?":
                    return Text(new string('A', IntArg(arg)));
                case "DATA:STORE?":
                    return Block(_stored);
            }
        }

        var custom = CustomHandler?.Invoke(command);
        if (custom is not null) return Text(custom);
        if (header.EndsWith('?') || CustomHandler is null)
        {
            lock (_sync) _errors.Enqueue("-113,\"Undefined header\"");
        }
        return null;
    }

    private void TriggerLocked()
    {
        _triggerCount++;
        Triggered?.Invoke();
    }

    /// <summary>Triggers the instrument (GPIB GET, VXI-11 device_trigger, HiSLIP Trigger, USB488 TRIGGER).</summary>
    public void Trigger()
    {
        lock (_sync) TriggerLocked();
    }

    /// <summary>Device clear: transports discard their own buffered input and output.</summary>
    public void DeviceClear()
    {
        Interlocked.Increment(ref _clearCount);
    }

    /// <summary>Raises a service request regardless of the enable registers (as if from the front panel).</summary>
    public void RequestService()
    {
        byte stb;
        lock (_sync)
        {
            _userStatus |= 0x01;
            stb = (byte)(StatusByteLocked(false) | 0x40);
        }
        ServiceRequested?.Invoke(stb);
    }

    private void CheckServiceRequest()
    {
        byte stb;
        bool raise = false;
        lock (_sync)
        {
            stb = StatusByteLocked(false);
            bool rqs = (stb & 0x40) != 0;
            if (rqs && !_srqAsserted) raise = true;
            _srqAsserted = rqs;
        }
        if (raise) ServiceRequested?.Invoke(stb);
    }
}

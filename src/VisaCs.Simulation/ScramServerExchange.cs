using System.Security.Cryptography;
using System.Text;
using VisaCs.Protocols.Hislip;

namespace VisaCs.Simulation;

/// <summary>Server side of SCRAM-SHA-256(-PLUS), used by the HiSLIP simulator.</summary>
internal sealed class ScramSha256ServerExchange
{
    private readonly Func<string, string?> _passwordLookup;
    private readonly byte[]? _channelBinding;
    private readonly bool _plusMechanism;
    private readonly bool _plusAdvertised;
    private readonly int _iterations;
    private string _gs2Header = "";
    private string _clientFirstBare = "";
    private string _serverFirst = "";
    private string _nonce = "";
    private byte[] _salted = [];

    public ScramSha256ServerExchange(Func<string, string?> passwordLookup, byte[]? channelBinding, bool plusMechanism, bool plusAdvertised, int iterations = 4096)
    {
        _passwordLookup = passwordLookup;
        _channelBinding = channelBinding;
        _plusMechanism = plusMechanism;
        _plusAdvertised = plusAdvertised;
        _iterations = iterations;
    }

    public string? User { get; private set; }

    /// <summary>Processes client-first; returns server-first, or null to fail.</summary>
    public byte[]? First(byte[] clientFirst)
    {
        string text = Encoding.UTF8.GetString(clientFirst);
        // gs2-header = cbind-flag "," [authzid] ","
        int first = text.IndexOf(',');
        int second = first < 0 ? -1 : text.IndexOf(',', first + 1);
        if (second < 0) return null;
        string flag = text[..first];
        _gs2Header = text[..(second + 1)];
        _clientFirstBare = text[(second + 1)..];
        if (_plusMechanism && flag != "p=tls-server-end-point") return null;
        if (!_plusMechanism && flag.StartsWith("p=", StringComparison.Ordinal)) return null;
        if (flag == "y" && _plusAdvertised) return null; // downgrade attack

        var fields = ScramFields.Parse(_clientFirstBare);
        if (!fields.TryGetValue('n', out var user) || !fields.TryGetValue('r', out var clientNonce)) return null;
        User = user.Replace("=2C", ",").Replace("=3D", "=");
        string? password = _passwordLookup(User);
        if (password is null) return null;
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        _salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Sasl.Prepare(password)), salt, _iterations, HashAlgorithmName.SHA256, 32);
        _nonce = clientNonce + Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        _serverFirst = $"r={_nonce},s={Convert.ToBase64String(salt)},i={_iterations}";
        return Encoding.UTF8.GetBytes(_serverFirst);
    }

    /// <summary>Processes client-final; returns server-final ("v=...") on success, or null to fail.</summary>
    public byte[]? Final(byte[] clientFinal)
    {
        string text = Encoding.UTF8.GetString(clientFinal);
        int proofIndex = text.LastIndexOf(",p=", StringComparison.Ordinal);
        if (proofIndex < 0) return null;
        string withoutProof = text[..proofIndex];
        var fields = ScramFields.Parse(text);
        if (!fields.TryGetValue('c', out var cbind) || !fields.TryGetValue('r', out var nonce) || !fields.TryGetValue('p', out var proofText))
            return null;
        if (nonce != _nonce) return null;
        byte[] expectedCbind = [.. Encoding.ASCII.GetBytes(_gs2Header), .. (_plusMechanism ? _channelBinding ?? [] : [])];
        if (Convert.ToBase64String(expectedCbind) != cbind) return null;

        byte[] clientKey = HMACSHA256.HashData(_salted, "Client Key"u8);
        byte[] storedKey = SHA256.HashData(clientKey);
        byte[] serverKey = HMACSHA256.HashData(_salted, "Server Key"u8);
        byte[] authMessage = Encoding.UTF8.GetBytes($"{_clientFirstBare},{_serverFirst},{withoutProof}");
        byte[] clientSignature = HMACSHA256.HashData(storedKey, authMessage);
        byte[] proof;
        try
        {
            proof = Convert.FromBase64String(proofText);
        }
        catch (FormatException)
        {
            return null;
        }
        if (proof.Length != clientSignature.Length) return null;
        byte[] recoveredKey = new byte[proof.Length];
        for (int i = 0; i < proof.Length; i++) recoveredKey[i] = (byte)(proof[i] ^ clientSignature[i]);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(recoveredKey), storedKey)) return null;
        return Encoding.UTF8.GetBytes($"v={Convert.ToBase64String(HMACSHA256.HashData(serverKey, authMessage))}");
    }
}

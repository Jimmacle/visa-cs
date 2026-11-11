using System.Security.Cryptography;
using System.Text;
using VisaCs.Internal;

namespace VisaCs.Lan.Protocols;

/// <summary>A client-side SASL mechanism (RFC 4422).</summary>
internal interface ISaslClientMechanism
{
    string Name { get; }

    /// <summary>The initial response sent with AuthenticationExchange right after AuthenticationStart.</summary>
    byte[] InitialResponse();

    /// <summary>Answers a server challenge.</summary>
    byte[] Respond(byte[] challenge);

    /// <summary>Checks the additional data sent with a successful AuthenticationResult (SCRAM server signature).</summary>
    bool VerifyOutcome(byte[] additionalData);
}

internal static class Sasl
{
    public const string ScramSha256Plus = "SCRAM-SHA-256-PLUS";
    public const string ScramSha256 = "SCRAM-SHA-256";
    public const string Plain = "PLAIN";
    public const string External = "EXTERNAL";
    public const string Anonymous = "ANONYMOUS";

    /// <summary>RFC 4013 SASLprep (mapping and NFKC normalization; prohibited-character checks are left to the server).</summary>
    public static string Prepare(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            // RFC 3454 table B.1: commonly mapped to nothing.
            if (c is '\u00AD' or '\u034F' or '\u1806' or (>= '\u180B' and <= '\u180D') or (>= '\u200B' and <= '\u200D') or '\u2060' or
                (>= '\uFE00' and <= '\uFE0F') or '\uFEFF')
                continue;
            // RFC 3454 table C.1.2: non-ASCII space characters, mapped to SPACE.
            if (c is '\u00A0' or '\u1680' or (>= '\u2000' and <= '\u200A') or '\u202F' or '\u205F' or '\u3000')
            {
                sb.Append(' ');
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormKC);
    }
}

internal sealed class AnonymousMechanism(string trace) : ISaslClientMechanism
{
    public string Name => Sasl.Anonymous;
    public byte[] InitialResponse() => Encoding.UTF8.GetBytes(trace);
    public byte[] Respond(byte[] challenge) => [];
    public bool VerifyOutcome(byte[] additionalData) => true;
}

internal sealed class PlainMechanism(string user, string password, string? authorizationId) : ISaslClientMechanism
{
    public string Name => Sasl.Plain;
    public byte[] InitialResponse() => Encoding.UTF8.GetBytes($"{authorizationId}\0{user}\0{password}");
    public byte[] Respond(byte[] challenge) => InitialResponse();
    public bool VerifyOutcome(byte[] additionalData) => true;
}

internal sealed class ExternalMechanism(string? authorizationId) : ISaslClientMechanism
{
    public string Name => Sasl.External;
    public byte[] InitialResponse() => Encoding.UTF8.GetBytes(authorizationId ?? "");
    public byte[] Respond(byte[] challenge) => InitialResponse();
    public bool VerifyOutcome(byte[] additionalData) => true;
}

/// <summary>SCRAM-SHA-256 and SCRAM-SHA-256-PLUS client (RFC 5802 / RFC 7677).</summary>
internal sealed class ScramSha256ClientMechanism : ISaslClientMechanism
{
    private readonly string _user;
    private readonly string _password;
    private readonly byte[]? _channelBinding;
    private readonly string _gs2Header;
    private readonly string _clientNonce;
    private string _clientFirstBare = "";
    private byte[] _expectedServerSignature = [];

    /// <summary>
    /// Creates the mechanism. Pass tls-server-end-point data as <paramref name="channelBinding"/> for -PLUS, or null
    /// for plain SCRAM; <paramref name="channelBindingAvailable"/> selects the gs2 'y' flag when not using -PLUS.
    /// </summary>
    public ScramSha256ClientMechanism(string user, string password, byte[]? channelBinding, bool channelBindingAvailable, string? nonce = null)
    {
        _user = user;
        _password = password;
        _channelBinding = channelBinding;
        _gs2Header = channelBinding is not null ? "p=tls-server-end-point,," : channelBindingAvailable ? "y,," : "n,,";
        _clientNonce = nonce ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
    }

    public string Name => _channelBinding is not null ? Sasl.ScramSha256Plus : Sasl.ScramSha256;

    internal static string EscapeName(string name) => name.Replace("=", "=3D").Replace(",", "=2C");

    public byte[] InitialResponse()
    {
        _clientFirstBare = $"n={EscapeName(Sasl.Prepare(_user))},r={_clientNonce}";
        return Encoding.UTF8.GetBytes(_gs2Header + _clientFirstBare);
    }

    /// <summary>Largest iteration count accepted from a server: PBKDF2 cannot be cancelled, and a hostile count could stall the client.</summary>
    public const int MaxIterations = 10_000_000;

    public byte[] Respond(byte[] challenge)
    {
        string serverFirst = Encoding.UTF8.GetString(challenge);
        var fields = ScramFields.Parse(serverFirst);
        if (fields.TryGetValue('e', out var error))
            throw LanErrors.Security(LanSecurityFailure.Authentication, $"SCRAM error from the server: {error}");
        if (fields.ContainsKey('m'))
            throw LanErrors.Security(LanSecurityFailure.Authentication, "The SCRAM server-first message requires an unsupported extension.");
        if (!fields.TryGetValue('r', out var nonce) || nonce.Length <= _clientNonce.Length || !nonce.StartsWith(_clientNonce, StringComparison.Ordinal) ||
            !fields.TryGetValue('s', out var saltText) || !fields.TryGetValue('i', out var iterationText) ||
            !int.TryParse(iterationText, out int iterations) || iterations < 1)
            throw LanErrors.Security(LanSecurityFailure.Authentication, "Invalid SCRAM server-first message.");
        if (iterations > MaxIterations)
            throw LanErrors.Security(LanSecurityFailure.Authentication,
                $"The server asks for {iterations} SCRAM iterations; at most {MaxIterations} are accepted.");
        byte[] salt;
        try
        {
            salt = Convert.FromBase64String(saltText);
        }
        catch (FormatException ex)
        {
            throw LanErrors.Security(LanSecurityFailure.Authentication, "Invalid SCRAM server-first message (salt).", ex);
        }

        byte[] salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Sasl.Prepare(_password)), salt, iterations, HashAlgorithmName.SHA256, 32);
        byte[] clientKey = HMACSHA256.HashData(salted, "Client Key"u8);
        byte[] storedKey = SHA256.HashData(clientKey);
        byte[] serverKey = HMACSHA256.HashData(salted, "Server Key"u8);

        byte[] cbindInput = [.. Encoding.ASCII.GetBytes(_gs2Header), .. _channelBinding ?? []];
        string clientFinalWithoutProof = $"c={Convert.ToBase64String(cbindInput)},r={nonce}";
        byte[] authMessage = Encoding.UTF8.GetBytes($"{_clientFirstBare},{serverFirst},{clientFinalWithoutProof}");
        byte[] clientSignature = HMACSHA256.HashData(storedKey, authMessage);
        byte[] proof = new byte[clientKey.Length];
        for (int i = 0; i < proof.Length; i++) proof[i] = (byte)(clientKey[i] ^ clientSignature[i]);
        _expectedServerSignature = HMACSHA256.HashData(serverKey, authMessage);
        return Encoding.UTF8.GetBytes($"{clientFinalWithoutProof},p={Convert.ToBase64String(proof)}");
    }

    public bool VerifyOutcome(byte[] additionalData)
    {
        if (additionalData.Length == 0) return false; // SCRAM requires the server signature
        var fields = ScramFields.Parse(Encoding.UTF8.GetString(additionalData));
        if (!fields.TryGetValue('v', out var signature)) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(signature), _expectedServerSignature);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal static class ScramFields
{
    public static Dictionary<char, string> Parse(string message)
    {
        var result = new Dictionary<char, string>();
        foreach (var part in message.Split(','))
        {
            if (part.Length >= 2 && part[1] == '=') result[part[0]] = part[2..];
        }
        return result;
    }
}

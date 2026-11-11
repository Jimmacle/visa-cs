using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using VisaCs.Internal;

namespace VisaCs.Lan.Protocols;

/// <summary>HiSLIP (IVI-6.1) message types.</summary>
internal enum HislipMessageType : byte
{
    Initialize = 0,
    InitializeResponse = 1,
    FatalError = 2,
    Error = 3,
    AsyncLock = 4,
    AsyncLockResponse = 5,
    Data = 6,
    DataEnd = 7,
    DeviceClearComplete = 8,
    DeviceClearAcknowledge = 9,
    AsyncRemoteLocalControl = 10,
    AsyncRemoteLocalResponse = 11,
    Trigger = 12,
    Interrupted = 13,
    AsyncInterrupted = 14,
    AsyncMaximumMessageSize = 15,
    AsyncMaximumMessageSizeResponse = 16,
    AsyncInitialize = 17,
    AsyncInitializeResponse = 18,
    AsyncDeviceClear = 19,
    AsyncServiceRequest = 20,
    AsyncStatusQuery = 21,
    AsyncStatusResponse = 22,
    AsyncDeviceClearAcknowledge = 23,
    AsyncLockInfo = 24,
    AsyncLockInfoResponse = 25,
    GetDescriptors = 26,
    GetDescriptorsResponse = 27,
    StartTls = 28,
    AsyncStartTls = 29,
    AsyncStartTlsResponse = 30,
    EndTls = 31,
    AsyncEndTls = 32,
    AsyncEndTlsResponse = 33,
    GetSaslMechanismList = 34,
    GetSaslMechanismListResponse = 35,
    AuthenticationStart = 36,
    AuthenticationExchange = 37,
    AuthenticationResult = 38,
    VendorSpecificFirst = 128,
}

internal readonly record struct HislipHeader(HislipMessageType Type, byte Control, uint Parameter, ulong PayloadLength);

internal sealed record HislipMessage(HislipHeader Header, byte[] Payload)
{
    public HislipMessageType Type => Header.Type;
    public byte Control => Header.Control;
    public uint Parameter => Header.Parameter;
    public string PayloadText => Encoding.ASCII.GetString(Payload);
}

/// <summary>HiSLIP framing helpers.</summary>
internal static class Hislip
{
    public const int HeaderSize = 16;
    public const ushort ProtocolVersion20 = 0x0200;
    /// <summary>Version this client offers. Servers answer with the lower of theirs and ours.</summary>
    public const ushort ProtocolVersion = ProtocolVersion20;
    public const uint InitialMessageId = 0xFFFFFF00;
    public const uint UnknownMessageId = 0xFFFFFFFF;
    /// <summary>Largest payload accepted in a message other than Data/DataEND.</summary>
    public const int MaxControlPayload = 1024 * 1024;

    public static void WriteHeader(Span<byte> destination, HislipMessageType type, byte control, uint parameter, ulong payloadLength)
    {
        destination[0] = (byte)'H';
        destination[1] = (byte)'S';
        destination[2] = (byte)type;
        destination[3] = control;
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], parameter);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], payloadLength);
    }

    public static HislipHeader ParseHeader(ReadOnlySpan<byte> source)
    {
        if (source[0] != 'H' || source[1] != 'S')
            throw LanErrors.Protocol("HiSLIP protocol error: invalid message prologue.");
        return new HislipHeader((HislipMessageType)source[2], source[3],
            BinaryPrimitives.ReadUInt32BigEndian(source[4..]),
            BinaryPrimitives.ReadUInt64BigEndian(source[8..]));
    }

    /// <summary>
    /// Sends one message. Use <see cref="NetUtil.MayHaveSentPart"/> on a failure to tell whether the server can be
    /// left inside the message.
    /// </summary>
    public static async ValueTask SendAsync(HislipChannel channel, HislipMessageType type, byte control, uint parameter,
        ReadOnlyMemory<byte> payload, Deadline deadline, bool async)
    {
        if (payload.Length <= 4096)
        {
            // Header and payload in one send.
            byte[] buffer = ArrayPool<byte>.Shared.Rent(HeaderSize + payload.Length);
            try
            {
                WriteHeader(buffer, type, control, parameter, (ulong)payload.Length);
                payload.Span.CopyTo(buffer.AsSpan(HeaderSize));
                await channel.SendAsync(buffer.AsMemory(0, HeaderSize + payload.Length), deadline, async).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        else
        {
            byte[] header = new byte[HeaderSize];
            WriteHeader(header, type, control, parameter, (ulong)payload.Length);
            await channel.SendAsync(header, deadline, async).ConfigureAwait(false);
            try
            {
                await channel.SendAsync(payload, deadline, async).ConfigureAwait(false);
            }
            catch (LanTimeoutException ex)
            {
                // The header is out: whatever happened to the payload, the server is inside this message.
                throw NetUtil.PartialSendTimeout("The send timed out after the message header.", ex, HeaderSize + ex.BytesTransferred);
            }
        }
    }

    /// <summary>
    /// Reads the payload of a message other than Data/DataEND. Larger payloads than <see cref="MaxControlPayload"/> are
    /// a protocol error, or with <paramref name="skipIfTooLarge"/> are skipped (an empty payload is returned), which
    /// keeps the stream in step.
    /// </summary>
    public static async ValueTask<byte[]> ReceivePayloadAsync(HislipChannel channel, HislipHeader header, Deadline deadline, bool async,
        bool skipIfTooLarge = false)
    {
        if (header.PayloadLength > MaxControlPayload)
        {
            if (!skipIfTooLarge)
                throw LanErrors.Protocol($"HiSLIP protocol error: unexpected payload of {header.PayloadLength} bytes in {header.Type}.");
            await channel.SkipAsync((long)Math.Min(header.PayloadLength, long.MaxValue), deadline, async).ConfigureAwait(false);
            return [];
        }
        var payload = new byte[(int)header.PayloadLength];
        await channel.ReceiveExactAsync(payload, deadline, async).ConfigureAwait(false);
        return payload;
    }

    /// <summary>Reads one complete message other than Data/DataEND. <paramref name="headerBuffer"/> may be reused.</summary>
    public static async ValueTask<HislipMessage> ReceiveAsync(HislipChannel channel, Deadline deadline, bool async, byte[]? headerBuffer = null)
    {
        var buffer = headerBuffer ?? new byte[HeaderSize];
        await channel.ReceiveExactAsync(buffer.AsMemory(0, HeaderSize), deadline, async).ConfigureAwait(false);
        var h = ParseHeader(buffer);
        return new HislipMessage(h, await ReceivePayloadAsync(channel, h, deadline, async).ConfigureAwait(false));
    }

    public static List<HislipDescriptor> ParseDescriptors(ReadOnlySpan<byte> payload)
    {
        var result = new List<HislipDescriptor>();
        int offset = 0;
        while (offset + 3 <= payload.Length)
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
            byte type = payload[offset + 2];
            if (offset + 3 + length > payload.Length) break;
            result.Add(new HislipDescriptor((HislipDescriptorType)type, payload.Slice(offset + 3, length).ToArray()));
            offset += 3 + length;
        }
        return result;
    }

    public static string DescribeFatalError(byte code) => code switch
    {
        0 => "unidentified error",
        1 => "poorly formed message header",
        2 => "attempt to use connection without both channels established",
        3 => "invalid initialization sequence",
        4 => "server refused connection due to maximum number of clients exceeded",
        5 => "secure connection failed",
        _ => $"device defined error {code}",
    };

    public static string DescribeError(byte code) => code switch
    {
        0 => "unidentified error",
        1 => "unrecognized message type",
        2 => "unrecognized control code",
        3 => "unrecognized vendor defined message",
        4 => "message too large",
        5 => "authentication failed",
        _ => $"device defined error {code}",
    };

    /// <summary>The exception for a FatalError message (the server closes the connection after sending one).</summary>
    public static LanException FatalErrorException(HislipMessage message)
    {
        string text = $"HiSLIP fatal error: {DescribeFatalError(message.Control)} {message.PayloadText}".Trim();
        return message.Control == 5
            ? LanErrors.Security(LanSecurityFailure.Policy, text)
            : LanErrors.ConnectionLost(text);
    }

    /// <summary>The exception for an Error message.</summary>
    public static LanException ErrorException(HislipMessage message, string? context = null) =>
        LanErrors.ServerError($"HiSLIP error{(context is null ? "" : " " + context)}: {DescribeError(message.Control)} {message.PayloadText}".Trim());
}

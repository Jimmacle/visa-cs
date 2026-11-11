using VisaCs.Internal;

namespace VisaCs.Lan.Rpc;

/// <summary>The accept status of an ONC RPC reply (RFC 5531 <c>accept_stat</c>).</summary>
public enum RpcAcceptStatus : uint
{
    /// <summary>The call succeeded; the results follow.</summary>
    Success = 0,
    /// <summary>The server does not export the program.</summary>
    ProgramUnavailable = 1,
    /// <summary>The server does not support the program version.</summary>
    ProgramMismatch = 2,
    /// <summary>The program has no such procedure.</summary>
    ProcedureUnavailable = 3,
    /// <summary>The server could not decode the arguments.</summary>
    GarbageArguments = 4,
    /// <summary>Another error on the server, such as a memory allocation failure.</summary>
    SystemError = 5,
}

/// <summary>ONC RPC v2 (RFC 5531) constants and message headers, for code that builds its own RPC messages (UDP, for example).</summary>
public static class OncRpc
{
    /// <summary>The RPC protocol version (2).</summary>
    public const uint RpcVersion = 2;

    /// <summary>Message type of a call.</summary>
    public const uint CallMessage = 0;

    /// <summary>Message type of a reply.</summary>
    public const uint ReplyMessage = 1;

    internal const uint MsgAccepted = 0;
    internal const uint MsgDenied = 1;
    internal const uint AuthNone = 0;
    internal const uint LastFragment = 0x80000000;
    internal const int MaxRecordSize = 256 * 1024 * 1024;

    /// <summary>Writes a call header with AUTH_NONE credentials; the procedure's arguments follow.</summary>
    public static void WriteCallHeader(XdrWriter writer, uint xid, uint program, uint version, uint procedure)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt32(xid);
        writer.WriteUInt32(CallMessage);
        writer.WriteUInt32(RpcVersion);
        writer.WriteUInt32(program);
        writer.WriteUInt32(version);
        writer.WriteUInt32(procedure);
        writer.WriteUInt32(AuthNone); // credentials
        writer.WriteUInt32(0);
        writer.WriteUInt32(AuthNone); // verifier
        writer.WriteUInt32(0);
    }

    /// <summary>Writes the header of an accepted reply; on <see cref="RpcAcceptStatus.Success"/> the results follow.</summary>
    public static void WriteAcceptedReplyHeader(XdrWriter writer, uint xid, RpcAcceptStatus status)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt32(xid);
        writer.WriteUInt32(ReplyMessage);
        writer.WriteUInt32(MsgAccepted);
        writer.WriteUInt32(AuthNone);
        writer.WriteUInt32(0);
        writer.WriteUInt32((uint)status);
    }

    /// <summary>
    /// Parses a reply. Returns null if the xid does not match (a stale reply), otherwise a reader positioned
    /// at the procedure results. Throws on RPC-level errors.
    /// </summary>
    internal static XdrReader? ParseReply(byte[] message, int length, uint expectedXid)
    {
        var r = new XdrReader(message, 0, length);
        uint xid = r.ReadUInt32();
        if (xid != expectedXid) return null;
        if (r.ReadUInt32() != ReplyMessage)
            throw LanErrors.IO("RPC: expected a reply message.");
        uint replyStatus = r.ReadUInt32();
        if (replyStatus == MsgDenied)
        {
            uint rejectStatus = r.ReadUInt32();
            throw LanErrors.IO(rejectStatus == 0 ? "RPC call rejected: RPC version mismatch." : "RPC call rejected: authentication error.");
        }
        r.ReadUInt32();     // verifier flavor
        r.ReadOpaqueSpan(); // verifier body
        var acceptStatus = (RpcAcceptStatus)r.ReadUInt32();
        return acceptStatus switch
        {
            RpcAcceptStatus.Success => r,
            RpcAcceptStatus.ProgramUnavailable => throw LanErrors.NotFound("RPC program unavailable."),
            RpcAcceptStatus.ProgramMismatch => throw LanErrors.NotFound("RPC program version mismatch."),
            RpcAcceptStatus.ProcedureUnavailable => throw LanErrors.NotSupported("RPC procedure unavailable."),
            RpcAcceptStatus.GarbageArguments => throw LanErrors.InvalidParameter("RPC server could not decode the arguments."),
            _ => throw LanErrors.IO($"RPC system error (accept status {(uint)acceptStatus})."),
        };
    }

    private static int _xidSeed = Environment.TickCount;

    internal static uint NewXidBase() => unchecked((uint)Interlocked.Add(ref _xidSeed, 0x10000));
}

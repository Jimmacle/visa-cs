using VisaCs.Internal;

namespace VisaCs.Lan.Rpc;

/// <summary>Portmapper (rpcbind version 2) client: finds the TCP port of an RPC program.</summary>
public static class Portmapper
{
    /// <summary>The well-known portmapper port (111).</summary>
    public const int DefaultPort = 111;

    /// <summary>The portmapper's RPC program number (100000).</summary>
    public const uint Program = 100000;

    /// <summary>The portmapper version this client speaks (2).</summary>
    public const uint Version = 2;

    /// <summary>The GETPORT procedure.</summary>
    public const uint GetPortProcedure = 3;

    /// <summary>Protocol number for TCP in a GETPORT request (6).</summary>
    public const uint ProtocolTcp = 6;

    /// <summary>
    /// Asks the portmapper of <paramref name="host"/> for the TCP port of <paramref name="program"/>. Throws
    /// <see cref="VisaException"/> (VI_ERROR_RSRC_NFOUND) when the program is not registered.
    /// </summary>
    public static int GetPort(string host, uint program, uint version, int timeoutMilliseconds, int portmapperPort = DefaultPort) =>
        AsyncIO.Complete(GetPortAsync(host, program, version, portmapperPort, Deadline.FromMilliseconds(timeoutMilliseconds), async: false));

    /// <summary>Asks the portmapper of <paramref name="host"/> for the TCP port of a program; see <see cref="GetPort(string, uint, uint, int, int)"/>.</summary>
    public static ValueTask<int> GetPortAsync(string host, uint program, uint version, int timeoutMilliseconds, int portmapperPort = DefaultPort,
        CancellationToken cancellationToken = default) =>
        LanCall.RunAsync(GetPortAsync(host, program, version, portmapperPort, LanCall.DeadlineFor(timeoutMilliseconds, cancellationToken), async: true),
            cancellationToken);

    private static async ValueTask<int> GetPortAsync(string host, uint program, uint version, int portmapperPort, Deadline deadline, bool async)
    {
        using var portmapper = await ConnectAsync(host, portmapperPort, deadline, async).ConfigureAwait(false);
        return await GetPortAsync(portmapper, host, program, version, deadline, async).ConfigureAwait(false);
    }

    /// <summary>Opens a connection to the portmapper of <paramref name="host"/>.</summary>
    internal static ValueTask<RpcTcpClient> ConnectAsync(string host, int portmapperPort, Deadline deadline, bool async) =>
        RpcTcpClient.ConnectAsync(host, portmapperPort, Program, Version, deadline, async);

    /// <summary>Looks up the TCP port of an RPC program through an open portmapper connection.</summary>
    internal static async ValueTask<int> GetPortAsync(RpcTcpClient portmapper, string host, uint program, uint version, Deadline deadline, bool async)
    {
        var reader = await portmapper.CallAsync(GetPortProcedure, w =>
        {
            w.WriteUInt32(program);
            w.WriteUInt32(version);
            w.WriteUInt32(ProtocolTcp);
            w.WriteUInt32(0);
        }, deadline, async).ConfigureAwait(false);
        uint port = reader.ReadUInt32();
        if (port == 0 || port > 65535)
            throw LanErrors.NotFound($"RPC program 0x{program:X} is not registered on {host}.");
        return (int)port;
    }

    /// <summary>Builds a GETPORT call for UDP broadcast discovery.</summary>
    internal static byte[] BuildGetPortDatagram(uint xid, uint program, uint version)
    {
        var w = new XdrWriter(128);
        OncRpc.WriteCallHeader(w, xid, Program, Version, GetPortProcedure);
        w.WriteUInt32(program);
        w.WriteUInt32(version);
        w.WriteUInt32(ProtocolTcp);
        w.WriteUInt32(0);
        return w.WrittenSpan.ToArray();
    }
}

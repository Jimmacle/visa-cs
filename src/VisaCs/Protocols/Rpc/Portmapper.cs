using VisaCs.Internal;

namespace VisaCs.Protocols.Rpc;

/// <summary>Portmapper (rpcbind v2) client.</summary>
internal static class Portmapper
{
    /// <summary>Opens a connection to the portmapper of <paramref name="host"/>.</summary>
    public static ValueTask<RpcTcpClient> ConnectAsync(string host, int portmapperPort, Deadline deadline, bool async) =>
        RpcTcpClient.ConnectAsync(host, portmapperPort, OncRpc.PortmapperProgram, OncRpc.PortmapperVersion, deadline, async);

    /// <summary>Looks up the TCP port of an RPC program through an open portmapper connection.</summary>
    public static async ValueTask<int> GetPortAsync(RpcTcpClient portmapper, string host, uint program, uint version, Deadline deadline, bool async)
    {
        var reader = await portmapper.CallAsync(OncRpc.PortmapperProcGetPort, w =>
        {
            w.WriteUInt32(program);
            w.WriteUInt32(version);
            w.WriteUInt32(OncRpc.ProtocolTcp);
            w.WriteUInt32(0);
        }, deadline, async).ConfigureAwait(false);
        uint port = reader.ReadUInt32();
        if (port == 0 || port > 65535)
            throw new VisaException(VisaStatus.ErrorResourceNotFound, $"RPC program 0x{program:X} is not registered on {host}.");
        return (int)port;
    }

    /// <summary>Builds a GETPORT call for UDP broadcast discovery.</summary>
    public static byte[] BuildGetPortDatagram(uint xid, uint program, uint version)
    {
        var w = new XdrWriter(128);
        OncRpc.WriteCallHeader(w, xid, OncRpc.PortmapperProgram, OncRpc.PortmapperVersion, OncRpc.PortmapperProcGetPort);
        w.WriteUInt32(program);
        w.WriteUInt32(version);
        w.WriteUInt32(OncRpc.ProtocolTcp);
        w.WriteUInt32(0);
        return w.WrittenSpan.ToArray();
    }
}

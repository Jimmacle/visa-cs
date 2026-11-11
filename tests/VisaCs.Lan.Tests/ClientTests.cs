using System.Net;
using System.Text;
using VisaCs.Lan.Rpc;
using VisaCs.Simulation;

namespace VisaCs.Lan.Tests;

public sealed class SimulatorFixture : IDisposable
{
    public InstrumentSimulator Simulator { get; } = new InstrumentSimulator().Start();

    public void Dispose() => Simulator.Dispose();
}

/// <summary>The public LAN clients, used without VISA, against the simulated instrument.</summary>
public sealed class ClientTests(SimulatorFixture fixture) : IClassFixture<SimulatorFixture>
{
    private const int Timeout = 10_000;

    private string Identity => fixture.Simulator.Instrument.Identity;

    // ------------------------------------------------------------------ VXI-11

    [Fact]
    public void Vxi11QueriesTheInstrument()
    {
        using var client = Vxi11Client.Connect("127.0.0.1", "inst0", Timeout, fixture.Simulator.Vxi11.PortmapperPort);
        Assert.True(client.MaxReceiveSize > 0);
        var write = client.Write("*IDN?\n"u8.ToArray(), end: true, Timeout);
        Assert.Equal(new Vxi11WriteResult(Vxi11Error.None, 6), write);
        var read = client.Read(1024, (byte)'\n', Timeout);
        Assert.Equal(Vxi11Error.None, read.Error);
        Assert.True(read.Reason.HasFlag(Vxi11ReadReasons.End) || read.Reason.HasFlag(Vxi11ReadReasons.TerminationCharacter));
        Assert.Equal(Identity + "\n", Encoding.ASCII.GetString(read.Data.Span));
        Assert.Equal(Vxi11Error.None, client.ReadStatusByte(Timeout).Error);
        Assert.Equal(Vxi11Error.None, client.Clear(Timeout));
        Assert.Equal(Vxi11Error.None, client.Trigger(Timeout));
        Assert.Equal(Vxi11Error.None, client.Lock(Timeout));
        Assert.Equal(Vxi11Error.None, client.Unlock(Timeout));
        Assert.Equal(Vxi11Error.NoLockHeld, client.Unlock(Timeout));
        client.DestroyLink(Timeout);
    }

    [Fact]
    public async Task Vxi11CancellationAbortsTheReadAndKeepsTheLink()
    {
        using var client = await Vxi11Client.ConnectAsync("127.0.0.1", "inst0", Timeout, fixture.Simulator.Vxi11.PortmapperPort);
        Assert.NotEqual(0, client.AbortPort);
        // Nothing to read: the device waits until the abort arrives.
        using var cts = new CancellationTokenSource(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await client.ReadAsync(1024, null, 30_000, cts.Token));
        await client.WriteAsync("*IDN?\n"u8.ToArray(), true, Timeout);
        // The aborted read's late reply is discarded; this read gets the answer.
        var read = await client.ReadAsync(1024, null, Timeout);
        Assert.Equal(Vxi11Error.None, read.Error);
        Assert.Equal(Identity + "\n", Encoding.ASCII.GetString(read.Data.Span));
    }

    [Fact]
    public void Vxi11ErrorsBecomeLanExceptions()
    {
        var timeout = Assert.IsType<LanTimeoutException>(Vxi11Errors.ToException(Vxi11Error.IOTimeout, "device_read"));
        Assert.Equal(Vxi11Error.IOTimeout, Assert.IsType<Vxi11Exception>(timeout.InnerException).Code);
        var locked = Assert.IsType<Vxi11Exception>(Vxi11Errors.ToException(Vxi11Error.DeviceLocked, "device_write"));
        Assert.Equal(Vxi11Error.DeviceLocked, locked.Code);
        Assert.Equal(LanError.Locked, locked.Error);
        Assert.IsAssignableFrom<IOException>(locked);
        Assert.Contains("device locked by another link", locked.Message);
        var ex = Assert.Throws<Vxi11Exception>(() =>
            Vxi11Client.Connect("127.0.0.1", "nosuchdevice", Timeout, fixture.Simulator.Vxi11.PortmapperPort));
        Assert.Equal(LanError.DeviceNotFound, ex.Error);
    }

    [Fact]
    public void ConnectFailuresNameTheCause()
    {
        // Nothing listens on port 1 of the loopback address.
        var refused = Assert.Throws<LanException>(() => VicpClient.Connect("127.0.0.1", Timeout, port: 1));
        Assert.Equal(LanError.ConnectionRefused, refused.Error);
        Assert.NotNull(refused.NativeError);
    }

    [Fact]
    public void ZeroTimeoutDoesNotWait()
    {
        using var client = HislipClient.Connect("127.0.0.1", fixture.Simulator.Hislip.Port, "hislip0", Timeout);
        // Nothing will arrive, so waiting would never end; the bound is generous for a loaded machine.
        var started = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<LanTimeoutException>(() => client.Read(new byte[16], 0));
        Assert.Equal(0, ex.BytesTransferred);
        Assert.True(started.ElapsedMilliseconds < 5000, $"took {started.ElapsedMilliseconds} ms");
    }

    // ------------------------------------------------------------------ HiSLIP

    [Fact]
    public async Task HislipQueriesTheInstrument()
    {
        using var client = await HislipClient.ConnectAsync("127.0.0.1", fixture.Simulator.Hislip.Port, "hislip0", Timeout);
        Assert.Equal(0x0200, client.ProtocolVersion);
        Assert.Equal(IPAddress.Loopback, client.RemoteAddress);
        await client.WriteAsync("*IDN?\n"u8.ToArray(), end: true, Timeout);
        var buffer = new byte[1024];
        int total = 0;
        while (true)
        {
            var (count, end) = await client.ReadAsync(buffer.AsMemory(total), Timeout);
            total += count;
            if (end) break;
        }
        client.MarkResponseDelivered();
        Assert.Equal(Identity + "\n", Encoding.ASCII.GetString(buffer, 0, total));
        await client.ReadStatusByteAsync(Timeout);
        client.Lock(null, Timeout);
        Assert.Equal(new HislipLockInfo(true, 1), client.GetLockInfo(Timeout));
        client.Unlock(Timeout);
        Assert.False(client.GetLockInfo(Timeout).ExclusiveLockGranted);
        client.DeviceClear(Timeout);
        client.RemoteLocal(HislipRemoteLocalMode.EnableRemoteGoToRemote, Timeout);
        Assert.NotEmpty(client.GetDescriptors(Timeout));
    }

    [Fact]
    public void HislipReadTimesOutAndCancels()
    {
        using var client = HislipClient.Connect("127.0.0.1", fixture.Simulator.Hislip.Port, "hislip0", Timeout);
        var buffer = new byte[16];
        Assert.Throws<LanTimeoutException>(() => client.Read(buffer, 100));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => client.ReadAsync(buffer, Timeout, cts.Token).AsTask().GetAwaiter().GetResult());
    }

    // ------------------------------------------------------------------ VICP

    [Fact]
    public void VicpQueriesTheInstrument()
    {
        using var client = VicpClient.Connect("127.0.0.1", Timeout, fixture.Simulator.Vicp.Port);
        client.Write("*IDN?\n"u8.ToArray(), end: true, Timeout);
        var buffer = new byte[1024];
        int total = 0;
        while (true)
        {
            var (count, end) = client.Read(buffer.AsMemory(total), Timeout);
            total += count;
            if (end) break;
        }
        Assert.Equal(Identity + "\n", Encoding.ASCII.GetString(buffer, 0, total));
        client.ReadStatusByte(Timeout);
        client.SetRemote(true, false, Timeout);
        client.DeviceClear(Timeout);
        client.Write("*IDN?\n"u8.ToArray(), end: true, Timeout);
        Assert.True(client.Read(buffer, Timeout).Count > 0);
    }

    // ------------------------------------------------------------------ ONC RPC and discovery

    [Fact]
    public async Task RpcServerAndClientRoundTrip()
    {
        const uint program = 0x20000099;
        using var server = new RpcTcpServer(IPAddress.Loopback, 0, program, 1, (context, args, results) =>
        {
            if (context.Procedure != 1) return RpcAcceptStatus.ProcedureUnavailable;
            results.WriteString(args.ReadString().ToUpperInvariant());
            return RpcAcceptStatus.Success;
        });
        server.Start();
        using var client = await RpcTcpClient.ConnectAsync(server.LocalEndPoint, program, 1, Timeout);
        var reply = await client.CallAsync(1, w => w.WriteString("hello"), Timeout);
        Assert.Equal("HELLO", reply.ReadString());
        Assert.Equal(0, client.Call(0, null, Timeout).Remaining); // the null procedure
        var unavailable = Assert.Throws<RpcException>(() => client.Call(7, null, Timeout));
        Assert.Equal(RpcAcceptStatus.ProcedureUnavailable, unavailable.AcceptStatus);
        Assert.Equal(LanError.NotSupported, unavailable.Error);
        var garbage = Assert.Throws<RpcException>(() => client.Call(1, null, Timeout));
        Assert.Equal(RpcAcceptStatus.GarbageArguments, garbage.AcceptStatus);
        Assert.Equal(LanError.InvalidRequest, garbage.Error);
    }

    [Fact]
    public void PortmapperFindsTheVxi11CoreChannel()
    {
        int port = Portmapper.GetPort("127.0.0.1", Vxi11Protocol.CoreProgram, Vxi11Protocol.Version, Timeout, fixture.Simulator.Vxi11.PortmapperPort);
        Assert.Equal(fixture.Simulator.Vxi11.CorePort, port);
        Assert.Equal(LanError.DeviceNotFound, Assert.Throws<LanException>(() =>
            Portmapper.GetPort("127.0.0.1", 0x20000098, 1, Timeout, fixture.Simulator.Vxi11.PortmapperPort)).Error);
    }

    [Fact]
    public async Task Vxi11DiscoveryFindsTheSimulator()
    {
        var found = await Vxi11Discovery.DiscoverAsync(500, fixture.Simulator.Vxi11.PortmapperPort, [IPAddress.Loopback], broadcast: false);
        Assert.Contains(IPAddress.Loopback, found);
    }

    [Fact]
    public void MdnsBrowsesAResponder()
    {
        using var responder = new MdnsTestResponder();
        responder.Services.Add(("_vxi-11._tcp", "Sim VXI-11", "sim-host", 111, IPAddress.Parse("192.0.2.10"), new()));
        responder.Start();
        var instance = Assert.Single(MdnsDiscovery.Browse([MdnsServiceTypes.Vxi11], 500, responder.EndPoint));
        Assert.Equal("Sim VXI-11", instance.InstanceName);
        Assert.Equal(MdnsServiceTypes.Vxi11, instance.ServiceType);
        Assert.Equal(111, instance.Port);
    }

    // ------------------------------------------------------------------ Discovery timeouts

    [Fact]
    public void SynchronousDiscoveryRejectsANegativeTimeout()
    {
        // The timeout is a listening time; with no token, "no limit" would never return.
        Assert.Throws<ArgumentOutOfRangeException>(() => Vxi11Discovery.Discover(-1, targets: [IPAddress.Loopback], broadcast: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => MdnsDiscovery.Browse([MdnsServiceTypes.Vxi11], -1, new IPEndPoint(IPAddress.Loopback, 9)));
    }

    [Fact]
    public void AsynchronousDiscoveryRejectsANegativeTimeoutWithoutACancellableToken()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = Vxi11Discovery.DiscoverAsync(-1, targets: [IPAddress.Loopback], broadcast: false); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = MdnsDiscovery.BrowseAsync([MdnsServiceTypes.Vxi11], -1, new IPEndPoint(IPAddress.Loopback, 9)); });
    }

    [Fact]
    public async Task AsynchronousDiscoveryWithANegativeTimeoutListensUntilCancelled()
    {
        using var cts = new CancellationTokenSource(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Vxi11Discovery.DiscoverAsync(-1, fixture.Simulator.Vxi11.PortmapperPort, [IPAddress.Loopback], broadcast: false, cts.Token));
        using var cts2 = new CancellationTokenSource(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MdnsDiscovery.BrowseAsync([MdnsServiceTypes.Vxi11], -1, new IPEndPoint(IPAddress.Loopback, 9), cts2.Token));
    }

    [Fact]
    public void DiscoveryWithZeroTimeoutReturnsImmediately()
    {
        Assert.Empty(MdnsDiscovery.Browse([MdnsServiceTypes.Vxi11], 0, new IPEndPoint(IPAddress.Loopback, 9)));
        _ = Vxi11Discovery.Discover(0, targets: [IPAddress.Loopback], broadcast: false);
    }
}

using System.Text;
using VisaCs.Simulation;

namespace VisaCs.Tests;

/// <summary>Starts one simulator (all LAN protocols) shared by a test class.</summary>
public sealed class SimulatorFixture : IDisposable
{
    public SimulatorFixture()
    {
        Simulator = new InstrumentSimulator().Start();
    }

    public InstrumentSimulator Simulator { get; }

    public SimulatedInstrument Instrument => Simulator.Instrument;

    public ResourceManager CreateResourceManager() => new(Simulator.CreateOptions());

    public void Dispose() => Simulator.Dispose();
}

/// <summary>
/// Test classes that open VXI-11 or VICP resources. Those names carry no port, so the simulators of different
/// classes share one name and therefore one entry in the process-wide lock table. Running these classes one at
/// a time keeps an exclusive lock taken by one class from failing another class's I/O.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PortlessLanResourceCollection
{
    public const string Name = "Port-less LAN resource names";
}

/// <summary>Tests every message-based protocol must pass. Derived classes provide the resource.</summary>
public abstract partial class MessageBasedProtocolTests
{
    protected abstract ResourceManager CreateResourceManager();

    protected abstract string Resource { get; }

    protected abstract SimulatedInstrument Instrument { get; }

    protected virtual bool HasEndIndicator => true;

    protected virtual bool SupportsServiceRequest => true;

    /// <summary>False when the environment for a derived class is not available (the tests then do nothing).</summary>
    protected virtual bool Enabled => true;

    /// <summary>
    /// True when cancelling an async operation interrupts a pending read. USB and GPIB transfers block in the
    /// driver, so there cancellation only takes effect between transfers.
    /// </summary>
    protected virtual bool CancelsPromptly => true;

    protected MessageBasedSession Open(ResourceManager rm)
    {
        var session = rm.Open(Resource);
        session.TimeoutMilliseconds = 5000;
        return session;
    }

    [Fact]
    public void QueriesIdentity()
    {
        if (!Enabled) return;
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        Assert.Equal(Instrument.Identity, session.Query("*IDN?"));
        Assert.Equal(Instrument.Identity, session.Query("*IDN?"));
        Assert.Equal("hello world", session.Query("ECHO? hello world"));
    }

    [Fact]
    public void ParsesNumbers()
    {
        if (!Enabled) return;
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        Assert.Equal(Instrument.Voltage, session.Query<double>("MEAS:VOLT?"), 6);
        Assert.Equal(1, session.Query<long>("*OPC?"));
        var values = session.QueryAsciiValues("DATA:ASCII? 50");
        Assert.Equal(50, values.Length);
        Assert.Equal(24.5, values[49]);
    }

    [Fact]
    public void ReadsDefiniteBinaryBlocks()
    {
        if (!Enabled) return;
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        foreach (int size in new[] { 0, 1, 10, 9999, 250_000 })
        {
            var block = session.QueryBinaryBlock($"DATA:BIN? {size}");
            Assert.Equal(size, block.Length);
            for (int i = 0; i < block.Length; i++)
                if (block[i] != (byte)i) Assert.Fail($"Mismatch at {i} for size {size}");
        }
        // The session must be in sync afterwards.
        Assert.Equal(Instrument.Identity, session.Query("*IDN?"));
    }

    [Fact]
    public void ReadsBinaryValuesWithTermCharEnabled()
    {
        if (!Enabled) return;
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        session.TerminationCharacterEnabled = true; // data contains 0x0A bytes; must be ignored inside the block
        var values = session.QueryBinaryValues<float>("DATA:FLOAT? 2000");
        Assert.Equal(2000, values.Length);
        Assert.Equal(999.5f, values[1999]);
        Assert.Equal(Instrument.Identity, session.Query("*IDN?"));
    }

    [Fact]
    public void ReadsIndefiniteBlock()
    {
        if (!Enabled) return;
        if (!HasEndIndicator) return;
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        var block = session.QueryBinaryBlock("DATA:BIN:INDEF? 30");
        Assert.Equal("abcdefghijklmnopqrstuvwxyzabcd", Encoding.ASCII.GetString(block));
    }

    [Fact]
    public void WritesBinaryBlocks()
    {
        if (!Enabled) return;
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        var data = new byte[70_000];
        new Random(42).NextBytes(data);
        data[10] = (byte)'\n';
        session.WriteBinaryBlock("DATA:STORE ", data);
        Assert.Equal(data, session.QueryBinaryBlock("DATA:STORE?"));

        session.BinaryByteOrder = ByteOrder.BigEndian;
        session.WriteBinaryValues<double>("DATA:STORE ", new[] { 1.5, -2.25 });
        Assert.Equal(new[] { 1.5, -2.25 }, session.QueryBinaryValues<double>("DATA:STORE?"));
    }

    [Fact]
    public void ReadsLargeTextResponses()
    {
        if (!Enabled) return;
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        var text = session.Query("DATA:LARGE? 600000");
        Assert.Equal(600_000, text.Length);
        Assert.All(text, c => Assert.Equal('A', c));
    }

    [Fact]
    public void ReadsInPieces()
    {
        if (!Enabled) return;
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        session.WriteLine("ECHO? 0123456789");
        var first = new byte[4];
        var result = session.Read(first);
        Assert.Equal("0123", Encoding.ASCII.GetString(first));
        Assert.Equal(new ReadResult(4, ReadStatus.MaximumCountReached), result);
        var rest = session.ReadToEnd();
        Assert.Equal("456789\n", Encoding.ASCII.GetString(rest));
    }

    [Fact]
    public void StopsAtTerminationCharacter()
    {
        if (!Enabled) return;
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        session.TerminationCharacter = (byte)',';
        session.TerminationCharacterEnabled = true;
        session.WriteLine("ECHO? a,b,c");
        var buffer = new byte[100];
        var result = session.Read(buffer);
        Assert.Equal(ReadStatus.TerminationCharacterEncountered, result.Status);
        Assert.Equal("a,", Encoding.ASCII.GetString(buffer, 0, result.Count));
        result = session.Read(buffer);
        Assert.Equal(ReadStatus.TerminationCharacterEncountered, result.Status);
        Assert.Equal("b,", Encoding.ASCII.GetString(buffer, 0, result.Count));
        session.TerminationCharacter = (byte)'\n';
        Assert.Equal("c", session.ReadLine());
    }

    [Fact]
    public void ReadsStatusByte()
    {
        if (!Enabled) return;
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        session.WriteLine("*CLS;*SRE 0");
        Assert.Equal("1", session.Query("*OPC?"));
        Assert.Equal(0, session.ReadStatusByte() & 0x41);
        session.WriteLine("*SRE 1;SRQ");
        Assert.Equal("1", session.Query("*OPC?"));
        byte stb = session.ReadStatusByte();
        Assert.Equal(0x41, stb & 0x41);
        session.WriteLine("*CLS;*SRE 0");
        Assert.Equal("1", session.Query("*OPC?"));
        Assert.Equal(0, session.ReadStatusByte() & 0x41);
    }

    [Fact]
    public void AssertsTrigger()
    {
        if (!Enabled) return;
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        int before = Instrument.TriggerCount;
        session.AssertTrigger();
        Assert.Equal("1", session.Query("*OPC?"));
        Assert.True(SpinWait.SpinUntil(() => Instrument.TriggerCount > before, 2000));
    }

    [Fact]
    public void ClearsDevice()
    {
        if (!Enabled) return;
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        int clears = Instrument.ClearCount;
        session.WriteLine("ECHO? never read");
        // Raw sockets have no device clear: Clear only discards what has already arrived.
        if (!HasEndIndicator) Thread.Sleep(300);
        session.Clear();
        Assert.Equal(Instrument.Identity, session.Query("*IDN?"));
        if (HasEndIndicator) Assert.True(Instrument.ClearCount > clears || this is VicpTests);
    }

    [Fact]
    public virtual void TimesOutAndRecovers()
    {
        if (!Enabled) return;
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        session.TimeoutMilliseconds = 300;
        var ex = Assert.Throws<VisaTimeoutException>(() => session.Query("DELAY? 1200"));
        Assert.Equal(VisaStatus.ErrorTimeout, ex.Status);
        session.TimeoutMilliseconds = 5000;
        session.Clear();
        Assert.Equal(Instrument.Identity, session.Query("*IDN?"));
    }

    [Fact]
    public void RejectsUnsupportedIOProtocols()
    {
        if (!Enabled) return;
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        foreach (var protocol in new[] { IOProtocol.Fdc, IOProtocol.HS488 })
        {
            var ex = Assert.Throws<VisaException>(() => session.SetAttribute(VisaAttribute.IOProtocol, protocol));
            Assert.Equal(VisaStatus.ErrorAttributeStateNotSupported, ex.Status);
        }
        Assert.Equal(IOProtocol.Normal, session.IOProtocol);
        Assert.Equal(Instrument.Identity, session.Query("*IDN?"));
    }

    [Fact]
    public async Task CancellationEndsPendingQuery()
    {
        if (!Enabled || !CancelsPromptly) return;
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        session.TimeoutMilliseconds = 10_000;
        using var cts = new CancellationTokenSource(200);
        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.QueryAsync("DELAY? 1500", cts.Token));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1.2), "Cancellation did not interrupt the read.");

        // Let the late response arrive, then discard it as after a timeout.
        await Task.Delay(1600);
        session.Clear();
        session.TimeoutMilliseconds = 5000;
        Assert.Equal(Instrument.Identity, session.Query("*IDN?"));
    }

    [Fact]
    public void QueuesServiceRequests()
    {
        if (!Enabled) return;
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        if (!SupportsServiceRequest)
        {
            var ex = Assert.Throws<VisaException>(() => session.EnableEvent(EventType.ServiceRequest));
            Assert.Equal(VisaStatus.ErrorInvalidEvent, ex.Status);
            return;
        }
        session.WriteLine("*CLS;*SRE 0");
        Assert.Equal("1", session.Query("*OPC?"));
        session.EnableEvent(EventType.ServiceRequest, EventMechanism.Queue);
        try
        {
            session.WriteLine("*SRE 1;SRQ");
            TriggerServiceRequestDelivery(session);
            var e = session.WaitOnEvent(EventType.ServiceRequest, 5000);
            Assert.Equal(EventType.ServiceRequest, e.EventType);
            Assert.Equal(0x40, session.ReadStatusByte() & 0x40);
        }
        finally
        {
            session.DisableEvent(EventType.ServiceRequest);
            session.WriteLine("*CLS;*SRE 0");
            Assert.Equal("1", session.Query("*OPC?"));
        }
        Assert.Throws<VisaException>(() => session.WaitOnEvent(EventType.ServiceRequest, 10));
    }

    [Fact]
    public void DeliversServiceRequestsToHandlers()
    {
        if (!Enabled) return;
        if (!SupportsServiceRequest) return;
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        using var received = new ManualResetEventSlim();
        session.ServiceRequest += (_, _) => received.Set();
        session.WriteLine("*CLS;*SRE 0");
        Assert.Equal("1", session.Query("*OPC?"));
        session.EnableEvent(EventType.ServiceRequest, EventMechanism.Handler);
        try
        {
            session.WriteLine("*SRE 1;SRQ");
            TriggerServiceRequestDelivery(session);
            Assert.True(received.Wait(5000), "No service request handler call.");
        }
        finally
        {
            session.DisableEvent(EventType.AllEnabled);
            session.WriteLine("*CLS;*SRE 0");
            session.Query("*OPC?");
        }
    }

    /// <summary>Protocols that deliver SRQ in-band (VICP) need a read to see it.</summary>
    protected virtual void TriggerServiceRequestDelivery(MessageBasedSession session)
    {
    }

    [Fact]
    public void EnforcesExclusiveLocks()
    {
        if (!Enabled) return;
        using var rm = CreateResourceManager();
        using var a = Open(rm);
        using var b = Open(rm);
        a.LockExclusive(1000);
        Assert.Equal(ResourceLockState.ExclusiveLock, a.ResourceLockState);
        var ex = Assert.Throws<VisaException>(() => b.Query("*IDN?"));
        Assert.Equal(VisaStatus.ErrorResourceLocked, ex.Status);
        Assert.Throws<VisaTimeoutException>(() => b.LockExclusive(100));
        Assert.Equal(Instrument.Identity, a.Query("*IDN?"));

        a.LockExclusive(0); // nested
        a.Unlock();
        Assert.Throws<VisaException>(() => b.Query("*IDN?"));
        a.Unlock();
        Assert.Equal(ResourceLockState.NoLock, a.ResourceLockState);
        Assert.Equal(Instrument.Identity, b.Query("*IDN?"));
        var notLocked = Assert.Throws<VisaException>(a.Unlock);
        Assert.Equal(VisaStatus.ErrorSessionNotLocked, notLocked.Status);
    }

    [Fact]
    public void SharesLocksByKey()
    {
        if (!Enabled) return;
        using var rm = CreateResourceManager();
        using var a = Open(rm);
        using var b = Open(rm);
        using var c = Open(rm);
        string key = a.LockShared(1000);
        Assert.Equal(key, b.LockShared(1000, key));
        Assert.Equal(Instrument.Identity, a.Query("*IDN?"));
        Assert.Equal(Instrument.Identity, b.Query("*IDN?"));
        Assert.Equal(VisaStatus.ErrorResourceLocked, Assert.Throws<VisaException>(() => c.Query("*IDN?")).Status);
        Assert.Throws<VisaTimeoutException>(() => c.LockShared(100, "other-key"));
        a.Unlock();
        b.Unlock();
        Assert.Equal(Instrument.Identity, c.Query("*IDN?"));
    }

    [Fact]
    public void ClosingReleasesLocks()
    {
        if (!Enabled) return;
        using var rm = CreateResourceManager();
        using var b = Open(rm);
        var a = Open(rm);
        a.LockExclusive(1000);
        a.Dispose();
        Assert.Equal(Instrument.Identity, b.Query("*IDN?"));
    }

    [Fact]
    public void OpensWithExclusiveLock()
    {
        if (!Enabled) return;
        using var rm = CreateResourceManager();
        using var a = rm.Open(Resource, AccessModes.ExclusiveLock, 1000);
        Assert.Equal(ResourceLockState.ExclusiveLock, a.ResourceLockState);
        var ex = Assert.Throws<VisaTimeoutException>(() => rm.Open(Resource, AccessModes.ExclusiveLock, 100));
        Assert.Equal(VisaStatus.ErrorTimeout, ex.Status);
    }

    [Fact]
    public void ExposesAttributes()
    {
        if (!Enabled) return;
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        Assert.Equal(session.ResourceName, session.GetAttribute<string>(VisaAttribute.ResourceName));
        Assert.Equal((int)session.HardwareInterfaceType, session.GetAttribute<int>(VisaAttribute.InterfaceType));
        session.SetAttribute(VisaAttribute.TimeoutValue, 1234);
        Assert.Equal(1234, session.TimeoutMilliseconds);
        Assert.Equal(1234L, session.GetAttribute<long>(VisaAttribute.TimeoutValue));
        session.SetAttribute(VisaAttribute.TimeoutValue, 0xFFFFFFFFu);
        Assert.Equal(-1, session.TimeoutMilliseconds);
        session.SetAttribute(VisaAttribute.TerminationCharacter, (byte)'\r');
        Assert.Equal((byte)'\r', session.TerminationCharacter);
        session.SetAttribute(VisaAttribute.TerminationCharacterEnabled, true);
        Assert.True(session.GetAttribute<bool>(VisaAttribute.TerminationCharacterEnabled));
        Assert.Equal(VisaStatus.ErrorAttributeReadOnly,
            Assert.Throws<VisaException>(() => session.SetAttribute(VisaAttribute.ResourceName, "x")).Status);
        Assert.Equal(VisaStatus.ErrorAttributeNotSupported,
            Assert.Throws<VisaException>(() => session.GetAttribute(AttributeTestExtensions.UnsupportedAttribute)).Status);
        Assert.Equal(session.Resource.ResourceClass, session.ResourceClass);
        Assert.Equal(session.ResourceClass == ResourceClass.Socket ? "SOCKET" : "INSTR", session.GetAttribute(VisaAttribute.ResourceClass));
    }

    [Fact]
    public async Task SupportsAsyncQueries()
    {
        if (!Enabled) return;
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        Assert.Equal(Instrument.Identity, await session.QueryAsync("*IDN?"));
        var values = await session.QueryBinaryValuesAsync<float>("DATA:FLOAT? 10");
        Assert.Equal(4.5f, values[9]);
    }

    [Fact]
    public void SerializesConcurrentQueries()
    {
        if (!Enabled) return;
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        Parallel.For(0, 40, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            Assert.Equal($"value {i}", session.Query($"ECHO? value {i}"));
        });
    }

    [Fact]
    public void ResourceManagerClosesSessions()
    {
        if (!Enabled) return;
        var rm = CreateResourceManager();
        var session = Open(rm);
        Assert.Contains(session, rm.Sessions);
        rm.Dispose();
        Assert.True(session.IsDisposed);
        Assert.Equal(VisaStatus.ErrorInvalidObject, Assert.Throws<VisaException>(() => session.Query("*IDN?")).Status);
    }
}

internal static class AttributeTestExtensions
{
    /// <summary>An attribute no message-based session here supports (VI_ATTR_VXI_LA).</summary>
    public const VisaAttribute UnsupportedAttribute = (VisaAttribute)0x3FFF00D5;
}

[Collection(PortlessLanResourceCollection.Name)]
public sealed class Vxi11Tests(SimulatorFixture fixture) : MessageBasedProtocolTests, IClassFixture<SimulatorFixture>
{
    protected override ResourceManager CreateResourceManager() => fixture.CreateResourceManager();
    protected override string Resource => fixture.Simulator.Vxi11Resource;
    protected override SimulatedInstrument Instrument => fixture.Instrument;

    [Fact]
    public void OpensTheRightSessionType()
    {
        using var rm = CreateResourceManager();
        using var session = rm.Open<TcpipVxi11Session>(Resource);
        Assert.False(session.IsHislip);
        Assert.Equal("127.0.0.1", session.Address);
        Assert.Equal("inst0", session.DeviceName);
        Assert.True(session.LinkId > 0);
    }

    [Fact]
    public void ReportsUnknownDevices()
    {
        using var rm = CreateResourceManager();
        var ex = Assert.Throws<VisaException>(() => rm.Open("TCPIP::127.0.0.1::bogus0::INSTR"));
        Assert.Equal(VisaStatus.ErrorResourceNotFound, ex.Status);
    }

    [Fact]
    public void ReportsUnreachableHosts()
    {
        var options = fixture.Simulator.CreateOptions();
        options.Vxi11PortmapperPort = 1; // nothing listens there
        options.ConnectTimeoutMilliseconds = 2000;
        using var rm = new ResourceManager(options);
        // Refused connections fail fast on Linux; Windows retries SYNs to closed loopback ports and times out.
        var ex = Assert.ThrowsAny<VisaException>(() => rm.Open(Resource));
        Assert.Contains(ex.Status, new[] { VisaStatus.ErrorResourceNotFound, VisaStatus.ErrorTimeout });
    }

    [Fact]
    public void SplitsWritesAtMaxReceiveSize()
    {
        var instrument = new SimulatedInstrument();
        using var server = new Vxi11InstrumentServer(instrument) { MaxReceiveSize = 1024 };
        server.Start();
        var options = new ResourceManagerOptions { Vxi11PortmapperPort = server.PortmapperPort };
        using var rm = new ResourceManager(options);
        using var session = rm.Open<TcpipVxi11Session>("TCPIP::127.0.0.1::INSTR");
        Assert.Equal(1024, session.MaxReceiveSize);
        var data = Enumerable.Range(0, 10_000).Select(i => (byte)(i * 7)).ToArray();
        session.WriteBinaryBlock("DATA:STORE ", data);
        Assert.Equal(data, session.QueryBinaryBlock("DATA:STORE?"));
    }

    [Fact]
    public async Task CancellationAbortsPendingReads()
    {
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        session.TimeoutMilliseconds = 10_000;
        using var cts = new CancellationTokenSource(300);
        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.QueryAsync("DELAY? 3000", cts.Token));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2.5), "device_abort did not cancel the read.");
        session.Clear();
        Assert.Equal(Instrument.Identity, session.Query("*IDN?"));
    }

    [Fact]
    public void LocksTheDevice()
    {
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        session.LockExclusive(1000);
        // A second VXI-11 link (as from another process) cannot get the device lock.
        using var other = Lan.Vxi11Client.Connect("127.0.0.1", "inst0", 10_000, fixture.Simulator.Vxi11.PortmapperPort, clientId: 99);
        Assert.Equal(Lan.Vxi11Error.DeviceLocked, other.Lock(0));
        session.Unlock();
        Assert.Equal(Lan.Vxi11Error.None, other.Lock(0));
        Assert.Equal(Lan.Vxi11Error.None, other.Unlock(10_000));
    }

    [Fact]
    public void ControlsRemoteLocal()
    {
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        session.ControlRen(RemoteLocalMode.AssertAddress);
        Assert.True(fixture.Simulator.Vxi11.IsRemote);
        session.ControlRen(RemoteLocalMode.DeassertGoToLocal);
        Assert.False(fixture.Simulator.Vxi11.IsRemote);
    }
}

public sealed class HislipTests(SimulatorFixture fixture) : MessageBasedProtocolTests, IClassFixture<SimulatorFixture>
{
    protected override ResourceManager CreateResourceManager() => fixture.CreateResourceManager();
    protected override string Resource => fixture.Simulator.HislipResource;
    protected override SimulatedInstrument Instrument => fixture.Instrument;

    [Fact]
    public void NegotiatesSession()
    {
        using var rm = CreateResourceManager();
        using var session = rm.Open<TcpipHislipSession>(Resource);
        Assert.True(session.IsHislip);
        Assert.Equal(0x0200, session.ProtocolVersion); // client and simulator both speak HiSLIP 2.0
        Assert.True(session.SessionId > 0);
        Assert.Equal("SI", session.ServerVendorId);
        Assert.Equal(fixture.Simulator.Hislip.MaximumMessageSize, session.ServerMaximumMessageSize);
        Assert.Equal(fixture.Simulator.Hislip.Port, session.GetAttribute<int>(VisaAttribute.TcpipPort));
    }

    [Fact]
    public void UsesDefaultPortFromOptions()
    {
        using var rm = CreateResourceManager(); // options.HislipPort = simulator port
        using var session = rm.Open("TCPIP::127.0.0.1::hislip0::INSTR");
        Assert.Equal(Instrument.Identity, session.Query("*IDN?"));
    }

    [Fact]
    public void SplitsMessagesAtServerMaximum()
    {
        var instrument = new SimulatedInstrument();
        using var server = new HislipInstrumentServer(instrument) { MaximumMessageSize = 200 };
        server.Start();
        using var rm = new ResourceManager();
        using var session = rm.Open<TcpipHislipSession>($"TCPIP::127.0.0.1::hislip0,{server.Port}::INSTR");
        session.SetMaximumMessageKB(1); // server responses are split too
        var data = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        session.WriteBinaryBlock("DATA:STORE ", data);
        Assert.Equal(data, session.QueryBinaryBlock("DATA:STORE?"));
        Assert.Equal(3000, session.Query("DATA:LARGE? 3000").Length);
    }

    [Fact]
    public void SwitchesOverlapMode()
    {
        using var rm = CreateResourceManager();
        using var session = rm.Open<TcpipHislipSession>(Resource);
        Assert.False(session.OverlapEnabled);
        session.SetOverlapEnabled(true);
        Assert.True(session.OverlapEnabled);
        Assert.Equal(Instrument.Identity, session.Query("*IDN?"));
        session.SetAttribute(VisaAttribute.TcpipHislipOverlapEnabled, false);
        Assert.False(session.OverlapEnabled);
        Assert.Equal(Instrument.Identity, session.Query("*IDN?"));
    }

    [Fact]
    public void DiscardsStaleResponsesInSynchronizedMode()
    {
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        // The first response is never read; in synchronized mode the next message makes it stale.
        session.WriteLine("ECHO? stale");
        Thread.Sleep(100);
        Assert.Equal("fresh", session.Query("ECHO? fresh"));
        session.WriteLine("ECHO? stale again");
        Assert.Equal("fresh again", session.Query("ECHO? fresh again"));
    }

    [Fact]
    public void ReportsRmtDelivered()
    {
        int before = fixture.Simulator.Hislip.RmtDeliveredCount;
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        session.Query("*IDN?");
        session.Query("*IDN?");
        Assert.True(SpinWait.SpinUntil(() => fixture.Simulator.Hislip.RmtDeliveredCount > before, 2000));
    }

    [Fact]
    public void ReportsServerLocks()
    {
        using var rm = CreateResourceManager();
        using var session = rm.Open<TcpipHislipSession>(Resource);
        Assert.False(session.GetLockInfo().ExclusiveLockGranted);
        session.LockExclusive(1000);
        Assert.True(session.GetLockInfo().ExclusiveLockGranted);
        session.Unlock();
        Assert.False(session.GetLockInfo().ExclusiveLockGranted);
        string key = session.LockShared(1000, "shared-key");
        Assert.Equal("shared-key", key);
        Assert.Equal(1, session.GetLockInfo().ClientsHoldingLocks);
        session.Unlock();
    }

    [Fact]
    public void ServiceRequestCarriesStatusByte()
    {
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        session.EnableEvent(EventType.ServiceRequest, EventMechanism.Queue);
        try
        {
            Instrument.RequestService();
            var e = Assert.IsType<ServiceRequestEventArgs>(session.WaitOnEvent(EventType.ServiceRequest, 5000));
            Assert.NotNull(e.StatusByte);
            Assert.Equal(0x40, e.StatusByte!.Value & 0x40);
        }
        finally
        {
            session.DisableEvent(EventType.ServiceRequest);
            session.WriteLine("*CLS");
            session.Query("*OPC?");
        }
    }

    [Fact]
    public void ControlsRemoteLocal()
    {
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        session.ControlRen(RemoteLocalMode.AssertAddressLocalLockout);
        Assert.True(Instrument.IsRemote);
        Assert.True(Instrument.LocalLockout);
        session.ControlRen(RemoteLocalMode.DeassertGoToLocal);
        Assert.False(Instrument.IsRemote);
    }

    [Fact]
    public void ReportsRefusedConnections()
    {
        using var rm = CreateResourceManager();
        var ex = Assert.Throws<VisaException>(() => rm.Open($"TCPIP::127.0.0.1::hislip0,{FreePort()}::INSTR"));
        Assert.Equal(VisaStatus.ErrorResourceNotFound, ex.Status);
    }

    internal static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

public sealed class SocketTests(SimulatorFixture fixture) : MessageBasedProtocolTests, IClassFixture<SimulatorFixture>
{
    protected override ResourceManager CreateResourceManager() => fixture.CreateResourceManager();
    protected override string Resource => fixture.Simulator.SocketResource;
    protected override SimulatedInstrument Instrument => fixture.Instrument;
    protected override bool HasEndIndicator => false;
    protected override bool SupportsServiceRequest => false;

    public override void TimesOutAndRecovers()
    {
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        session.TimeoutMilliseconds = 300;
        Assert.Throws<VisaTimeoutException>(() => session.Query("DELAY? 800"));
        // Sockets cannot clear the device: wait for the late response and discard it.
        Thread.Sleep(900);
        session.Clear();
        session.TimeoutMilliseconds = 5000;
        Assert.Equal(Instrument.Identity, session.Query("*IDN?"));
    }

    [Fact]
    public void EnablesTermCharByDefault()
    {
        using var rm = CreateResourceManager();
        using var session = rm.Open<TcpipSocketSession>(Resource);
        Assert.True(session.TerminationCharacterEnabled);
        Assert.True(session.NoDelay);
        Assert.Equal(fixture.Simulator.Socket.Port, session.Port);
        session.KeepAlive = true;
        Assert.True(session.KeepAlive);
    }
}

[Collection(PortlessLanResourceCollection.Name)]
public sealed class VicpTests(SimulatorFixture fixture) : MessageBasedProtocolTests, IClassFixture<SimulatorFixture>
{
    protected override ResourceManager CreateResourceManager() => fixture.CreateResourceManager();
    protected override string Resource => fixture.Simulator.VicpResource;
    protected override SimulatedInstrument Instrument => fixture.Instrument;

    protected override void TriggerServiceRequestDelivery(MessageBasedSession session) => session.Query("*OPC?");

    [Fact]
    public void ControlsRemoteState()
    {
        using var rm = CreateResourceManager();
        using var session = Open(rm);
        session.ControlRen(RemoteLocalMode.Assert);
        Assert.True(SpinWait.SpinUntil(() => fixture.Simulator.Vicp.IsRemote, 2000));
        session.ControlRen(RemoteLocalMode.Deassert);
        Assert.True(SpinWait.SpinUntil(() => !fixture.Simulator.Vicp.IsRemote, 2000));
    }
}

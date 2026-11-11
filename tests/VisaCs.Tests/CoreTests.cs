using System.Diagnostics;
using System.Text;
using VisaCs.Discovery;
using VisaCs.Formatting;
using VisaCs.Internal;
using VisaCs.Simulation;
using VisaCs.Usb;

namespace VisaCs.Tests;

/// <summary>Parsing, matching and formatting rules of the core VISA layer that need no instrument.</summary>
public class CoreParsingTests
{
    [Fact]
    public void ParsesDecimalsWithExponents()
    {
        // decimal's own parser rejects exponents; NR3 values must not be rounded to integers.
        Assert.Equal(new[] { 0.001234m, 5m }, Ieee4882.ParseValues<decimal>("1.234E-03,5E+00", ','));
        Assert.Equal(-2.5m, Ieee4882.ParseValue<decimal>(" -2.5E+00\n"));
    }

    [Fact]
    public void ParsesSingleValuesLikeInstrumentsSendThem()
    {
        Assert.Equal(2L, Ieee4882.ParseValue<long>("1.9E+00"));
        Assert.Equal(3, Ieee4882.ParseValue<int>("2.5"));
        Assert.Equal(-3, Ieee4882.ParseValue<int>("-2.5"));
        Assert.True(Ieee4882.ParseValue<bool>("1"));
        Assert.False(Ieee4882.ParseValue<bool>("OFF"));
        Assert.True(Ieee4882.ParseValue<bool>("+1.000E+00"));
        Assert.Equal(1.5, Ieee4882.ParseValue<double>("+1.50000E+00"));
        Assert.Equal(VisaStatus.ErrorInvalidFormat, Assert.Throws<VisaException>(() => Ieee4882.ParseValue<double>("abc")).Status);
        Assert.Equal(VisaStatus.ErrorInvalidFormat, Assert.Throws<VisaException>(() => Ieee4882.ParseValue<byte>("300")).Status);
    }

    [Theory]
    [InlineData("USB?*INSTR|TCPIP?*INSTR", "TCPIP", true)]
    [InlineData("USB?*INSTR|TCPIP?*INSTR", "GPIB", true)]
    [InlineData("(GPIB|USB)?*", "USB", true)]
    [InlineData("USB?*", "TCPIP", false)]
    [InlineData("USB?*", "USB", true)]
    [InlineData("?*::INSTR", "ASRL", true)]
    [InlineData("GPIB-VXI?*", "GPIB", true)]
    [InlineData("TCPIP0::1.2.3.4::?*", "TCPIP", true)]
    [InlineData("ASRL1::INSTR", "GPIB", false)]
    public void FindSkipsOnlySourcesTheExpressionCannotMatch(string expression, string intf, bool expected)
    {
        Assert.Equal(expected, ResourceManager.CouldMatchInterface(expression, intf));
    }

    [Fact]
    public void ResourceNamesAreEqualWhenTheyNameTheSameResource()
    {
        var a = ResourceName.Parse("tcpip::scope.local");
        var b = ResourceName.Parse("TCPIP0::SCOPE.LOCAL::inst0::INSTR");
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(ResourceName.Parse("TCPIP::h::INSTR"), ResourceName.Parse("TCPIP::h::5025::SOCKET"));
        Assert.NotEqual(ResourceName.Parse("GPIB0::5::INSTR"), ResourceName.Parse("GPIB0::6::INSTR"));

        var options = new ResourceManagerOptions();
        options.Aliases["dmm"] = "TCPIP::10.1.2.3::INSTR";
        using var rm = new ResourceManager(options);
        Assert.Equal(ResourceName.Parse("TCPIP0::10.1.2.3::inst0::INSTR"), rm.Parse("dmm"));
    }

    [Theory]
    [InlineData("%d bytes", false)]
    [InlineData("%,#f", false)]
    [InlineData("%%b", false)]
    [InlineData("%b", true)]
    [InlineData("DATA %!olzb", true)]
    [InlineData("%,10y", true)]
    [InlineData("%k", false)]
    public void DetectsBinaryConversions(string format, bool expected)
    {
        Assert.Equal(expected, VisaFormatter.HasBinaryConversion(format));
    }

    [Fact]
    public void ScanfStopsAtMalformedBlockHeaders()
    {
        Assert.Equal(new object?[] { 5 }, VisaFormatter.ParseString("%d,%b", "5,#9abcdefghi"));
        Assert.Equal(new object?[] { 5 }, VisaFormatter.ParseString("%d,%b", "5,#2x"));
        Assert.Equal(new object?[] { 5 }, VisaFormatter.ParseString("%d,%b", "5,#z"));
    }

    [Fact]
    public void FormatReportsArgumentErrorsAsVisaErrors()
    {
        var ex = Assert.Throws<VisaException>(() => VisaFormatter.FormatString("%d", "abc"));
        Assert.Equal(VisaStatus.ErrorInvalidFormat, ex.Status);
        VisaFormatter.Format("A %d %*d", [1, 4, 2, 9], null, out int used);
        Assert.Equal(3, used);
    }
}

/// <summary>A USB backend owned by the test: the resource manager must not dispose it.</summary>
internal sealed class OwnedUsbBackend : IUsbBackend, IDisposable
{
    private readonly SimulatedUsbBackend _inner = new();

    public bool IsDisposed { get; private set; }

    public string Name => _inner.Name;

    public IReadOnlyList<UsbDeviceInfo> EnumerateDevices() => _inner.EnumerateDevices();

    public IUsbDeviceHandle Open(UsbDeviceInfo device, int interfaceNumber) => _inner.Open(device, interfaceNumber);

    public void Dispose() => IsDisposed = true;
}

public class CoreResourceManagerTests
{
    private static ResourceManagerOptions LocalOnly() => new()
    {
        EnableLanDiscovery = false,
        EnableMdnsDiscovery = false,
        EnableSerial = false,
        EnablePxi = false,
    };

    [Fact]
    public void DoesNotDisposeAUsbBackendItDidNotCreate()
    {
        var backend = new OwnedUsbBackend();
        var options = LocalOnly();
        options.UsbBackend = backend;
        var rm = new ResourceManager(options);
        Assert.Same(backend, rm.UsbBackend);
        rm.Dispose();
        Assert.False(backend.IsDisposed);
    }

    [Fact]
    public void FindsEveryInterfaceOfAnAlternation()
    {
        var usb = new SimulatedUsbBackend();
        usb.Devices.Add(new SimulatedUsbTmcDevice(new SimulatedInstrument(), 0x1234, 0x5678, "ALT1"));
        var gpib = new SimulatedGpibDriver();
        gpib.Instruments[22] = new SimulatedInstrument();
        var options = LocalOnly();
        options.UsbBackend = usb;
        options.GpibDriver = gpib;
        using var rm = new ResourceManager(options);

        var found = rm.Find("GPIB?*INSTR|USB?*INSTR");
        Assert.Contains("GPIB0::22::INSTR", found);
        Assert.Contains(found, name => name.StartsWith("USB0::0x1234::0x5678::ALT1", StringComparison.Ordinal));
        Assert.Empty(rm.Find("TCPIP?*"));
    }
}

/// <summary>Session behavior of the core layer against the simulated instruments.</summary>
[Collection(PortlessLanResourceCollection.Name)]
public sealed class CoreSessionTests(SimulatorFixture fixture) : IClassFixture<SimulatorFixture>
{
    private MessageBasedSession Open(ResourceManager rm, string? resource = null)
    {
        var session = rm.Open(resource ?? fixture.Simulator.HislipResource);
        session.TimeoutMilliseconds = 5000;
        return session;
    }

    [Fact]
    public async Task QueriesParseTypedValues()
    {
        using var rm = fixture.CreateResourceManager();
        using var session = Open(rm);
        Assert.Equal(2L, session.Query<long>("ECHO? 1.9E+00"));
        Assert.Equal(0.001234m, session.Query<decimal>("ECHO? 1.234E-03"));
        Assert.True(await session.QueryAsync<bool>("ECHO? 1"));
        Assert.Equal(VisaStatus.ErrorInvalidFormat, Assert.Throws<VisaException>(() => session.Query<double>("ECHO? abc")).Status);
        Assert.Equal(new[] { 0m, 0.5m, 1m }, session.QueryAsciiValues<decimal>("DATA:ASCII? 3"));
        Assert.Equal(new[] { 0.0, 0.5 }, await session.QueryAsciiValuesAsync("DATA:ASCII? 2", CancellationToken.None));
    }

    [Fact]
    public void QueryDelayWaitsBetweenWriteAndRead()
    {
        using var rm = fixture.CreateResourceManager();
        using var session = Open(rm);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.QueryDelay = TimeSpan.FromMilliseconds(-1));
        session.QueryDelay = TimeSpan.FromMilliseconds(300);
        var watch = Stopwatch.StartNew();
        Assert.Equal(fixture.Instrument.Identity, session.Query("*IDN?"));
        Assert.True(watch.ElapsedMilliseconds >= 250, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void AttributesKeepNullAndUseEnums()
    {
        using var rm = fixture.CreateResourceManager();
        using var session = Open(rm);
        Assert.Null(session.GetAttribute(VisaAttribute.UserData));
        session.SetAttribute(VisaAttribute.UserData, "x");
        Assert.Equal("x", session.UserData);
        session.SetAttribute(VisaAttribute.UserData, null);
        Assert.Null(session.UserData);
        Assert.Null(session.GetAttribute<string>(VisaAttribute.UserData));

        Assert.Equal(HardwareInterfaceType.Tcpip, session.GetAttribute(VisaAttribute.InterfaceType));
        Assert.Equal(ResourceClass.Instrument, session.ResourceClass);
        Assert.Equal("INSTR", session.GetAttribute(VisaAttribute.ResourceClass));
        Assert.Equal(0x3FFF, session.GetAttribute(VisaAttribute.ResourceManufacturerId));
        Assert.Equal(VisaStatus.ErrorAttributeStateNotSupported,
            Assert.Throws<VisaException>(() => session.SetAttribute(VisaAttribute.TimeoutValue, null)).Status);
    }

    [Fact]
    public void ValidatesSettings()
    {
        using var rm = fixture.CreateResourceManager();
        using var session = Open(rm);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.MaximumQueueLength = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.ReadChunkSize = 0);
        Assert.Equal(VisaStatus.ErrorAttributeStateNotSupported,
            Assert.Throws<VisaException>(() => session.SetAttribute(VisaAttribute.MaxQueueLength, 0)).Status);
        session.SetAttribute(VisaAttribute.MaxQueueLength, 7);
        Assert.Equal(7, session.MaximumQueueLength);
    }

    [Fact]
    public async Task WaitOnEventReportsAClosedSession()
    {
        using var rm = fixture.CreateResourceManager();
        var session = Open(rm);
        await session.EnableEventAsync(EventType.ServiceRequest, EventMechanism.Queue, CancellationToken.None);
        Assert.Null(await session.TryWaitOnEventAsync(EventType.ServiceRequest, 0));
        var waiting = session.WaitOnEventAsync(EventType.ServiceRequest, 10_000);
        await session.DisposeAsync();
        var ex = await Assert.ThrowsAsync<VisaException>(() => waiting);
        Assert.Equal(VisaStatus.ErrorInvalidObject, ex.Status);
    }

    [Fact]
    public async Task AsyncMethodsTakeTheTokenPositionally()
    {
        using var rm = fixture.CreateResourceManager();
        using var session = Open(rm);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.EnableEventAsync(EventType.ServiceRequest, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.LockSharedAsync(1000, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadBinaryValuesAsync<float>(canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rm.OpenAsync(fixture.Simulator.HislipResource, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rm.FindAsync(canceled.Token));

        // A token passed as the last format argument cancels; anywhere else it is an error.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.PrintfAsync("VOLT %f\n", 1.5, canceled.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => session.PrintfAsync("VOLT %f %d\n", canceled.Token, 1.5, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => session.Printf("VOLT %f\n", 1.5, CancellationToken.None));

        await session.PrintfAsync("ECHO? %d\n", 42, CancellationToken.None);
        Assert.Equal(42, await session.ScanfAsync<int>("%d"));
        string key = await session.LockSharedAsync(1000, CancellationToken.None);
        Assert.NotEmpty(key);
        await session.UnlockAsync(CancellationToken.None);
    }

    [Fact]
    public void QueryfPassesLeftoverArgumentsToTheReadFormat()
    {
        using var rm = fixture.CreateResourceManager();
        using var session = Open(rm);
        // 4 values are written; "%,*f" reads as many as the next argument says.
        Assert.Equal(new[] { 0.0, 0.5 }, session.Queryf<double[]>("DATA:ASCII? %d\n", "%,*f", 4, 2));
    }

    [Fact]
    public void ScanfReadsLiteralBInTextNormally()
    {
        using var rm = fixture.CreateResourceManager();
        using var session = Open(rm);
        session.WriteLine("ECHO? 12 bytes");
        Assert.Equal(new object?[] { 12 }, session.Scanf("%d bytes"));
    }

    [Fact]
    public void RejectsABlockThatEndsBeforeItsAnnouncedLength()
    {
        using var rm = fixture.CreateResourceManager();
        using var session = Open(rm);
        var ex = Assert.Throws<VisaException>(() => session.QueryBinaryBlock("ECHO? #9999999999"));
        Assert.Equal(VisaStatus.ErrorIO, ex.Status);
        Assert.Equal(fixture.Instrument.Identity, session.Query("*IDN?"));
    }

    [Fact]
    public void FlushDiscardsOutputWithoutError()
    {
        using var rm = fixture.CreateResourceManager();
        using var session = Open(rm);
        session.Flush(BufferTypes.OutBufferDiscard | BufferTypes.WriteBufferDiscard | BufferTypes.InBufferDiscard);
        Assert.Equal(fixture.Instrument.Identity, session.Query("*IDN?"));
    }

    [Fact]
    public async Task ClosingTheResourceManagerDuringALockedOpenLeavesNoLock()
    {
        string resource = fixture.Simulator.SocketResource;
        using var rmA = fixture.CreateResourceManager();
        using var holder = Open(rmA, resource);
        holder.LockExclusive(1000);

        var rmB = fixture.CreateResourceManager();
        var opening = rmB.OpenAsync(resource, AccessModes.ExclusiveLock, 10_000);
        var watch = Stopwatch.StartNew();
        while (rmB.Sessions.Count == 0 && watch.ElapsedMilliseconds < 5000) await Task.Delay(10);
        await rmB.DisposeAsync();
        holder.Unlock();

        var ex = await Assert.ThrowsAsync<VisaException>(() => opening);
        Assert.Equal(VisaStatus.ErrorInvalidObject, ex.Status);
        Assert.Throws<ObjectDisposedException>(() => rmB.Open(resource));

        // The closed session must not have taken the lock on its way out.
        using var other = Open(rmA, resource);
        other.LockExclusive(1000);
        other.Unlock();
    }
}

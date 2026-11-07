using System.Text;
using VisaCs.Discovery;
using VisaCs.Internal;

namespace VisaCs.Tests;

public class ResourceNameTests
{
    [Theory]
    [InlineData("TCPIP::192.168.1.5::INSTR", "TCPIP0::192.168.1.5::inst0::INSTR")]
    [InlineData("tcpip0::192.168.1.5", "TCPIP0::192.168.1.5::inst0::INSTR")]
    [InlineData("TCPIP3::scope.local::inst1::INSTR", "TCPIP3::scope.local::inst1::INSTR")]
    [InlineData("TCPIP::10.0.0.2::gpib0,5::INSTR", "TCPIP0::10.0.0.2::gpib0,5::INSTR")]
    [InlineData("TCPIP::10.0.0.2::hislip0::INSTR", "TCPIP0::10.0.0.2::hislip0::INSTR")]
    [InlineData("TCPIP::[fe80::1]::INSTR", "TCPIP0::[fe80::1]::inst0::INSTR")]
    [InlineData("TCPIP::10.0.0.2::5025::SOCKET", "TCPIP0::10.0.0.2::5025::SOCKET")]
    [InlineData("TCPIP::[::1]::5025::SOCKET", "TCPIP0::[::1]::5025::SOCKET")]
    [InlineData("USB::0x0957::0x1796::MY123::INSTR", "USB0::0x0957::0x1796::MY123::0::INSTR")]
    [InlineData("USB0::2391::6038::MY123::1::INSTR", "USB0::0x0957::0x1796::MY123::1::INSTR")]
    [InlineData("USB::0x1AB1::0x04CE::DS1ZA1::RAW", "USB0::0x1AB1::0x04CE::DS1ZA1::0::RAW")]
    [InlineData("GPIB::22::INSTR", "GPIB0::22::INSTR")]
    [InlineData("GPIB1::5::7", "GPIB1::5::7::INSTR")]
    [InlineData("GPIB0::INTFC", "GPIB0::INTFC")]
    [InlineData("VICP::192.168.0.9", "VICP0::192.168.0.9::INSTR")]
    [InlineData("ASRL/dev/ttyUSB0::INSTR", "ASRL/dev/ttyUSB0::INSTR")]
    public void ParsesToCanonicalForm(string input, string canonical)
    {
        Assert.Equal(canonical, ResourceName.Parse(input).Canonical);
    }

    [Fact]
    public void ParsesTcpipDetails()
    {
        var hislip = Assert.IsType<TcpipInstrumentResourceName>(ResourceName.Parse("TCPIP0::example.com::hislip2,4881::INSTR"));
        Assert.True(hislip.IsHislip);
        Assert.Equal("hislip2", hislip.HislipSubAddress);
        Assert.Equal(4881, hislip.HislipPort);
        Assert.Equal("example.com", hislip.Host);

        var v6 = Assert.IsType<TcpipInstrumentResourceName>(ResourceName.Parse("TCPIP::[fe80::1%3]::inst0::INSTR"));
        Assert.Equal("fe80::1%3", v6.Host);
        Assert.False(v6.IsHislip);

        var socket = Assert.IsType<TcpipSocketResourceName>(ResourceName.Parse("TCPIP1::host::5025::SOCKET"));
        Assert.Equal(1, socket.Board);
        Assert.Equal(5025, socket.Port);
        Assert.Equal(ResourceClass.Socket, socket.ResourceClass);
    }

    [Fact]
    public void ParsesUsbAndGpibDetails()
    {
        var usb = Assert.IsType<UsbResourceName>(ResourceName.Parse("USB0::0x0957::0x1796::MY12345678::2::INSTR"));
        Assert.Equal(0x0957, usb.VendorId);
        Assert.Equal(0x1796, usb.ProductId);
        Assert.Equal("MY12345678", usb.SerialNumber);
        Assert.Equal(2, usb.InterfaceNumber);

        var gpib = Assert.IsType<GpibInstrumentResourceName>(ResourceName.Parse("GPIB2::14::3::INSTR"));
        Assert.Equal(2, gpib.Board);
        Assert.Equal(14, gpib.PrimaryAddress);
        Assert.Equal(3, gpib.SecondaryAddress);
        Assert.Equal(HardwareInterfaceType.Gpib, gpib.InterfaceType);
    }

    [Fact]
    public void SerialBoardNumberMapsToPlatformPort()
    {
        var serial = Assert.IsType<SerialResourceName>(ResourceName.Parse("ASRL3::INSTR"));
        Assert.Equal(OperatingSystem.IsWindows() ? "COM3" : "/dev/ttyS2", serial.PortName);
        Assert.Equal("ASRL3::INSTR", serial.Canonical);
        Assert.Equal(3, serial.Board);

        // On Windows ASRLCOM7 is the same port as ASRL7, so it has the same canonical name (and lock key).
        var com = Assert.IsType<SerialResourceName>(ResourceName.Parse("ASRLCOM7::INSTR"));
        Assert.Equal("COM7", com.PortName);
        Assert.Equal(7, com.Board);
        Assert.Equal(OperatingSystem.IsWindows() ? "ASRL7::INSTR" : "ASRLCOM7::INSTR", com.Canonical);
    }

    [Fact]
    public void RecognizesHardwareOnlyInterfaces()
    {
        Assert.IsType<UnsupportedResourceName>(ResourceName.Parse("PXI0::MEMACC"));
        Assert.IsType<UnsupportedResourceName>(ResourceName.Parse("VXI0::1::INSTR"));
        Assert.Equal(HardwareInterfaceType.GpibVxi, ResourceName.Parse("GPIB-VXI0::24::INSTR").InterfaceType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("FOO::1::INSTR")]
    [InlineData("TCPIP::")]
    [InlineData("TCPIP::host::port::SOCKET")]
    [InlineData("TCPIP::host::99999::SOCKET")]
    [InlineData("TCPIP::host::a::b::INSTR")]
    [InlineData("USB::0x0957::INSTR")]
    [InlineData("USB::zz::0x1::SN::INSTR")]
    [InlineData("GPIB::31::INSTR")]
    [InlineData("GPIB::5::31::INSTR")]
    [InlineData("GPIBX::5::INSTR")]
    [InlineData("ASRL1::INSTR::EXTRA")]
    public void RejectsInvalidNames(string input)
    {
        Assert.False(ResourceName.TryParse(input, out _));
        var ex = Assert.Throws<VisaException>(() => ResourceName.Parse(input));
        Assert.Equal(VisaStatus.ErrorInvalidResourceName, ex.Status);
    }

    [Fact]
    public void ResourceManagerResolvesAliases()
    {
        var options = new ResourceManagerOptions();
        options.Aliases["dmm"] = "TCPIP::10.1.2.3::INSTR";
        using var rm = new ResourceManager(options);
        var parsed = rm.Parse("DMM");
        Assert.Equal("TCPIP0::10.1.2.3::inst0::INSTR", parsed.Canonical);
        Assert.Equal("DMM", parsed.Alias);
    }
}

public class VisaPatternTests
{
    [Theory]
    [InlineData("?*::INSTR", "TCPIP0::1.2.3.4::inst0::INSTR", true)]
    [InlineData("?*::INSTR", "TCPIP0::1.2.3.4::5025::SOCKET", false)]
    [InlineData("?*", "anything", true)]
    [InlineData("USB?*", "USB0::0x1::0x2::S::0::INSTR", true)]
    [InlineData("usb?*", "USB0::0x1::0x2::S::0::INSTR", true)]
    [InlineData("USB?*", "TCPIP0::x::inst0::INSTR", false)]
    [InlineData("GPIB[0-1]::?*", "GPIB1::5::INSTR", true)]
    [InlineData("GPIB[0-1]::?*", "GPIB2::5::INSTR", false)]
    [InlineData("GPIB[^0]::?*", "GPIB2::5::INSTR", true)]
    [InlineData("(GPIB|ASRL)?*INSTR", "ASRL1::INSTR", true)]
    [InlineData("TCPIP0::192.168.1.5::?*", "TCPIP0::192.168.1.5::inst0::INSTR", true)]
    [InlineData("TCPIP0::192.168.1.5::?*", "TCPIP0::192x168x1x5::inst0::INSTR", false)]
    [InlineData("GPIB0::1+::INSTR", "GPIB0::111::INSTR", true)]
    [InlineData(@"ASRL\?::INSTR", "ASRL?::INSTR", true)]
    [InlineData(@"ASRL\?::INSTR", "ASRL1::INSTR", false)]
    public void MatchesLikeVisa(string expression, string resource, bool expected)
    {
        Assert.Equal(expected, VisaPattern.Parse(expression).IsMatch(resource));
    }

    [Fact]
    public void EvaluatesAttributeExpressions()
    {
        var attributes = new Dictionary<VisaAttribute, object>
        {
            [VisaAttribute.ManufacturerId] = 0x0957,
            [VisaAttribute.ModelCode] = 0x1796,
            [VisaAttribute.UsbSerialNumber] = "MY123",
        };
        const string name = "USB0::0x0957::0x1796::MY123::0::INSTR";
        Assert.True(VisaPattern.Parse("USB?*{VI_ATTR_MANF_ID==0x0957}").IsMatch(name, attributes));
        Assert.False(VisaPattern.Parse("USB?*{VI_ATTR_MANF_ID==0x1AB1}").IsMatch(name, attributes));
        Assert.True(VisaPattern.Parse("USB?*{VI_ATTR_MANF_ID==2391 && VI_ATTR_MODEL_CODE!=0}").IsMatch(name, attributes));
        Assert.True(VisaPattern.Parse("USB?*{VI_ATTR_MANF_ID==1 || VI_ATTR_USB_SERIAL_NUM==\"my123\"}").IsMatch(name, attributes));
        Assert.True(VisaPattern.Parse("USB?*{!(VI_ATTR_MODEL_CODE<0x1000)}").IsMatch(name, attributes));
        Assert.False(VisaPattern.Parse("USB?*{VI_ATTR_GPIB_PRIMARY_ADDR==1}").IsMatch(name, attributes));
    }

    [Theory]
    [InlineData("GPIB[0-1")]
    [InlineData("?*{VI_ATTR_MANF_ID==}")]
    [InlineData("?*{VI_ATTR_UNKNOWN==1}")]
    [InlineData("?*{VI_ATTR_MANF_ID==1")]
    public void RejectsInvalidExpressions(string expression)
    {
        var ex = Assert.Throws<VisaException>(() => VisaPattern.Parse(expression));
        Assert.Equal(VisaStatus.ErrorInvalidExpression, ex.Status);
    }
}

public class InputBufferTests
{
    /// <summary>Feeds predefined chunks; each tuple is (data, END on last byte).</summary>
    private static FillCallback Source(params (string Data, bool End)[] chunks)
    {
        int index = 0;
        return (target, deadline, async) =>
        {
            if (index >= chunks.Length) throw new VisaTimeoutException();
            var (data, e) = chunks[index++];
            var bytes = Encoding.ASCII.GetBytes(data);
            Assert.True(bytes.Length <= target.Length);
            bytes.CopyTo(target);
            return new ValueTask<FillResult>(new FillResult(bytes.Length, e));
        };
    }

    private static (ReadStatus Status, string Text) Read(InputBuffer buffer, int count, int term, FillCallback fill, bool termIsEnd = false)
    {
        var dest = new byte[count];
        var result = AsyncIO.Complete(buffer.ReadAsync(dest, term, termIsEnd, fill, Deadline.FromMilliseconds(100), async: false));
        return (result.Status, Encoding.ASCII.GetString(dest, 0, result.Count));
    }

    [Fact]
    public void StopsAtEnd()
    {
        var buffer = new InputBuffer(1024);
        var fill = Source(("hel", false), ("lo\n", true));
        Assert.Equal((ReadStatus.EndReceived, "hello\n"), Read(buffer, 100, -1, fill));
    }

    [Fact]
    public void StopsAtTermCharAndKeepsRest()
    {
        var buffer = new InputBuffer(1024);
        var fill = Source(("one\ntwo\n", true));
        Assert.Equal((ReadStatus.TerminationCharacterEncountered, "one\n"), Read(buffer, 100, '\n', fill));
        Assert.Equal((ReadStatus.EndReceived, "two\n"), Read(buffer, 100, '\n', fill));
    }

    [Fact]
    public void StopsAtCount()
    {
        var buffer = new InputBuffer(1024);
        var fill = Source(("abcdef", true));
        Assert.Equal((ReadStatus.MaximumCountReached, "abc"), Read(buffer, 3, -1, fill));
        Assert.Equal((ReadStatus.EndReceived, "def"), Read(buffer, 3, -1, fill));
    }

    [Fact]
    public void EmptyEndChunkCompletesRead()
    {
        var buffer = new InputBuffer(1024);
        var fill = Source(("data", false), ("", true));
        Assert.Equal((ReadStatus.EndReceived, "data"), Read(buffer, 100, -1, fill));
    }

    [Fact]
    public void DirectFillPathPushesBackDataAfterTermChar()
    {
        var buffer = new InputBuffer(8);
        var fill = Source(("ab\ncdefg", true));
        // Destination larger than the internal buffer uses the direct path.
        Assert.Equal((ReadStatus.TerminationCharacterEncountered, "ab\n"), Read(buffer, 64, '\n', fill));
        Assert.Equal((ReadStatus.EndReceived, "cdefg"), Read(buffer, 64, '\n', fill));
    }

    [Fact]
    public void TermCharCanCountAsEnd()
    {
        var buffer = new InputBuffer(1024);
        var fill = Source(("x\ny", false));
        Assert.Equal((ReadStatus.EndReceived, "x\n"), Read(buffer, 100, '\n', fill, termIsEnd: true));
    }

    [Fact]
    public void TimeoutReportsPartialCount()
    {
        var buffer = new InputBuffer(1024);
        var fill = Source(("abc", false));
        var ex = Assert.Throws<VisaTimeoutException>(() => Read(buffer, 100, -1, fill));
        Assert.Equal(3, ex.BytesTransferred);
    }
}

public class StatusAndAttributeNameTests
{
    [Fact]
    public void StatusCodesMatchVisaH()
    {
        Assert.Equal(unchecked((int)0xBFFF0015), (int)VisaStatus.ErrorTimeout);
        Assert.Equal("VI_ERROR_TMO", VisaStatusInfo.GetName(VisaStatus.ErrorTimeout));
        Assert.True(VisaStatus.ErrorIO.IsError());
        Assert.False(VisaStatus.SuccessMaxCount.IsError());
        var ex = new VisaException(VisaStatus.ErrorResourceNotFound);
        Assert.StartsWith("VI_ERROR_RSRC_NFOUND", ex.Message);
    }

    [Fact]
    public void AttributeNamesRoundTrip()
    {
        Assert.Equal(0x3FFF001Au, (uint)VisaAttribute.TimeoutValue);
        Assert.Equal("VI_ATTR_TMO_VALUE", VisaAttributeNames.GetName(VisaAttribute.TimeoutValue));
        Assert.True(VisaAttributeNames.TryParse("vi_attr_manf_id", out var a));
        Assert.Equal(VisaAttribute.ManufacturerId, a);
    }

    [Fact]
    public void BinaryHelpersConvertValues()
    {
        Assert.Equal("#15", Encoding.ASCII.GetString(Ieee4882.BuildBlockHeader(5)));
        Assert.Equal("#3123", Encoding.ASCII.GetString(Ieee4882.BuildBlockHeader(123)));
        var big = new byte[] { 0x00, 0x01, 0x01, 0x00 };
        Assert.Equal(new short[] { 1, 256 }, Ieee4882.BytesToValues<short>(big, ByteOrder.BigEndian));
        Assert.Equal(new short[] { 256, 1 }, Ieee4882.BytesToValues<short>(big, ByteOrder.LittleEndian));
        Assert.Throws<VisaException>(() => Ieee4882.BytesToValues<int>(new byte[3], ByteOrder.LittleEndian));
        // Element size is the in-memory size: 2 bytes for char and 1 for bool (Marshal.SizeOf would say 1 and 4).
        Assert.Equal("AB".ToCharArray(), Ieee4882.BytesToValues<char>([0x00, 0x41, 0x00, 0x42], ByteOrder.BigEndian));
        Assert.Equal(new[] { true, false, true }, Ieee4882.BytesToValues<bool>([1, 0, 1], ByteOrder.LittleEndian));
        Assert.Equal(new[] { 1.5, -2.0, 300.0 }, Ieee4882.ParseValues<double>("1.5, -2.0E0 ,3E2", ','));
        Assert.Equal(new[] { 1, 2 }, Ieee4882.ParseValues<int>("1.000E+00,2", ','));
        Assert.Equal(new[] { 1L, -2L, 3L }, Ieee4882.ParseValues<long>(" 1 ,, \t,-2,3,", ','));
        Assert.Equal(new[] { 4.5, 6.0 }, Ieee4882.ParseValues<double>("4.5 6", ' '));
        Assert.Empty(Ieee4882.ParseValues<double>("", ','));
        Assert.Throws<VisaException>(() => Ieee4882.ParseValues<int>("1,abc,3", ','));
        Assert.Throws<VisaException>(() => Ieee4882.ParseValues<double>("1.5,NaNx", ','));
    }
}

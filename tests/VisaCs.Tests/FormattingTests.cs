using System.Text;
using VisaCs.Formatting;
using VisaCs.Internal;
using VisaCs.Simulation;

namespace VisaCs.Tests;

public class VisaFormatterTests
{
    [Theory]
    [InlineData("%d", 42, "42")]
    [InlineData("%5d|", 42, "   42|")]
    [InlineData("%-5d|", 42, "42   |")]
    [InlineData("%05d", -42, "-0042")]
    [InlineData("%+d", 7, "+7")]
    [InlineData("%.3d", 7, "007")]
    [InlineData("%x", 255, "ff")]
    [InlineData("%#X", 255, "0XFF")]
    [InlineData("%o", 8, "10")]
    [InlineData("%u", -1, "4294967295")]
    [InlineData("%f", 1.5, "1.500000")]
    [InlineData("%.2f", 3.14159, "3.14")]
    [InlineData("%8.3f|", -2.5, "  -2.500|")]
    [InlineData("%e", 12345.678, "1.234568e+04")]
    [InlineData("%.2E", 0.000123, "1.23E-04")]
    [InlineData("%g", 0.0001, "0.0001")]
    [InlineData("%g", 1234567.0, "1.23457e+06")]
    [InlineData("%g", 100.0, "100")]
    [InlineData("%G", 1e-10, "1E-10")]
    [InlineData("%s", "VOLT", "VOLT")]
    [InlineData("%.2s", "VOLT", "VO")]
    [InlineData("%c", 'A', "A")]
    [InlineData("%@1d", 3.7, "4")]
    [InlineData("%@2d", 3, "3.000000")]
    [InlineData("%@3f", 1500.0, "1.500000E+03")]
    [InlineData("100%%", 0, "100%")]
    public void FormatsLikePrintf(string format, object value, string expected)
    {
        object?[] args = format.Contains("%%") ? [] : [value];
        Assert.Equal(expected, VisaFormatter.FormatString(format, args));
    }

    [Fact]
    public void FormatsWidthsAndArrays()
    {
        Assert.Equal("  ab", VisaFormatter.FormatString("%*s", 4, "ab"));
        Assert.Equal("3.14", VisaFormatter.FormatString("%.*f", 2, 3.14159));
        Assert.Equal("1,2,3", VisaFormatter.FormatString("%,3d", new[] { 1, 2, 3, 4 }));
        Assert.Equal("1,2", VisaFormatter.FormatString("%,*d", 2, new[] { 1, 2, 3 }));
        Assert.Equal("1.50,2.25", VisaFormatter.FormatString("%,#.2f", new[] { 1.5, 2.25 }));
        Assert.Equal("SOUR:LIST 1,2,3\n", VisaFormatter.FormatString("SOUR:LIST %d\n", new List<int> { 1, 2, 3 }));
    }

    [Fact]
    public void FormatsBinaryBlocks()
    {
        Assert.Equal("#13\x01\x02\x03"u8.ToArray(), VisaFormatter.Format("%b", [new byte[] { 1, 2, 3 }]));
        Assert.Equal("#14\x00\x01\x01\x00"u8.ToArray(), VisaFormatter.Format("%hb", [new short[] { 1, 256 }]));
        Assert.Equal("#14\x01\x00\x00\x01"u8.ToArray(), VisaFormatter.Format("%!olhb", [new short[] { 1, 256 }]));
        Assert.Equal(new byte[] { 0x3F, 0x80, 0, 0 }, VisaFormatter.Format("%zy", [new[] { 1.0f }]));
        Assert.Equal("#12\x01\x02"u8.ToArray(), VisaFormatter.Format("%,2b", [new byte[] { 1, 2, 3 }]));
    }

    [Fact]
    public void ParsesLikeScanf()
    {
        Assert.Equal(new object?[] { 42 }, VisaFormatter.ParseString("%d", " 42\n"));
        Assert.Equal(new object?[] { -1.25e-3 }, VisaFormatter.ParseString("%f", "-1.25E-03"));
        Assert.Equal(new object?[] { 1 }, VisaFormatter.ParseString("%d", "+1.00000000E+00"));
        Assert.Equal(new object?[] { 255u }, VisaFormatter.ParseString("%x", "0xff"));
        Assert.Equal(new object?[] { 16 }, VisaFormatter.ParseString("%i", "0x10"));
        Assert.Equal(new object?[] { 8 }, VisaFormatter.ParseString("%i", "010"));
        Assert.Equal(new object?[] { 5L }, VisaFormatter.ParseString("%Ld", "5"));
        Assert.Equal(new object?[] { (short)5 }, VisaFormatter.ParseString("%hd", "5"));
        Assert.Equal(new object?[] { "Keysight", "34461A" }, VisaFormatter.ParseString("%[^,],%[^,]", "Keysight,34461A,MY1,A.02"));
        Assert.Equal(new object?[] { "VOLT", 2.5 }, VisaFormatter.ParseString("%s %f", "VOLT 2.5"));
        Assert.Equal(new object?[] { 3 }, VisaFormatter.ParseString("%*s %d", "skip 3"));
        Assert.Equal(new object?[] { 'A', "BC" }, VisaFormatter.ParseString("%c%2c", "ABC"));
        Assert.Equal(new object?[] { "rest of it\n" }, VisaFormatter.ParseString("%t", "rest of it\n"));
        Assert.Equal(new object?[] { "line one", "line two" }, VisaFormatter.ParseString("%T%T", "line one\nline two\n"));
        Assert.Equal(new object?[] { 1, 2 }, VisaFormatter.ParseString("ERR %d,%d", "ERR 1,2"));
        Assert.Empty(VisaFormatter.ParseString("ERR %d", "OK 1")); // literal mismatch stops parsing
    }

    [Fact]
    public void ParsesArraysAndBinary()
    {
        Assert.Equal(new[] { 1.0, 2.5, -3.0 }, (double[])VisaFormatter.ParseString("%,#f", "1.0, 2.5,-3E0\n")[0]!);
        Assert.Equal(new[] { 1, 2 }, (int[])VisaFormatter.ParseString("%,2d", "1,2,3")[0]!);
        Assert.Equal(new[] { 1, 2, 3 }, (int[])VisaFormatter.Parse("%,*d", "1,2,3"u8, [3])[0]!);
        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])VisaFormatter.Parse("%b", "#13\x01\x02\x03\n"u8)[0]!);
        Assert.Equal(new short[] { 1, 256 }, (short[])VisaFormatter.Parse("%hb", "CURV #14\x00\x01\x01\x00"u8)[0]!);
        Assert.Equal(new short[] { 256, 1 }, (short[])VisaFormatter.Parse("%!olhb", "#14\x00\x01\x01\x00"u8)[0]!);
        Assert.Equal(new[] { 1.0f }, (float[])VisaFormatter.Parse("%zy", new byte[] { 0x3F, 0x80, 0, 0 })[0]!);
        Assert.Equal("abc"u8.ToArray(), (byte[])VisaFormatter.Parse("%b", "#0abc\n"u8)[0]!);
    }

    [Theory]
    [InlineData("%")]
    [InlineData("%,d")]
    [InlineData("%[abc")]
    public void RejectsBadFormats(string format)
    {
        var ex = Assert.ThrowsAny<Exception>(() => VisaFormatter.FormatString(format, 1));
        Assert.True(ex is FormatException or NotSupportedException, ex.GetType().Name);
        Assert.False(VisaFormatter.HasBinaryConversion(format));
    }

    [Fact]
    public void RejectsMissingArguments()
    {
        Assert.Throws<FormatException>(() => VisaFormatter.FormatString("%d %d", 1));
        Assert.Throws<NotSupportedException>(() => VisaFormatter.FormatString("%k", 1));
    }

    [Fact]
    public void DoesNotWrapItsOwnErrorsTwice()
    {
        // "Not enough arguments" is raised where BCL parse errors of an argument are wrapped; it must stay as it is.
        var ex = Assert.Throws<FormatException>(() => VisaFormatter.FormatString("%d %d", 1));
        Assert.Null(ex.InnerException);
        var wrapped = Assert.Throws<FormatException>(() => VisaFormatter.FormatString("%d", "abc"));
        Assert.IsType<FormatException>(wrapped.InnerException);
    }
}

/// <summary>The VISA statuses MessageBasedSession reports for VisaCs.Formatting errors.</summary>
public class FormattingErrorMappingTests
{
    [Fact]
    public void MapsFormattingErrorsToVisaStatuses()
    {
        var invalid = new FormatException("bad");
        Assert.True(FormattingErrorMapping.IsFormattingError(invalid));
        var mapped = FormattingErrorMapping.ToVisa(invalid);
        Assert.Equal(VisaStatus.ErrorInvalidFormat, mapped.Status);
        Assert.Same(invalid, mapped.InnerException);

        var unsupported = new NotSupportedException("unknown");
        Assert.True(FormattingErrorMapping.IsFormattingError(unsupported));
        mapped = FormattingErrorMapping.ToVisa(unsupported);
        Assert.Equal(VisaStatus.ErrorFormatNotSupported, mapped.Status);
        Assert.Same(unsupported, mapped.InnerException);

        Assert.False(FormattingErrorMapping.IsFormattingError(new IOException()));
    }
}

[Collection(PortlessLanResourceCollection.Name)]
public sealed class FormattedIOSessionTests(SimulatorFixture fixture) : IClassFixture<SimulatorFixture>
{
    public static TheoryData<string> Resources => new() { "vxi11", "hislip", "socket" };

    private string Resource(string kind) => kind switch
    {
        "vxi11" => fixture.Simulator.Vxi11Resource,
        "hislip" => fixture.Simulator.HislipResource,
        _ => fixture.Simulator.SocketResource,
    };

    [Theory]
    [MemberData(nameof(Resources))]
    public void PrintsAndScans(string kind)
    {
        using var rm = fixture.CreateResourceManager();
        using var session = rm.Open(Resource(kind));
        session.TimeoutMilliseconds = 5000;

        session.Printf("ECHO? %d,%.2f,%s\n", 5, 2.5, "text");
        var values = session.Scanf("%d,%f,%s");
        Assert.Equal(new object?[] { 5, 2.5, "text" }, values);

        Assert.Equal(1.2345, session.Queryf<double>("MEAS:VOLT?\n", "%f"), 6);
        Assert.Equal("VisaCs", session.Queryf<string>("*IDN?\n", "%[^,]"));
        var array = session.Queryf<double[]>("DATA:ASCII? %d\n", "%,#f", 4);
        Assert.Equal(new[] { 0.0, 0.5, 1.0, 1.5 }, array);

        // Binary block out and back in, with '\n' bytes inside the data.
        var data = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        session.Printf("DATA:STORE %b\n", data);
        Assert.Equal(data, session.Queryf<byte[]>("DATA:STORE?\n", "%b"));

        var floats = session.Queryf<float[]>("DATA:FLOAT? 8\n", "%!olzb");
        Assert.Equal(3.5f, floats[7]);
        Assert.Equal(fixture.Instrument.Identity, session.Query("*IDN?"));
    }

    [Fact]
    public void ReportsFormatErrorsAsVisaStatuses()
    {
        using var rm = fixture.CreateResourceManager();
        using var session = rm.Open(fixture.Simulator.SocketResource);
        session.TimeoutMilliseconds = 5000;
        Assert.Equal(VisaStatus.ErrorInvalidFormat, Assert.Throws<VisaException>(() => session.Printf("%d %d\n", 1)).Status);
        Assert.Equal(VisaStatus.ErrorFormatNotSupported, Assert.Throws<VisaException>(() => session.Printf("%k\n", 1)).Status);
        var ex = Assert.Throws<VisaException>(() => session.Queryf("*IDN?\n", "%,"));
        Assert.Equal(VisaStatus.ErrorInvalidFormat, ex.Status);
        Assert.IsType<FormatException>(ex.InnerException);
        session.Printf("DATA:STORE %b\n", new byte[] { 1, 2, 3 });
        Assert.Equal(VisaStatus.ErrorInvalidFormat, Assert.Throws<VisaException>(() => session.QueryBinaryValues<int>("DATA:STORE?")).Status);
        Assert.Equal(VisaStatus.ErrorInvalidFormat, Assert.Throws<VisaException>(() => session.QueryAsciiValues<int>("ECHO? 1,abc")).Status);
        Assert.Equal(fixture.Instrument.Identity, session.Query("*IDN?"));
    }
}

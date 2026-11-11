using VisaCs.Internal;
using VisaCs.Serial;

namespace VisaCs.Tests;

/// <summary>The translation of VisaCs.Serial exceptions to the VISA status codes a serial session reports.</summary>
public sealed class SerialErrorMappingTests
{
    public static TheoryData<SerialPortError, VisaStatus> Statuses => new()
    {
        { SerialPortError.IOError, VisaStatus.ErrorIO },
        { SerialPortError.NotFound, VisaStatus.ErrorResourceNotFound },
        { SerialPortError.InUse, VisaStatus.ErrorResourceBusy },
        { SerialPortError.AccessDenied, VisaStatus.ErrorNoPermission },
        { SerialPortError.Disconnected, VisaStatus.ErrorConnectionLost },
        { SerialPortError.Aborted, VisaStatus.ErrorAbort },
        { SerialPortError.Closed, VisaStatus.ErrorAbort },
        { SerialPortError.UnsupportedSetting, VisaStatus.ErrorAttributeStateNotSupported },
        { SerialPortError.SystemError, VisaStatus.ErrorSystemError },
    };

    [Fact]
    public void EveryErrorHasAStatus() =>
        Assert.Equal(Enum.GetValues<SerialPortError>().Order(), Statuses.Select(row => (SerialPortError)row[0]).Order());

    [Theory]
    [MemberData(nameof(Statuses))]
    public void MapsErrorToStatus(SerialPortError error, VisaStatus status)
    {
        var original = new SerialPortException(error, "The port failed.", nativeError: 42);
        Assert.True(SerialErrorMapping.TryMap(original, out var mapped));
        var visa = Assert.IsType<VisaException>(mapped);
        Assert.Equal(status, visa.Status);
        Assert.Equal(42, visa.NativeError);
        Assert.Same(original, visa.InnerException);
        Assert.EndsWith("The port failed.", visa.Message);
    }

    [Fact]
    public void MapsTimeoutWithBytesTransferred()
    {
        var original = new SerialTimeoutException("Serial write timed out.") { BytesTransferred = 17 };
        Assert.True(SerialErrorMapping.TryMap(original, out var mapped));
        var timeout = Assert.IsType<VisaTimeoutException>(mapped);
        Assert.Equal(VisaStatus.ErrorTimeout, timeout.Status);
        Assert.Equal(17, timeout.BytesTransferred);
        Assert.Same(original, timeout.InnerException);
    }

    [Fact]
    public void MapsTheTimeoutInsideACancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var original = new OperationCanceledException("Canceled.", new SerialTimeoutException("t") { BytesTransferred = 5 }, cts.Token);
        Assert.True(SerialErrorMapping.TryMap(original, out var mapped));
        var canceled = Assert.IsType<OperationCanceledException>(mapped);
        Assert.Equal(cts.Token, canceled.CancellationToken);
        Assert.Equal(5, Assert.IsType<VisaTimeoutException>(canceled.InnerException).BytesTransferred);
    }

    [Fact]
    public void MapsUnsupportedPlatform()
    {
        Assert.True(SerialErrorMapping.TryMap(new PlatformNotSupportedException("No serial ports."), out var mapped));
        Assert.Equal(VisaStatus.ErrorOperationNotSupported, Assert.IsType<VisaException>(mapped).Status);
    }

    [Fact]
    public void MissingPortIsNotFoundInBothLayers()
    {
        string port = OperatingSystem.IsWindows() ? "COM239" : "/dev/visacs-no-such-port";
        var direct = Assert.Throws<SerialPortException>(() => NativeSerialPort.Open(port));
        Assert.Equal(SerialPortError.NotFound, direct.Error);
        Assert.NotNull(direct.NativeError);

        using var rm = new ResourceManager();
        var visa = Assert.Throws<VisaException>(() => rm.Open($"ASRL{port}::INSTR"));
        Assert.Equal(VisaStatus.ErrorResourceNotFound, visa.Status);
        Assert.Equal(direct.NativeError, visa.NativeError);
        Assert.IsType<SerialPortException>(visa.InnerException);
    }

    [Fact]
    public void LeavesOtherExceptionsAlone()
    {
        Assert.False(SerialErrorMapping.TryMap(new ObjectDisposedException("port"), out _));
        Assert.False(SerialErrorMapping.TryMap(new OperationCanceledException(), out _));
        Assert.False(SerialErrorMapping.TryMap(new ArgumentOutOfRangeException("x"), out _));
    }
}

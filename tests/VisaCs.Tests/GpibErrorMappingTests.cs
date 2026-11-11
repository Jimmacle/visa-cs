using VisaCs.Gpib;
using VisaCs.Internal;

namespace VisaCs.Tests;

/// <summary>How the VISA sessions translate the GPIB library's exceptions (src/VisaCs/Internal/GpibErrorMapping.cs).</summary>
public sealed class GpibErrorMappingTests
{
    /// <summary>Every <see cref="GpibError"/> and the VISA status the GPIB sessions reported for it before the split.</summary>
    public static TheoryData<GpibError, VisaStatus> Statuses => new()
    {
        { GpibError.Unknown, VisaStatus.ErrorIO },
        { GpibError.SystemError, VisaStatus.ErrorSystemError },
        { GpibError.NotControllerInCharge, VisaStatus.ErrorNotControllerInCharge },
        { GpibError.NoListeners, VisaStatus.ErrorNoListeners },
        { GpibError.NotAddressed, VisaStatus.ErrorInvalidParameter },
        { GpibError.InvalidArgument, VisaStatus.ErrorInvalidParameter },
        { GpibError.NotSystemController, VisaStatus.ErrorNotSystemController },
        { GpibError.NoBoard, VisaStatus.ErrorResourceNotFound },
        { GpibError.DmaError, VisaStatus.ErrorIO },
        { GpibError.AsyncInProgress, VisaStatus.ErrorInProgress },
        { GpibError.NotSupported, VisaStatus.ErrorOperationNotSupported },
        { GpibError.FileSystemError, VisaStatus.ErrorIO },
        { GpibError.BusError, VisaStatus.ErrorBusError },
        { GpibError.StatusByteOverflow, VisaStatus.ErrorIO },
        { GpibError.SrqStuck, VisaStatus.ErrorSrqNotOccurred },
        { GpibError.TableProblem, VisaStatus.ErrorIO },
        { GpibError.Locked, VisaStatus.ErrorResourceLocked },
        { GpibError.NotifyRearmFailed, VisaStatus.ErrorIO },
        { GpibError.InvalidDescriptor, VisaStatus.ErrorInvalidObject },
        { GpibError.WaitInProgress, VisaStatus.ErrorInProgress },
        { GpibError.EventReset, VisaStatus.ErrorIO },
        { GpibError.PowerLost, VisaStatus.ErrorConnectionLost },
        { GpibError.LibraryNotFound, VisaStatus.ErrorLibraryNotFound },
        { GpibError.DeviceOpenFailed, VisaStatus.ErrorResourceNotFound },
    };

    [Theory]
    [MemberData(nameof(Statuses))]
    public void MapsErrorsToVisaStatuses(GpibError error, VisaStatus status)
    {
        var original = new GpibException(error, "GPIB write failed: something.") { NativeError = 42 };
        var mapped = Assert.IsType<VisaException>(GpibErrorMapping.ToVisa(original));
        Assert.Equal(status, mapped.Status);
        Assert.Equal(42, mapped.NativeError);
        Assert.Same(original, mapped.InnerException);
        Assert.Contains("GPIB write failed: something.", mapped.Message);
    }

    [Fact]
    public void CoversEveryError()
    {
        var listed = Statuses.Select(row => (GpibError)row[0]).ToHashSet();
        Assert.Equal(Enum.GetValues<GpibError>().ToHashSet(), listed);
    }

    [Fact]
    public void MapsTimeouts()
    {
        var original = new GpibTimeoutException("GPIB write timed out.") { BytesTransferred = 17, NativeError = 6 };
        var mapped = Assert.IsType<VisaTimeoutException>(GpibErrorMapping.ToVisa(original));
        Assert.Equal(VisaStatus.ErrorTimeout, mapped.Status);
        Assert.Equal(17, mapped.BytesTransferred);
        Assert.Equal(6, mapped.NativeError);
        Assert.Same(original, mapped.InnerException);
    }

    [Fact]
    public void MapsTheTimeoutInsideACancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var inner = new GpibTimeoutException("GPIB read was canceled.") { BytesTransferred = 3 };
        var canceled = new OperationCanceledException("The operation was canceled.", inner, source.Token);
        var mapped = Assert.IsType<OperationCanceledException>(GpibErrorMapping.ToVisa(canceled));
        Assert.Equal(source.Token, mapped.CancellationToken);
        Assert.Equal(3, Assert.IsType<VisaTimeoutException>(mapped.InnerException).BytesTransferred);
    }

    [Fact]
    public void LeavesOtherExceptionsAlone()
    {
        Assert.Null(GpibErrorMapping.ToVisa(new ObjectDisposedException("device")));
        Assert.Null(GpibErrorMapping.ToVisa(new ArgumentOutOfRangeException("primaryAddress")));
        Assert.Null(GpibErrorMapping.ToVisa(new OperationCanceledException()));
        var thrown = Assert.Throws<VisaException>(() => GpibErrorMapping.Call(() => throw new GpibException(GpibError.NoListeners)));
        Assert.Equal(VisaStatus.ErrorNoListeners, thrown.Status);
    }
}

using VisaCs.Internal;
using VisaCs.Lan;
using VisaCs.Lan.Rpc;

namespace VisaCs.Tests;

/// <summary>The translation of VisaCs.Lan exceptions into the VISA statuses the TCPIP and VICP sessions report.</summary>
public class LanErrorMappingTests
{
    public static TheoryData<LanError, VisaStatus> LanErrors => new()
    {
        { LanError.ConnectionLost, VisaStatus.ErrorConnectionLost },
        { LanError.HostNotFound, VisaStatus.ErrorResourceNotFound },
        { LanError.ConnectionRefused, VisaStatus.ErrorResourceNotFound },
        { LanError.DeviceNotFound, VisaStatus.ErrorResourceNotFound },
        { LanError.ProtocolError, VisaStatus.ErrorIO },
        { LanError.ServerError, VisaStatus.ErrorIO },
        { LanError.NotSupported, VisaStatus.ErrorOperationNotSupported },
        { LanError.InvalidRequest, VisaStatus.ErrorInvalidParameter },
        { LanError.Locked, VisaStatus.ErrorResourceLocked },
        { LanError.NotLocked, VisaStatus.ErrorSessionNotLocked },
        { LanError.Busy, VisaStatus.ErrorResourceBusy },
        { LanError.Aborted, VisaStatus.ErrorAbort },
        { LanError.OutOfResources, VisaStatus.ErrorAllocation },
        { LanError.Security, VisaStatus.ErrorNoPermission },
        { LanError.SystemError, VisaStatus.ErrorSystemError },
    };

    [Fact]
    public void CoversEveryLanError() =>
        Assert.Equal(Enum.GetValues<LanError>().Order(), LanErrors.Select(row => (LanError)row[0]).Order());

    [Theory]
    [MemberData(nameof(LanErrors))]
    public void MapsLanErrors(LanError error, VisaStatus status)
    {
        var lan = new LanException(error, "failed") { NativeError = 10061 };
        var visa = Assert.IsType<VisaException>(LanErrorMapping.Map(lan));
        Assert.Equal(status, visa.Status);
        Assert.Equal(10061, visa.NativeError);
        Assert.Same(lan, visa.InnerException);
        Assert.Contains("failed", visa.Message);
    }

    public static TheoryData<Vxi11Error, VisaStatus> Vxi11Errors => new()
    {
        { Vxi11Error.Syntax, VisaStatus.ErrorInvalidParameter },
        { Vxi11Error.DeviceNotAccessible, VisaStatus.ErrorResourceNotFound },
        { Vxi11Error.InvalidLinkIdentifier, VisaStatus.ErrorConnectionLost },
        { Vxi11Error.Parameter, VisaStatus.ErrorInvalidParameter },
        { Vxi11Error.ChannelNotEstablished, VisaStatus.ErrorConnectionLost },
        { Vxi11Error.OperationNotSupported, VisaStatus.ErrorOperationNotSupported },
        { Vxi11Error.OutOfResources, VisaStatus.ErrorAllocation },
        { Vxi11Error.DeviceLocked, VisaStatus.ErrorResourceLocked },
        { Vxi11Error.NoLockHeld, VisaStatus.ErrorSessionNotLocked },
        { Vxi11Error.IOTimeout, VisaStatus.ErrorTimeout },
        { Vxi11Error.IOError, VisaStatus.ErrorIO },
        { Vxi11Error.InvalidAddress, VisaStatus.ErrorResourceNotFound },
        { Vxi11Error.Abort, VisaStatus.ErrorAbort },
        { Vxi11Error.ChannelAlreadyEstablished, VisaStatus.ErrorIO },
        { (Vxi11Error)99, VisaStatus.ErrorIO },
    };

    [Theory]
    [MemberData(nameof(Vxi11Errors))]
    public void MapsVxi11Codes(Vxi11Error code, VisaStatus status)
    {
        // As the VXI-11 session reports a code, and as a Vxi11Exception built directly.
        var fromSession = LanErrorMapping.FromVxi11(code, "device_write");
        var fromException = LanErrorMapping.Map(new Vxi11Exception(code));
        foreach (var mapped in new[] { fromSession, fromException })
        {
            var visa = Assert.IsAssignableFrom<VisaException>(mapped);
            Assert.Equal(status, visa.Status);
            Assert.Equal(code == Vxi11Error.IOTimeout, visa is VisaTimeoutException);
            Assert.NotNull(visa.InnerException);
        }
        Assert.Contains("device_write", fromSession.Message);
        // The LAN library's own classification agrees with the VISA status.
        if (code != Vxi11Error.IOTimeout)
            Assert.Equal(status, LanErrorMapping.ToStatus(Vxi11Exception.ErrorFor(code)));
    }

    [Theory]
    [InlineData(RpcAcceptStatus.ProgramUnavailable, VisaStatus.ErrorResourceNotFound)]
    [InlineData(RpcAcceptStatus.ProgramMismatch, VisaStatus.ErrorResourceNotFound)]
    [InlineData(RpcAcceptStatus.ProcedureUnavailable, VisaStatus.ErrorOperationNotSupported)]
    [InlineData(RpcAcceptStatus.GarbageArguments, VisaStatus.ErrorInvalidParameter)]
    [InlineData(RpcAcceptStatus.SystemError, VisaStatus.ErrorIO)]
    public void MapsRpcAcceptStatuses(RpcAcceptStatus acceptStatus, VisaStatus status) =>
        Assert.Equal(status, Assert.IsType<VisaException>(LanErrorMapping.Map(new RpcException(acceptStatus))).Status);

    [Theory]
    [InlineData(LanSecurityFailure.TlsNegotiation, SecurityFailure.TlsNegotiation)]
    [InlineData(LanSecurityFailure.ServerCertificate, SecurityFailure.ServerCertificate)]
    [InlineData(LanSecurityFailure.Authentication, SecurityFailure.Authentication)]
    [InlineData(LanSecurityFailure.Policy, SecurityFailure.Policy)]
    public void MapsSecurityFailures(LanSecurityFailure failure, SecurityFailure expected)
    {
        var lan = new LanSecurityException(failure, "denied");
        var visa = Assert.IsType<VisaSecurityException>(LanErrorMapping.Map(lan));
        Assert.Equal(VisaStatus.ErrorNoPermission, visa.Status);
        Assert.Equal(expected, visa.Failure);
        Assert.Same(lan, visa.InnerException);
    }

    [Fact]
    public void MapsTimeoutsWithTheTransferredCount()
    {
        var lan = new LanTimeoutException("slow") { BytesTransferred = 17 };
        var visa = Assert.IsType<VisaTimeoutException>(LanErrorMapping.Map(lan));
        Assert.Equal(VisaStatus.ErrorTimeout, visa.Status);
        Assert.Equal(17, visa.BytesTransferred);
        Assert.Same(lan, visa.InnerException);
    }

    [Fact]
    public void LeavesOtherExceptionsAlone()
    {
        var other = new InvalidOperationException();
        Assert.Same(other, LanErrorMapping.Map(other));
        Assert.False(LanErrorMapping.TryMap(new VisaException(VisaStatus.ErrorIO), out _));
    }
}

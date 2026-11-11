using VisaCs.Pci;

namespace VisaCs.Tests;

/// <summary>The VisaCs.Pci API on its own, without a ResourceManager or PxiSession, against the fake sysfs tree.</summary>
public sealed class PciRegionTests(FakePciFixture fixture) : IClassFixture<FakePciFixture>
{
    private PciDeviceInfo Device(string address = "0000:03:12.0") => PciDeviceInfo.FromAddress(address, fixture.Root)!;

    [Fact]
    public void EnumeratesAndDescribesDevices()
    {
        if (!fixture.Available) return;
        var devices = PciDeviceInfo.Enumerate(fixture.Root);
        Assert.Equal(["0000:00:01.0", "0000:03:12.0", "0000:03:13.0"], devices.Select(d => d.Address));
        var device = Device();
        Assert.Equal((0x1234, 0xABCD, 0x118000u), (device.VendorId, device.DeviceId, device.ClassCode));
        Assert.True(device.IsAvailableToUserSpace);
        Assert.Equal(new PciBar(0, PciBarType.Memory, 0xF7000000, 4096, false, false), device.Bars[0]);
        Assert.Equal(PciBarType.IO, device.Bars[1].Type);
        Assert.Null(PciDeviceInfo.FromAddress("0000:09:09.0", fixture.Root));
        if (fixture.BoundDeviceHasDriver) Assert.Equal("e1000", Device("0000:00:01.0").Driver);
    }

    [Fact]
    public void ReadsConfigurationSpace()
    {
        if (!fixture.Available) return;
        using var config = Device().OpenConfigurationSpace();
        Assert.Equal((256L, 8, (nint)0), (config.Size, config.MaxWidth, config.Address));
        Assert.Equal(0x1234, config.Read16(0));
        Assert.Equal(0xABCD1234u, config.Read32(0));
        Assert.Equal(0xABCDUL, config.Read(2, 2));
    }

    [Fact]
    public void AccessesMemoryAndIoBars()
    {
        if (!fixture.Available) return;
        var device = Device();
        using (var bar0 = device.OpenBar(0))
        {
            Assert.Equal((4096L, 8), (bar0.Size, bar0.MaxWidth));
            Assert.NotEqual(0, bar0.Address);
            bar0.Write32(0x800, 0xDEADBEEF);
            Assert.Equal(0xDEADBEEFu, bar0.Read32(0x800));
            Assert.Equal(0xEF, bar0.Read8(0x800)); // little endian in memory
            bar0.Write64(0x808, 0x0102030405060708);
            Assert.Equal(0x0102030405060708UL, bar0.Read64(0x808));
            bar0.Write(0x810, 2, 0xBEEF);
            Assert.Equal(0xBEEF, bar0.Read16(0x810));
        }
        using var bar1 = device.OpenBar(1);
        Assert.Equal((256L, 4, (nint)0), (bar1.Size, bar1.MaxWidth, bar1.Address));
        bar1.Write16(0x20, 0x5AA5);
        Assert.Equal(0x5AA5, bar1.Read16(0x20));
        Assert.Equal(VisaStatus.ErrorWidthNotSupported, Assert.Throws<VisaException>(() => bar1.Read64(0)).Status);
    }

    [Fact]
    public void RejectsInvalidAccesses()
    {
        if (!fixture.Available) return;
        var device = Device();
        Assert.Equal(VisaStatus.ErrorInvalidSpace, Assert.Throws<VisaException>(() => device.OpenBar(2)).Status);
        Assert.Throws<ArgumentOutOfRangeException>(() => device.OpenBar(6));
        var bar0 = device.OpenBar(0);
        Assert.Equal(VisaStatus.ErrorInvalidOffset, Assert.Throws<VisaException>(() => bar0.Read32(4096)).Status);
        Assert.Equal(VisaStatus.ErrorInvalidOffset, Assert.Throws<VisaException>(() => bar0.Write64(4096, 0)).Status);
        Assert.Equal(VisaStatus.ErrorAlignmentOffsetNotSupported, Assert.Throws<VisaException>(() => bar0.Read32(2)).Status);
        Assert.Equal(VisaStatus.ErrorAlignmentOffsetNotSupported, Assert.Throws<VisaException>(() => bar0.Read32(-4)).Status);
        Assert.Equal(VisaStatus.ErrorWidthNotSupported, Assert.Throws<VisaException>(() => bar0.Read(0, 3)).Status);
        bar0.Dispose();
        bar0.Dispose(); // a second Dispose does nothing
        Assert.Equal(VisaStatus.ErrorInvalidObject, Assert.Throws<VisaException>(() => bar0.Read32(0)).Status);
        var config = device.OpenConfigurationSpace();
        config.Dispose();
        Assert.Equal(VisaStatus.ErrorInvalidObject, Assert.Throws<VisaException>(() => config.Read16(0)).Status);
        Assert.Equal(VisaStatus.ErrorInvalidObject, Assert.Throws<VisaException>(() => config.Write16(0x40, 1)).Status);
    }

    [Fact]
    public void ConfigurationWritesNeedWriteAccess()
    {
        if (!fixture.Available || Environment.UserName == "root") return; // root may write the read-only file
        using var config = Device("0000:03:13.0").OpenConfigurationSpace();
        Assert.Equal(0x1234, config.Read16(0));
        Assert.Equal(VisaStatus.ErrorNoPermission, Assert.Throws<VisaException>(() => config.Write16(0x40, 1)).Status);
    }
}

using System.Buffers.Binary;

namespace VisaCs.Tests;

public class PxiResourceNameTests
{
    [Theory]
    [InlineData("PXI0::3-18.0::INSTR", 0, 3, 18, 0, "PXI0::3-18.0::INSTR")]
    [InlineData("PXI::3-18", 0, 3, 18, 0, "PXI0::3-18.0::INSTR")]
    [InlineData("pxi1::4-2.3::instr", 0, 4, 2, 3, "PXI1::4-2.3::INSTR")]
    [InlineData("PXI3::18::INSTR", 0, 3, 18, 0, "PXI0::3-18.0::INSTR")]
    [InlineData("PXI3::18::2", 0, 3, 18, 2, "PXI0::3-18.2::INSTR")]
    [InlineData("PXI0::1a2b:5-0.0::INSTR", 0x1a2b, 5, 0, 0, "PXI0::1a2b:5-0.0::INSTR")]
    public void ParsesBusDeviceFunction(string text, int domain, int bus, int device, int function, string canonical)
    {
        var name = Assert.IsType<PxiResourceName>(ResourceName.Parse(text));
        Assert.Equal((domain, bus, device, function), (name.Domain, name.Bus, name.Device, name.Function));
        Assert.Equal(canonical, name.Canonical);
        Assert.Equal(HardwareInterfaceType.Pxi, name.InterfaceType);
    }

    [Fact]
    public void ParsesChassisAndSlot()
    {
        var name = Assert.IsType<PxiResourceName>(ResourceName.Parse("PXI0::CHASSIS1::SLOT4::FUNC1::INSTR"));
        Assert.Equal((1, 4, 1), (name.Chassis, name.Slot, name.Function));
        Assert.Equal("PXI0::CHASSIS1::SLOT4::FUNC1::INSTR", name.Canonical);
        Assert.Equal("0000:03:12.0", ((PxiResourceName)ResourceName.Parse("PXI0::3-18.0::INSTR")).PciAddress);
    }

    [Theory]
    [InlineData("PXI0::3-40.0::INSTR")]
    [InlineData("PXI0::3-1.9::INSTR")]
    [InlineData("PXI0::CHASSIS1::INSTR")]
    [InlineData("PXI0::a-b::INSTR")]
    public void RejectsInvalidNames(string text) => Assert.False(ResourceName.TryParse(text, out _));
}

/// <summary>
/// A fake sysfs PCI device: config space, a 4 KiB memory BAR0 and a 256-byte I/O BAR1, all regular files. A second
/// function (0000:03:13.0) has a read-only configuration file, like sysfs for a user other than root, and a third one
/// is bound to a kernel driver.
/// </summary>
public sealed class FakePciFixture : IDisposable
{
    public FakePciFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "visacs-pci-" + Guid.NewGuid().ToString("N"));
        DevicePath = Path.Combine(Root, "0000:03:12.0");
        // sysfs names contain ':', which Windows does not allow in file names; PXI access is Linux only anyway.
        Available = OperatingSystem.IsLinux();
        if (!Available) return;
        Directory.CreateDirectory(DevicePath);
        File.WriteAllText(Path.Combine(DevicePath, "vendor"), "0x1234\n");
        File.WriteAllText(Path.Combine(DevicePath, "device"), "0xabcd\n");
        File.WriteAllText(Path.Combine(DevicePath, "subsystem_vendor"), "0x1234\n");
        File.WriteAllText(Path.Combine(DevicePath, "subsystem_device"), "0x0001\n");
        File.WriteAllText(Path.Combine(DevicePath, "class"), "0x118000\n");
        File.WriteAllText(Path.Combine(DevicePath, "max_link_width"), "4\n");
        File.WriteAllText(Path.Combine(DevicePath, "current_link_width"), "1\n");
        File.WriteAllText(Path.Combine(DevicePath, "resource"),
            "0x00000000f7000000 0x00000000f7000fff 0x0000000000040200\n" +
            "0x000000000000e000 0x000000000000e0ff 0x0000000000040101\n" +
            string.Concat(Enumerable.Repeat("0x0000000000000000 0x0000000000000000 0x0000000000000000\n", 5)));
        var config = new byte[256];
        BinaryPrimitives.WriteUInt16LittleEndian(config, 0x1234);
        BinaryPrimitives.WriteUInt16LittleEndian(config.AsSpan(2), 0xABCD);
        File.WriteAllBytes(Path.Combine(DevicePath, "config"), config);
        File.WriteAllBytes(Path.Combine(DevicePath, "resource0"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(DevicePath, "resource1"), new byte[256]);

        var readOnly = Path.Combine(Root, "0000:03:13.0");
        Directory.CreateDirectory(readOnly);
        File.WriteAllText(Path.Combine(readOnly, "vendor"), "0x1234\n");
        File.WriteAllText(Path.Combine(readOnly, "device"), "0xabce\n");
        File.WriteAllBytes(Path.Combine(readOnly, "config"), config);
        File.SetUnixFileMode(Path.Combine(readOnly, "config"), UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        // A second function bound to a kernel driver: not listed by Find.
        var bound = Path.Combine(Root, "0000:00:01.0");
        Directory.CreateDirectory(bound);
        File.WriteAllText(Path.Combine(bound, "vendor"), "0x8086\n");
        File.WriteAllText(Path.Combine(bound, "device"), "0x1111\n");
        Directory.CreateDirectory(Path.Combine(Root, "drivers", "e1000"));
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(bound, "driver"), Path.Combine(Root, "drivers", "e1000"));
            BoundDeviceHasDriver = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Creating symbolic links may need privileges on Windows.
        }
    }

    public bool Available { get; }
    public string Root { get; }
    public string DevicePath { get; }
    public bool BoundDeviceHasDriver { get; }

    public ResourceManager CreateResourceManager() => new(new ResourceManagerOptions
    {
        PciDevicesPath = Root,
        EnableLanDiscovery = false,
        EnableMdnsDiscovery = false,
        EnableUsb = false,
        EnableSerial = false,
        EnableGpib = false,
    });

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* files may still be mapped */ }
    }
}

public sealed class PxiSessionTests(FakePciFixture fixture) : IClassFixture<FakePciFixture>
{
    private const string Resource = "PXI0::3-18.0::INSTR";

    [Fact]
    public void FindsUserSpaceDevices()
    {
        if (!fixture.Available) return;
        using var rm = fixture.CreateResourceManager();
        var found = rm.FindResources("PXI?*");
        var info = Assert.Single(found, r => r.ResourceName == Resource);
        Assert.Equal("PCI 1234:ABCD", info.Description);
        if (fixture.BoundDeviceHasDriver) Assert.DoesNotContain(found, r => r.ResourceName == "PXI0::0-1.0::INSTR");
        Assert.Single(rm.Find("PXI?*{VI_ATTR_MANF_ID==0x1234 && VI_ATTR_PXI_DEV_NUM==18}"));
    }

    [Fact]
    public void ReadsConfigurationSpaceAndAttributes()
    {
        if (!fixture.Available) return;
        using var rm = fixture.CreateResourceManager();
        using var session = rm.Open<PxiSession>(Resource);
        Assert.Equal(0x1234, session.In16(AddressSpace.PxiConfig, 0));
        Assert.Equal(0xABCD, session.In16(AddressSpace.PxiConfig, 2));
        Assert.Equal(0xABCD1234u, session.In32(AddressSpace.PxiConfig, 0));
        Assert.Equal(0x1234, session.GetAttribute<int>(VisaAttribute.ManufacturerId));
        Assert.Equal(0xABCD, session.GetAttribute<int>(VisaAttribute.ModelCode));
        Assert.Equal(3, session.GetAttribute<int>(VisaAttribute.PxiBusNumber));
        Assert.Equal(18, session.GetAttribute<int>(VisaAttribute.PxiDeviceNumber));
        Assert.Equal((int)PciBarType.Memory, session.GetAttribute<int>(VisaAttribute.PxiMemoryTypeBar0));
        Assert.Equal((int)PciBarType.IO, session.GetAttribute<int>(VisaAttribute.PxiMemoryTypeBar1));
        Assert.Equal((int)PciBarType.None, session.GetAttribute<int>(VisaAttribute.PxiMemoryTypeBar2));
        Assert.Equal(4096L, session.GetAttribute<long>(VisaAttribute.PxiMemorySizeBar0));
        Assert.Equal(0xF7000000L, session.GetAttribute<long>(VisaAttribute.PxiMemoryBaseBar0));
        Assert.True(session.GetAttribute<bool>(VisaAttribute.PxiIsExpress));
        Assert.Equal(1, session.GetAttribute<int>(VisaAttribute.PxiActualLinkWidth));
        Assert.Equal(PciBarType.IO, session.GetBar(1).Type);
    }

    [Fact]
    public void ReadsAndWritesMemoryBars()
    {
        if (!fixture.Available) return;
        using var rm = fixture.CreateResourceManager();
        using var session = rm.Open<PxiSession>(Resource);
        session.Out32(AddressSpace.PxiBar0, 0x10, 0xDEADBEEF);
        Assert.Equal(0xDEADBEEFu, session.In32(AddressSpace.PxiBar0, 0x10));
        Assert.Equal(0xEF, session.In8(AddressSpace.PxiBar0, 0x10)); // little endian in memory
        Assert.Equal(0xBEEF, session.In16(AddressSpace.PxiBar0, 0x10));
        session.Out64(AddressSpace.PxiBar0, 0x20, 0x0102030405060708);
        Assert.Equal(0x0102030405060708UL, session.In64(AddressSpace.PxiBar0, 0x20));

        session.SourceByteOrder = ByteOrder.BigEndian;
        Assert.Equal(0xEFBEADDEu, session.In32(AddressSpace.PxiBar0, 0x10));
        session.SetAttribute(VisaAttribute.SourceByteOrder, ByteOrder.LittleEndian);
        session.DestinationByteOrder = ByteOrder.BigEndian;
        session.Out16(AddressSpace.PxiBar0, 0x30, 0x1122);
        Assert.Equal(0x2211, session.In16(AddressSpace.PxiBar0, 0x30));
        session.DestinationByteOrder = ByteOrder.LittleEndian;
    }

    [Fact]
    public void MovesBlocksAndFifos()
    {
        if (!fixture.Available) return;
        using var rm = fixture.CreateResourceManager();
        using var session = rm.Open<PxiSession>(Resource);
        uint[] data = [1, 2, 3, 4, 0xFFFFFFFF];
        session.MoveOut32(AddressSpace.PxiBar0, 0x100, data);
        var back = new uint[5];
        session.MoveIn32(AddressSpace.PxiBar0, 0x100, back);
        Assert.Equal(data, back);

        // FIFO register: every element reads the same address.
        session.SourceIncrement = false;
        var fifo = new uint[3];
        session.MoveIn32(AddressSpace.PxiBar0, 0x104, fifo);
        Assert.Equal(new uint[] { 2, 2, 2 }, fifo);
        session.SourceIncrement = true;

        session.SetAttribute(VisaAttribute.DestinationIncrement, 0);
        session.MoveOut16(AddressSpace.PxiBar0, 0x200, new ushort[] { 7, 8, 9 });
        Assert.Equal(9, session.In16(AddressSpace.PxiBar0, 0x200));
        Assert.Equal(0, session.In16(AddressSpace.PxiBar0, 0x202));
        session.DestinationIncrement = true;

        var bytes = new byte[4];
        session.MoveIn8(AddressSpace.PxiBar0, 0x100, bytes);
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, bytes);
    }

    [Fact]
    public void AccessesIoBars()
    {
        if (!fixture.Available) return;
        using var rm = fixture.CreateResourceManager();
        using var session = rm.Open<PxiSession>(Resource);
        session.Out8(AddressSpace.PxiBar1, 3, 0x5A);
        Assert.Equal(0x5A, session.In8(AddressSpace.PxiBar1, 3));
        session.Out32(AddressSpace.PxiBar1, 8, 0x01020304);
        Assert.Equal(0x01020304u, session.In32(AddressSpace.PxiBar1, 8));
    }

    [Fact]
    public void MapsWindows()
    {
        if (!fixture.Available) return;
        using var rm = fixture.CreateResourceManager();
        using var session = rm.Open<PxiSession>(Resource);
        using (var window = session.MapAddress(AddressSpace.PxiBar0, 0x400, 0x100))
        {
            Assert.NotEqual(0, window.Address);
            window.Poke32(0x10, 0xCAFEF00D);
            Assert.Equal(0xCAFEF00Du, window.Peek32(0x10));
            Assert.Equal(0xCAFEF00Du, session.In32(AddressSpace.PxiBar0, 0x410));
            unsafe
            {
                Assert.Equal(0xCAFEF00Du, *(uint*)(window.Address + 0x10)); // directly dereferenceable
            }
            Assert.Equal(0x400L, session.GetAttribute<long>(VisaAttribute.WindowBaseAddress));
            Assert.Equal(VisaStatus.ErrorWindowMapped, Assert.Throws<VisaException>(() => session.MapAddress(AddressSpace.PxiBar0, 0, 4)).Status);
            Assert.Equal(VisaStatus.ErrorInvalidOffset, Assert.Throws<VisaException>(() => window.Peek32(0x100)).Status);
        }
        Assert.Null(session.Window);
        Assert.Equal(VisaStatus.ErrorWindowNotMapped, Assert.Throws<VisaException>(session.UnmapAddress).Status);
        using var config = session.MapAddress(AddressSpace.PxiConfig, 0, 64);
        Assert.Equal(0, config.Address); // configuration space is accessed with reads/writes, not a mapping
        Assert.Equal(0x1234, config.Peek16(0));
    }

    [Fact]
    public void ReportsInvalidAccesses()
    {
        if (!fixture.Available) return;
        using var rm = fixture.CreateResourceManager();
        using var session = rm.Open<PxiSession>(Resource);
        Assert.Equal(VisaStatus.ErrorInvalidOffset, Assert.Throws<VisaException>(() => session.In32(AddressSpace.PxiBar0, 4096)).Status);
        Assert.Equal(VisaStatus.ErrorAlignmentOffsetNotSupported, Assert.Throws<VisaException>(() => session.In32(AddressSpace.PxiBar0, 2)).Status);
        Assert.Equal(VisaStatus.ErrorInvalidSpace, Assert.Throws<VisaException>(() => session.In32(AddressSpace.PxiBar3, 0)).Status);
        Assert.Equal(VisaStatus.ErrorInvalidOffset,
            Assert.Throws<VisaException>(() => session.MoveIn32(AddressSpace.PxiBar1, 248, new uint[3])).Status);
        Assert.Equal(VisaStatus.ErrorResourceNotFound, Assert.Throws<VisaException>(() => rm.Open<PxiSession>("PXI0::9-9.0::INSTR")).Status);
        Assert.Equal(VisaStatus.ErrorResourceNotFound,
            Assert.Throws<VisaException>(() => rm.Open<PxiSession>("PXI0::CHASSIS1::SLOT2::INSTR")).Status);
        Assert.Equal(VisaStatus.ErrorInvalidObject, Assert.Throws<VisaException>(() => rm.Open(Resource)).Status); // not message based
    }

    [Fact]
    public void HonorsLocks()
    {
        if (!fixture.Available) return;
        using var rm = fixture.CreateResourceManager();
        using var a = rm.Open<PxiSession>(Resource);
        using var b = (PxiSession)rm.Open<VisaSession>(Resource);
        a.LockExclusive(1000);
        Assert.Equal(VisaStatus.ErrorResourceLocked, Assert.Throws<VisaException>(() => b.In16(AddressSpace.PxiConfig, 0)).Status);
        a.Unlock();
        Assert.Equal(0x1234, b.In16(AddressSpace.PxiConfig, 0));
    }

    [Fact]
    public void RefusesFunctionsOwnedByKernelDrivers()
    {
        if (!fixture.Available || !fixture.BoundDeviceHasDriver) return;
        using var rm = fixture.CreateResourceManager();
        var error = Assert.Throws<VisaException>(() => rm.Open<PxiSession>("PXI0::0-1.0::INSTR"));
        Assert.Equal(VisaStatus.ErrorResourceBusy, error.Status);
        Assert.Contains("e1000", error.Message);
    }

    [Fact]
    public void ReadsConfigurationSpaceWithoutWriteAccess()
    {
        if (!fixture.Available) return;
        using var rm = fixture.CreateResourceManager();
        using var session = rm.Open<PxiSession>("PXI0::3-19.0::INSTR");
        Assert.Equal(0x1234, session.In16(AddressSpace.PxiConfig, 0));
        Assert.Equal(0xABCD, session.In16(AddressSpace.PxiConfig, 2));
        if (Environment.UserName == "root") return; // root may write the read-only file
        var error = Assert.Throws<VisaException>(() => session.Out16(AddressSpace.PxiConfig, 0x40, 1));
        Assert.Equal(VisaStatus.ErrorNoPermission, error.Status);
        Assert.Equal(0x1234, session.In16(AddressSpace.PxiConfig, 0));
    }

    [Fact]
    public void RejectsUnsupportedWidthsAndSizes()
    {
        if (!fixture.Available) return;
        using var rm = fixture.CreateResourceManager();
        using var session = rm.Open<PxiSession>(Resource);
        // Linux accesses I/O ports 1, 2 or 4 bytes at a time.
        Assert.Equal(VisaStatus.ErrorWidthNotSupported, Assert.Throws<VisaException>(() => session.In64(AddressSpace.PxiBar1, 8)).Status);
        Assert.Equal(VisaStatus.ErrorWidthNotSupported,
            Assert.Throws<VisaException>(() => session.MoveOut64(AddressSpace.PxiBar1, 0, new ulong[2])).Status);
        Assert.Equal(VisaStatus.ErrorInvalidSize, Assert.Throws<VisaException>(() => session.MapAddress(AddressSpace.PxiBar0, 0, 0)).Status);
        using var window = session.MapAddress(AddressSpace.PxiBar1, 0, 16);
        Assert.Equal(VisaStatus.ErrorWidthNotSupported, Assert.Throws<VisaException>(() => window.Peek64(0)).Status);
        window.Poke32(4, 0x11223344);
        Assert.Equal(0x11223344u, window.Peek32(4));
    }

    [Fact]
    public async Task ClosesWhileOtherThreadsAccessRegisters()
    {
        if (!fixture.Available) return;
        using var rm = fixture.CreateResourceManager();
        for (int round = 0; round < 50; round++)
        {
            var session = rm.Open<PxiSession>(Resource);
            var window = session.MapAddress(AddressSpace.PxiBar0, 0, 0x100);
            using var started = new SemaphoreSlim(0);
            var accesses = Enumerable.Range(0, 2).Select(worker => Task.Run(() =>
            {
                started.Release();
                for (int i = 0; ; i++)
                {
                    try
                    {
                        if (worker == 0) session.Out32(AddressSpace.PxiBar0, 0x80, (uint)i);
                        else _ = window.Peek32(0x80);
                    }
                    catch (VisaException ex) when (ex.Status is VisaStatus.ErrorInvalidObject or VisaStatus.ErrorWindowNotMapped)
                    {
                        return; // closed
                    }
                }
            })).ToList();
            await started.WaitAsync();
            await started.WaitAsync();
            session.Dispose();
            await Task.WhenAll(accesses).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(VisaStatus.ErrorInvalidObject, Assert.Throws<VisaException>(() => session.In32(AddressSpace.PxiBar0, 0)).Status);
            Assert.Equal(VisaStatus.ErrorWindowNotMapped, Assert.Throws<VisaException>(() => window.Peek32(0)).Status);
        }
    }

    [Fact]
    public void RefusesDefaultPathOffLinux()
    {
        if (OperatingSystem.IsLinux()) return;
        using var rm = new ResourceManager();
        Assert.Equal(VisaStatus.ErrorResourceNotFound, Assert.Throws<VisaException>(() => rm.Open<VisaSession>(Resource)).Status);
    }

    /// <summary>Reads real PCI configuration space through sysfs (Linux, root, VISACS_TEST_PCI=1). Read only.</summary>
    [Fact]
    public void ReadsRealConfigurationSpace()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("VISACS_TEST_PCI") != "1") return;
        var devices = PciDeviceInfo.Enumerate();
        static string NameOf(PciDeviceInfo d) => $"PXI0::{(d.Domain == 0 ? "" : $"{d.Domain:x4}:")}{d.Bus}-{d.Device}.{d.Function}::INSTR";
        using var rm = new ResourceManager();
        // Functions that a kernel driver owns are refused.
        if (devices.FirstOrDefault(d => !d.IsAvailableToUserSpace) is { } owned)
            Assert.Equal(VisaStatus.ErrorResourceBusy, Assert.Throws<VisaException>(() => rm.Open<PxiSession>(NameOf(owned))).Status);
        if (devices.FirstOrDefault(d => d.IsAvailableToUserSpace) is not { } device) return; // nothing user space may open
        using var session = rm.Open<PxiSession>(NameOf(device));
        Assert.Equal(device.VendorId, session.In16(AddressSpace.PxiConfig, 0));
        Assert.Equal(device.DeviceId, session.In16(AddressSpace.PxiConfig, 2));
        Assert.Equal(device.ClassCode, session.In32(AddressSpace.PxiConfig, 8) >> 8);
    }
}

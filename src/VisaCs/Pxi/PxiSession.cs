using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VisaCs.Pxi;

namespace VisaCs;

/// <summary>
/// A PXI / PCI(e) register-based instrument session (viIn/viOut, viMoveIn/viMoveOut, viMapAddress/viPeek/viPoke).
/// Implemented on Linux through sysfs: configuration space via the <c>config</c> file and BARs via the
/// <c>resourceN</c> files. Windows and macOS need a vendor kernel driver and are not supported.
/// </summary>
/// <remarks>
/// <para>
/// Only functions that no kernel driver owns, or that are bound to vfio-pci, uio_pci_generic or pci-stub, can be opened
/// (<see cref="PciDeviceInfo.IsAvailableToUserSpace"/>); others fail with <see cref="VisaStatus.ErrorResourceBusy"/>.
/// The first 64 bytes of configuration space are readable by any user; the rest of it, writes and the BARs need root or
/// a udev rule (<see cref="VisaStatus.ErrorNoPermission"/> otherwise).
/// </para>
/// <para>
/// Register accesses check the address space (<see cref="VisaStatus.ErrorInvalidSpace"/> for a BAR the device does not
/// implement), the alignment to the access width (<see cref="VisaStatus.ErrorAlignmentOffsetNotSupported"/>), the
/// bounds (<see cref="VisaStatus.ErrorInvalidOffset"/>) and the width (I/O BARs: at most 32 bits,
/// <see cref="VisaStatus.ErrorWidthNotSupported"/>). They apply the byte-order attributes and may run on several
/// threads at once; closing the session while one runs lets it finish, and later ones throw
/// <see cref="VisaStatus.ErrorInvalidObject"/>.
/// </para>
/// </remarks>
public sealed class PxiSession : VisaSession
{
    private readonly PxiResourceName _name;
    private readonly Dictionary<AddressSpace, IPciRegion> _regions = new(); // guarded by itself, as are _window and _closed
    private PciDeviceInfo? _device;
    private PxiWindow? _window;
    private bool _closed;

    internal PxiSession(ResourceManager manager, PxiResourceName name) : base(manager, name) => _name = name;

    /// <summary>The PCI function behind the session.</summary>
    public PciDeviceInfo Device => _device ?? throw new VisaException(VisaStatus.ErrorInvalidObject, "The session is not open.");

    /// <summary>PCI bus number (VI_ATTR_PXI_BUS_NUM).</summary>
    public int Bus => Device.Bus;
    /// <summary>PCI device number (VI_ATTR_PXI_DEV_NUM).</summary>
    public int DeviceNumber => Device.Device;
    /// <summary>PCI function number (VI_ATTR_PXI_FUNC_NUM).</summary>
    public int Function => Device.Function;

    /// <summary>PCI vendor ID (VI_ATTR_MANF_ID).</summary>
    public ushort ManufacturerId => Device.VendorId;

    /// <summary>PCI device ID (VI_ATTR_MODEL_CODE).</summary>
    public ushort ModelCode => Device.DeviceId;

    /// <summary>
    /// Whether block reads advance through addresses (true, the default) or repeatedly read one FIFO register
    /// (VI_ATTR_SRC_INCREMENT).
    /// </summary>
    public bool SourceIncrement { get; set; } = true;

    /// <summary>Whether block writes advance through addresses (true, the default) or repeatedly write one register (VI_ATTR_DEST_INCREMENT).</summary>
    public bool DestinationIncrement { get; set; } = true;

    /// <summary>Byte order of the registers read with In/MoveIn (VI_ATTR_SRC_BYTE_ORDER). Default little endian, the PCI order.</summary>
    public ByteOrder SourceByteOrder { get; set; } = ByteOrder.LittleEndian;

    /// <summary>Byte order of the registers written with Out/MoveOut (VI_ATTR_DEST_BYTE_ORDER). Default little endian.</summary>
    public ByteOrder DestinationByteOrder { get; set; } = ByteOrder.LittleEndian;

    /// <summary>Byte order of Peek/Poke through a mapped window (VI_ATTR_WIN_BYTE_ORDER). Default little endian.</summary>
    public ByteOrder WindowByteOrder { get; set; } = ByteOrder.LittleEndian;

    /// <summary>The currently mapped window, if any.</summary>
    public PxiWindow? Window
    {
        get
        {
            lock (_regions) return _window;
        }
    }

    private protected override ValueTask OpenCoreAsync(int connectTimeoutMs, CancellationToken cancellationToken, bool async)
    {
        string root = ResourceManager.Options.PciDevicesPath;
        if (!OperatingSystem.IsLinux())
            throw new VisaException(VisaStatus.ErrorResourceNotFound,
                "PXI/PCI register access needs a vendor kernel driver on this operating system; VisaCs supports it on Linux through sysfs.");
        if (_name.Chassis is not null)
            throw new VisaException(VisaStatus.ErrorResourceNotFound,
                "PXI chassis/slot names need a chassis description (pxisys.ini); use the bus-device.function form, e.g. PXI0::3-18.0::INSTR.");
        string path = Path.Combine(root, _name.PciAddress);
        var device = Directory.Exists(path) ? PciDeviceInfo.Read(path) : null;
        if (device is null)
            throw new VisaException(VisaStatus.ErrorResourceNotFound, $"PCI device {_name.PciAddress} was not found in {root}.");
        if (!device.IsAvailableToUserSpace)
        {
            throw new VisaException(VisaStatus.ErrorResourceBusy,
                $"PCI device {_name.PciAddress} is owned by the '{device.Driver}' kernel driver. Unbind it, or bind it to vfio-pci, uio_pci_generic or pci-stub.");
        }
        _device = device;
        return default;
    }

    private protected override ValueTask CloseCoreAsync(bool async)
    {
        PxiWindow? window;
        List<IPciRegion> regions;
        lock (_regions)
        {
            _closed = true;
            window = _window;
            regions = [.. _regions.Values];
            _regions.Clear();
        }
        window?.Dispose();
        foreach (var region in regions) region.Dispose(); // waits for running accesses to memory BARs
        return default;
    }

    /// <summary>Describes one BAR (type, address and size); <paramref name="index"/> is 0 to 5.</summary>
    public PciBar GetBar(int index) => Device.Bars[index];

    private IPciRegion Region(AddressSpace space)
    {
        lock (_regions)
        {
            if (_closed) throw PciAccess.Closed();
            if (_regions.TryGetValue(space, out var region)) return region;
            if (space == AddressSpace.PxiConfig)
            {
                region = new PciFileRegion(Path.Combine(Device.Path, "config"), 0, isConfig: true);
            }
            else if (space is >= AddressSpace.PxiBar0 and <= AddressSpace.PxiBar5)
            {
                var bar = Device.Bars[space - AddressSpace.PxiBar0];
                string file = Path.Combine(Device.Path, $"resource{bar.Index}");
                region = bar.Type switch
                {
                    PciBarType.Memory => new PciMappedRegion(file, (long)bar.Size),
                    PciBarType.IO => new PciFileRegion(file, (long)bar.Size, isConfig: false),
                    _ => throw new VisaException(VisaStatus.ErrorInvalidSpace, $"BAR{bar.Index} is not implemented by this device."),
                };
            }
            else
            {
                throw new VisaException(VisaStatus.ErrorInvalidSpace, $"Address space {space} is not valid for PXI.");
            }
            _regions[space] = region;
            return region;
        }
    }

    private IPciRegion Checked(AddressSpace space, long offset, int width, long count = 1)
    {
        EnsureAccess();
        var region = Region(space);
        if (width > region.MaxWidth)
            throw new VisaException(VisaStatus.ErrorWidthNotSupported, $"{width * 8}-bit accesses are not supported in {space}.");
        if (offset < 0 || offset % width != 0)
            throw new VisaException(VisaStatus.ErrorAlignmentOffsetNotSupported, $"Offset 0x{offset:X} is not aligned to {width} bytes.");
        long span = (count <= 0 ? 0 : count - 1) * width + width;
        if (offset + span > region.Size)
            throw new VisaException(VisaStatus.ErrorInvalidOffset, $"Offset 0x{offset:X} (+{span}) is outside the {region.Size}-byte address space.");
        return region;
    }

    internal static ulong Swap(ulong value, int width, ByteOrder order) =>
        (order == ByteOrder.LittleEndian) == BitConverter.IsLittleEndian ? value : width switch
        {
            2 => BinaryPrimitives.ReverseEndianness((ushort)value),
            4 => BinaryPrimitives.ReverseEndianness((uint)value),
            8 => BinaryPrimitives.ReverseEndianness(value),
            _ => value,
        };

    private ulong In(AddressSpace space, long offset, int width) =>
        Swap(Checked(space, offset, width).Read(offset, width), width, SourceByteOrder);

    private void Out(AddressSpace space, long offset, int width, ulong value) =>
        Checked(space, offset, width).Write(offset, width, Swap(value, width, DestinationByteOrder));

    /// <summary>Reads an 8-bit register (viIn8).</summary>
    public byte In8(AddressSpace space, long offset) => (byte)In(space, offset, 1);
    /// <summary>Reads a 16-bit register (viIn16).</summary>
    public ushort In16(AddressSpace space, long offset) => (ushort)In(space, offset, 2);
    /// <summary>Reads a 32-bit register (viIn32).</summary>
    public uint In32(AddressSpace space, long offset) => (uint)In(space, offset, 4);
    /// <summary>Reads a 64-bit register (viIn64); not available in I/O BARs.</summary>
    public ulong In64(AddressSpace space, long offset) => In(space, offset, 8);

    /// <summary>Writes an 8-bit register (viOut8).</summary>
    public void Out8(AddressSpace space, long offset, byte value) => Out(space, offset, 1, value);
    /// <summary>Writes a 16-bit register (viOut16).</summary>
    public void Out16(AddressSpace space, long offset, ushort value) => Out(space, offset, 2, value);
    /// <summary>Writes a 32-bit register (viOut32).</summary>
    public void Out32(AddressSpace space, long offset, uint value) => Out(space, offset, 4, value);
    /// <summary>Writes a 64-bit register (viOut64); not available in I/O BARs.</summary>
    public void Out64(AddressSpace space, long offset, ulong value) => Out(space, offset, 8, value);

    private void MoveIn<T>(AddressSpace space, long offset, Span<T> destination) where T : unmanaged
    {
        int width = Unsafe.SizeOf<T>();
        var region = Checked(space, offset, width, SourceIncrement ? destination.Length : 1);
        var bytes = MemoryMarshal.AsBytes(destination);
        Span<byte> eight = stackalloc byte[8];
        for (int i = 0; i < destination.Length; i++)
        {
            ulong v = Swap(region.Read(SourceIncrement ? offset + (long)i * width : offset, width), width, SourceByteOrder);
            // Write the low 'width' bytes (TryWriteBytes on a narrower span would silently write nothing).
            BinaryPrimitives.WriteUInt64LittleEndian(eight, v);
            eight[..width].CopyTo(bytes.Slice(i * width, width));
        }
    }

    private void MoveOut<T>(AddressSpace space, long offset, ReadOnlySpan<T> source) where T : unmanaged
    {
        int width = Unsafe.SizeOf<T>();
        var region = Checked(space, offset, width, DestinationIncrement ? source.Length : 1);
        var bytes = MemoryMarshal.AsBytes(source);
        Span<byte> eight = stackalloc byte[8];
        for (int i = 0; i < source.Length; i++)
        {
            eight.Clear();
            bytes.Slice(i * width, width).CopyTo(eight);
            ulong v = BitConverter.ToUInt64(eight);
            region.Write(DestinationIncrement ? offset + (long)i * width : offset, width, Swap(v, width, DestinationByteOrder));
        }
    }

    /// <summary>Block read of 8-bit registers (viMoveIn8).</summary>
    public void MoveIn8(AddressSpace space, long offset, Span<byte> destination) => MoveIn(space, offset, destination);
    /// <summary>Block read of 16-bit registers (viMoveIn16).</summary>
    public void MoveIn16(AddressSpace space, long offset, Span<ushort> destination) => MoveIn(space, offset, destination);
    /// <summary>Block read of 32-bit registers (viMoveIn32).</summary>
    public void MoveIn32(AddressSpace space, long offset, Span<uint> destination) => MoveIn(space, offset, destination);
    /// <summary>Block read of 64-bit registers (viMoveIn64); not available in I/O BARs.</summary>
    public void MoveIn64(AddressSpace space, long offset, Span<ulong> destination) => MoveIn(space, offset, destination);

    /// <summary>Block write of 8-bit registers (viMoveOut8).</summary>
    public void MoveOut8(AddressSpace space, long offset, ReadOnlySpan<byte> source) => MoveOut(space, offset, source);
    /// <summary>Block write of 16-bit registers (viMoveOut16).</summary>
    public void MoveOut16(AddressSpace space, long offset, ReadOnlySpan<ushort> source) => MoveOut(space, offset, source);
    /// <summary>Block write of 32-bit registers (viMoveOut32).</summary>
    public void MoveOut32(AddressSpace space, long offset, ReadOnlySpan<uint> source) => MoveOut(space, offset, source);
    /// <summary>Block write of 64-bit registers (viMoveOut64); not available in I/O BARs.</summary>
    public void MoveOut64(AddressSpace space, long offset, ReadOnlySpan<ulong> source) => MoveOut(space, offset, source);

    /// <summary>
    /// Maps <paramref name="size"/> bytes of an address space from <paramref name="offset"/> for direct access
    /// (viMapAddress). Only one window can be mapped per session (<see cref="VisaStatus.ErrorWindowMapped"/>); dispose it
    /// (viUnmapAddress) before mapping another.
    /// </summary>
    public PxiWindow MapAddress(AddressSpace space, long offset, long size)
    {
        if (size <= 0) throw new VisaException(VisaStatus.ErrorInvalidSize, "The window size must be positive.");
        var region = Checked(space, offset, 1, size);
        lock (_regions)
        {
            if (_closed) throw PciAccess.Closed();
            if (_window is not null) throw new VisaException(VisaStatus.ErrorWindowMapped);
            return _window = new PxiWindow(this, region, space, offset, size);
        }
    }

    /// <summary>Unmaps the current window (viUnmapAddress).</summary>
    public void UnmapAddress()
    {
        var window = Window ?? throw new VisaException(VisaStatus.ErrorWindowNotMapped);
        window.Dispose();
    }

    internal void WindowClosed(PxiWindow window)
    {
        lock (_regions)
            if (ReferenceEquals(_window, window)) _window = null;
    }

    private protected override void RegisterAttributes(AttributeTable table)
    {
        base.RegisterAttributes(table);
        table.Add(VisaAttribute.ManufacturerId, () => (int)ManufacturerId);
        table.Add(VisaAttribute.ModelCode, () => (int)ModelCode);
        table.Add(VisaAttribute.PxiBusNumber, () => Bus);
        table.Add(VisaAttribute.PxiDeviceNumber, () => DeviceNumber);
        table.Add(VisaAttribute.PxiFunctionNumber, () => Function);
        table.Add(VisaAttribute.PxiChassis, () => -1);
        table.Add(VisaAttribute.Slot, () => -1);
        table.Add(VisaAttribute.PxiSlotPath, () => "");
        table.Add(VisaAttribute.PxiIsExpress, () => Device.IsExpress);
        table.Add(VisaAttribute.PxiActualLinkWidth, () => Device.CurrentLinkWidth ?? -1);
        table.Add(VisaAttribute.PxiMaxLinkWidth, () => Device.MaxLinkWidth ?? -1);
        for (int i = 0; i < 6; i++)
        {
            int bar = i;
            table.Add(VisaAttribute.PxiMemoryTypeBar0 + (uint)bar, () => (int)Device.Bars[bar].Type);
            table.Add(VisaAttribute.PxiMemoryBaseBar0 + (uint)bar, () => (long)Device.Bars[bar].Address);
            table.Add(VisaAttribute.PxiMemorySizeBar0 + (uint)bar, () => (long)Device.Bars[bar].Size);
            table.Add(VisaAttribute.PxiMemoryBaseBar0_32 + (uint)bar, () => (int)(uint)Device.Bars[bar].Address);
            table.Add(VisaAttribute.PxiMemorySizeBar0_32 + (uint)bar, () => (int)(uint)Device.Bars[bar].Size);
        }
        table.Add(VisaAttribute.SourceIncrement, () => SourceIncrement ? 1 : 0, v => SourceIncrement = v != 0);
        table.Add(VisaAttribute.DestinationIncrement, () => DestinationIncrement ? 1 : 0, v => DestinationIncrement = v != 0);
        table.Add(VisaAttribute.SourceByteOrder, () => SourceByteOrder, v => SourceByteOrder = v);
        table.Add(VisaAttribute.DestinationByteOrder, () => DestinationByteOrder, v => DestinationByteOrder = v);
        table.Add(VisaAttribute.WindowByteOrder, () => WindowByteOrder, v => WindowByteOrder = v);
        // VI_NMAPPED / VI_USE_OPERS / VI_DEREF_ADDR
        table.Add(VisaAttribute.WindowAccess, () => Window is { } w ? w.Address != 0 ? 3 : 2 : 1);
        table.Add(VisaAttribute.WindowBaseAddress, () => Window?.Offset ?? 0);
        table.Add(VisaAttribute.WindowSize, () => Window?.Size ?? 0);
    }
}

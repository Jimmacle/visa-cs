using VisaCs.Internal;
using VisaCs.Pci;

namespace VisaCs;

/// <summary>
/// A mapped region of a PXI address space (viMapAddress). Offsets are relative to the window start. The window ends
/// when it is disposed (viUnmapAddress) or its session closes; later accesses throw
/// <see cref="VisaStatus.ErrorWindowNotMapped"/>.
/// </summary>
public sealed class PxiWindow : IDisposable
{
    private readonly PxiSession _session;
    private readonly PciRegion _region;
    private bool _disposed;

    internal PxiWindow(PxiSession session, PciRegion region, AddressSpace space, long offset, long size)
    {
        _session = session;
        _region = region;
        Space = space;
        Offset = offset;
        Size = size;
        Address = region.Address != 0 ? region.Address + (nint)offset : 0;
    }

    /// <summary>The mapped address space.</summary>
    public AddressSpace Space { get; }

    /// <summary>Offset of the window in its address space (VI_ATTR_WIN_BASE_ADDR).</summary>
    public long Offset { get; }

    /// <summary>Size of the window in bytes (VI_ATTR_WIN_SIZE).</summary>
    public long Size { get; }

    /// <summary>
    /// Process address of the window for memory BARs (directly dereferenceable), otherwise 0. Valid only until the
    /// window is disposed or the session closes: dereferencing it afterwards crashes the process. Accesses through it
    /// bypass the bounds checks and <see cref="PxiSession.WindowByteOrder"/>.
    /// </summary>
    public nint Address { get; }

    private ulong Peek(long offset, int width)
    {
        Check(offset, width);
        try
        {
            return PxiSession.Swap(_region.Read(Offset + offset, width), width, _session.WindowByteOrder);
        }
        catch (Exception ex) when (PciErrorMapping.IsPciError(ex))
        {
            throw PciErrorMapping.ToVisa(ex);
        }
    }

    private void Poke(long offset, int width, ulong value)
    {
        Check(offset, width);
        try
        {
            _region.Write(Offset + offset, width, PxiSession.Swap(value, width, _session.WindowByteOrder));
        }
        catch (Exception ex) when (PciErrorMapping.IsPciError(ex))
        {
            throw PciErrorMapping.ToVisa(ex);
        }
    }

    private void Check(long offset, int width)
    {
        if (Volatile.Read(ref _disposed)) throw new VisaException(VisaStatus.ErrorWindowNotMapped);
        if (width > _region.MaxWidth) throw new VisaException(VisaStatus.ErrorWidthNotSupported, $"{width}-byte accesses are not supported in {Space}.");
        if (offset < 0 || offset + width > Size) throw new VisaException(VisaStatus.ErrorInvalidOffset);
        if ((Offset + offset) % width != 0) throw new VisaException(VisaStatus.ErrorAlignmentOffsetNotSupported);
    }

    /// <summary>Reads 8 bits at an offset in the window (viPeek8).</summary>
    public byte Peek8(long offset) => (byte)Peek(offset, 1);
    /// <summary>Reads 16 bits at an offset in the window (viPeek16).</summary>
    public ushort Peek16(long offset) => (ushort)Peek(offset, 2);
    /// <summary>Reads 32 bits at an offset in the window (viPeek32).</summary>
    public uint Peek32(long offset) => (uint)Peek(offset, 4);
    /// <summary>Reads 64 bits at an offset in the window (viPeek64).</summary>
    public ulong Peek64(long offset) => Peek(offset, 8);
    /// <summary>Writes 8 bits at an offset in the window (viPoke8).</summary>
    public void Poke8(long offset, byte value) => Poke(offset, 1, value);
    /// <summary>Writes 16 bits at an offset in the window (viPoke16).</summary>
    public void Poke16(long offset, ushort value) => Poke(offset, 2, value);
    /// <summary>Writes 32 bits at an offset in the window (viPoke32).</summary>
    public void Poke32(long offset, uint value) => Poke(offset, 4, value);
    /// <summary>Writes 64 bits at an offset in the window (viPoke64).</summary>
    public void Poke64(long offset, ulong value) => Poke(offset, 8, value);

    /// <summary>Unmaps the window (viUnmapAddress).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, true)) return;
        _session.WindowClosed(this);
    }
}

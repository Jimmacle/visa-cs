using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;

namespace VisaCs.Pci;

/// <summary>
/// Register access to one address space of a PCI function: its configuration space or a BAR. Open one with
/// <see cref="PciDeviceInfo.OpenConfigurationSpace"/> or <see cref="PciDeviceInfo.OpenBar"/>.
/// </summary>
/// <remarks>
/// Each access is checked: it throws <see cref="ArgumentOutOfRangeException"/> unless the width is 1, 2, 4 or 8 bytes
/// and at most <see cref="MaxWidth"/> (<c>ParamName</c> "width"), and the offset is aligned to the width and inside
/// <see cref="Size"/> (<c>ParamName</c> "offset"). Values are in host byte order; PCI registers are little endian.
/// Accesses may run on several threads; after <see cref="Dispose"/> they throw <see cref="ObjectDisposedException"/>,
/// and an access that is running when Dispose is called completes first. Device and operating-system failures throw
/// <see cref="PciException"/>.
/// </remarks>
public abstract class PciRegion : IDisposable
{
    private protected PciRegion()
    {
    }

    /// <summary>Size of the address space in bytes.</summary>
    public abstract long Size { get; }

    /// <summary>The widest access in bytes: 8, or 4 for I/O BARs (Linux accesses I/O ports 1, 2 or 4 bytes at a time).</summary>
    public abstract int MaxWidth { get; }

    /// <summary>
    /// Process address of a mapped memory BAR, otherwise 0. Valid only until the region is disposed: dereferencing it
    /// afterwards crashes the process. Accesses through it bypass the checks.
    /// </summary>
    public virtual nint Address => 0;

    /// <summary>Reads <paramref name="width"/> bytes (1, 2, 4 or 8) at <paramref name="offset"/> as one access.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The width or offset is invalid (see <see cref="PciRegion"/>).</exception>
    /// <exception cref="ObjectDisposedException">The region is disposed.</exception>
    /// <exception cref="PciException">The access fails.</exception>
    public ulong Read(long offset, int width)
    {
        Check(offset, width);
        return ReadCore(offset, width);
    }

    /// <summary>
    /// Writes the low <paramref name="width"/> bytes (1, 2, 4 or 8) of <paramref name="value"/> at
    /// <paramref name="offset"/> as one access.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The width or offset is invalid (see <see cref="PciRegion"/>).</exception>
    /// <exception cref="ObjectDisposedException">The region is disposed.</exception>
    /// <exception cref="PciException">The access fails.</exception>
    public void Write(long offset, int width, ulong value)
    {
        Check(offset, width);
        WriteCore(offset, width, value);
    }

    /// <summary>Reads an 8-bit register.</summary>
    public byte Read8(long offset) => (byte)Read(offset, 1);
    /// <summary>Reads a 16-bit register.</summary>
    public ushort Read16(long offset) => (ushort)Read(offset, 2);
    /// <summary>Reads a 32-bit register.</summary>
    public uint Read32(long offset) => (uint)Read(offset, 4);
    /// <summary>Reads a 64-bit register; not available in I/O BARs.</summary>
    public ulong Read64(long offset) => Read(offset, 8);

    /// <summary>Writes an 8-bit register.</summary>
    public void Write8(long offset, byte value) => Write(offset, 1, value);
    /// <summary>Writes a 16-bit register.</summary>
    public void Write16(long offset, ushort value) => Write(offset, 2, value);
    /// <summary>Writes a 32-bit register.</summary>
    public void Write32(long offset, uint value) => Write(offset, 4, value);
    /// <summary>Writes a 64-bit register; not available in I/O BARs.</summary>
    public void Write64(long offset, ulong value) => Write(offset, 8, value);

    /// <summary>Closes the region; a memory BAR is unmapped once the accesses that are running finish.</summary>
    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    private void Check(long offset, int width)
    {
        if (width is not (1 or 2 or 4 or 8) || width > MaxWidth) throw PciAccess.WidthNotSupported(width, MaxWidth);
        if (offset < 0 || offset % width != 0) throw PciAccess.Misaligned(offset, width);
        if (offset > Size - width) throw PciAccess.OutOfRange(offset, width, Size);
    }

    private protected abstract ulong ReadCore(long offset, int width);

    private protected abstract void WriteCore(long offset, int width, ulong value);

    private protected abstract void DisposeCore();
}

/// <summary>
/// Configuration space or an I/O BAR, accessed with positional reads and writes on a sysfs file. The file is opened for
/// reading first and for writing on the first write, so reading configuration space needs no privileges.
/// </summary>
internal sealed class PciFileRegion : PciRegion
{
    private readonly string _path;
    private readonly bool _isConfig;
    private readonly SafeFileHandle _reader;
    private readonly Lock _lock = new();
    private SafeFileHandle? _writer; // guarded by _lock
    private bool _disposed; // guarded by _lock

    /// <param name="path">The sysfs <c>config</c> or <c>resourceN</c> file.</param>
    /// <param name="size">The size of the space, or 0 for the file's length.</param>
    /// <param name="isConfig">True for configuration space, false for an I/O BAR.</param>
    public PciFileRegion(string path, long size, bool isConfig)
    {
        _path = path;
        _isConfig = isConfig;
        _reader = PciAccess.OpenFile(path, FileAccess.Read);
        Size = size > 0 ? size : RandomAccess.GetLength(_reader);
    }

    public override long Size { get; }

    /// <summary>Linux reads and writes I/O ports 1, 2 or 4 bytes at a time; configuration space allows any width.</summary>
    public override int MaxWidth => _isConfig ? 8 : 4;

    private protected override ulong ReadCore(long offset, int width)
    {
        Span<byte> buffer = stackalloc byte[8];
        int n;
        try
        {
            n = RandomAccess.Read(_reader, buffer[..width], offset);
        }
        catch (Exception ex) when (PciAccess.IsAccessError(ex))
        {
            throw PciAccess.Translate(ex, _path, offset);
        }
        if (n != width) throw PciAccess.ShortRead(_isConfig, offset);
        return width switch
        {
            1 => buffer[0],
            2 => BitConverter.ToUInt16(buffer),
            4 => BitConverter.ToUInt32(buffer),
            _ => BitConverter.ToUInt64(buffer),
        };
    }

    private protected override void WriteCore(long offset, int width, ulong value)
    {
        var writer = Writer();
        Span<byte> buffer = stackalloc byte[8];
        BitConverter.TryWriteBytes(buffer, value);
        try
        {
            RandomAccess.Write(writer, buffer[..width], offset);
        }
        catch (Exception ex) when (PciAccess.IsAccessError(ex))
        {
            throw PciAccess.Translate(ex, _path, offset);
        }
    }

    private SafeFileHandle Writer()
    {
        lock (_lock)
        {
            if (_disposed) throw PciAccess.Closed();
            return _writer ??= PciAccess.OpenFile(_path, FileAccess.ReadWrite);
        }
    }

    private protected override void DisposeCore()
    {
        lock (_lock)
        {
            _disposed = true;
            _writer?.Dispose();
        }
        _reader.Dispose();
    }
}

/// <summary>
/// A memory BAR mapped into the process (mmap of the sysfs resourceN file); accesses are single, exact-width loads and
/// stores. Each access holds a reference on the mapping, so Dispose unmaps only after running accesses finish.
/// </summary>
internal sealed unsafe class PciMappedRegion : PciRegion
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly SafeMemoryMappedViewHandle _handle;
    private readonly byte* _base;
    private int _disposed;

    public PciMappedRegion(string path, long size)
    {
        try
        {
            _file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw PciAccess.MapError(path, ex);
        }
        try
        {
            _view = _file.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _file.Dispose();
            throw PciAccess.MapError(path, ex);
        }
        _handle = _view.SafeMemoryMappedViewHandle;
        byte* pointer = null;
        _handle.AcquirePointer(ref pointer);
        _base = pointer + _view.PointerOffset;
        Size = size;
    }

    public override long Size { get; }

    public override int MaxWidth => 8;

    public override nint Address => (nint)_base;

    private protected override ulong ReadCore(long offset, int width)
    {
        bool added = false;
        try
        {
            AddRef(ref added);
            return width switch
            {
                1 => Volatile.Read(ref *(_base + offset)),
                2 => Volatile.Read(ref *(ushort*)(_base + offset)),
                4 => Volatile.Read(ref *(uint*)(_base + offset)),
                _ => Volatile.Read(ref *(ulong*)(_base + offset)),
            };
        }
        finally
        {
            if (added) _handle.DangerousRelease();
        }
    }

    private protected override void WriteCore(long offset, int width, ulong value)
    {
        bool added = false;
        try
        {
            AddRef(ref added);
            switch (width)
            {
                case 1: Volatile.Write(ref *(_base + offset), (byte)value); break;
                case 2: Volatile.Write(ref *(ushort*)(_base + offset), (ushort)value); break;
                case 4: Volatile.Write(ref *(uint*)(_base + offset), (uint)value); break;
                default: Volatile.Write(ref *(ulong*)(_base + offset), value); break;
            }
        }
        finally
        {
            if (added) _handle.DangerousRelease();
        }
    }

    /// <summary>Keeps the mapping alive for one access; throws once the region is disposed.</summary>
    private void AddRef(ref bool added)
    {
        if (Volatile.Read(ref _disposed) != 0) throw PciAccess.Closed();
        try
        {
            _handle.DangerousAddRef(ref added);
        }
        catch (ObjectDisposedException)
        {
            throw PciAccess.Closed();
        }
    }

    private protected override void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _handle.ReleasePointer();
        _view.Dispose(); // unmaps once no access holds a reference
        _file.Dispose();
    }
}

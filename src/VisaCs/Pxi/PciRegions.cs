using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;

namespace VisaCs.Pxi;

/// <summary>
/// Register access to one address space of a PCI function. Accesses may run on several threads; after
/// <see cref="IDisposable.Dispose"/> they throw <see cref="VisaStatus.ErrorInvalidObject"/>, and an access that is
/// running when Dispose is called completes first.
/// </summary>
internal interface IPciRegion : IDisposable
{
    long Size { get; }

    /// <summary>The widest access in bytes the region supports.</summary>
    int MaxWidth { get; }

    ulong Read(long offset, int width);

    void Write(long offset, int width, ulong value);
}

/// <summary>
/// Configuration space or an I/O BAR, accessed with positional reads and writes on a sysfs file. The file is opened for
/// reading first and for writing on the first write, so reading configuration space needs no privileges.
/// </summary>
internal sealed class PciFileRegion : IPciRegion
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

    public long Size { get; }

    /// <summary>Linux reads and writes I/O ports 1, 2 or 4 bytes at a time; configuration space allows any width.</summary>
    public int MaxWidth => _isConfig ? 8 : 4;

    public ulong Read(long offset, int width)
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
        if (n != width)
        {
            throw _isConfig
                ? new VisaException(VisaStatus.ErrorNoPermission,
                    $"Configuration space beyond the first 64 bytes needs root (offset 0x{offset:X}).")
                : new VisaException(VisaStatus.ErrorBusError, $"Short PCI read at offset 0x{offset:X}.");
        }
        return width switch
        {
            1 => buffer[0],
            2 => BitConverter.ToUInt16(buffer),
            4 => BitConverter.ToUInt32(buffer),
            _ => BitConverter.ToUInt64(buffer),
        };
    }

    public void Write(long offset, int width, ulong value)
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

    public void Dispose()
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
internal sealed unsafe class PciMappedRegion : IPciRegion
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
            throw MapError(path, ex);
        }
        try
        {
            _view = _file.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _file.Dispose();
            throw MapError(path, ex);
        }
        _handle = _view.SafeMemoryMappedViewHandle;
        byte* pointer = null;
        _handle.AcquirePointer(ref pointer);
        _base = pointer + _view.PointerOffset;
        Size = size;
    }

    private static VisaException MapError(string path, Exception ex) => ex is UnauthorizedAccessException
        ? new VisaException(VisaStatus.ErrorNoPermission,
            $"Access to {path} was denied. PCI BAR access needs root or a udev rule granting access to the resource files.", ex)
        : new VisaException(VisaStatus.ErrorResourceBusy, $"Cannot map {path}: {ex.Message}", ex);

    public long Size { get; }

    public int MaxWidth => 8;

    /// <summary>The mapped address; valid until Dispose.</summary>
    public nint Address => (nint)_base;

    public ulong Read(long offset, int width)
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

    public void Write(long offset, int width, ulong value)
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _handle.ReleasePointer();
        _view.Dispose(); // unmaps once no access holds a reference
        _file.Dispose();
    }
}

internal static class PciAccess
{
    public static SafeFileHandle OpenFile(string path, FileAccess access)
    {
        try
        {
            return File.OpenHandle(path, FileMode.Open, access, FileShare.ReadWrite);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new VisaException(VisaStatus.ErrorNoPermission,
                $"Access to {path} was denied. PCI register access needs root or a udev rule granting access.", ex);
        }
        catch (FileNotFoundException ex)
        {
            throw new VisaException(VisaStatus.ErrorResourceNotFound, $"{path} does not exist.", ex);
        }
    }

    /// <summary>The error for an access to a region of a closed session.</summary>
    public static VisaException Closed() => new(VisaStatus.ErrorInvalidObject, "The session is closed.");

    public static bool IsAccessError(Exception ex) => ex is ObjectDisposedException or UnauthorizedAccessException or IOException;

    /// <summary>The VISA error for a failed file access (see <see cref="IsAccessError"/>).</summary>
    public static VisaException Translate(Exception ex, string path, long offset) => ex switch
    {
        ObjectDisposedException => Closed(),
        UnauthorizedAccessException => new VisaException(VisaStatus.ErrorNoPermission, $"Access to {path} at offset 0x{offset:X} was denied.", ex),
        _ => new VisaException(VisaStatus.ErrorIO, $"PCI access to {path} at offset 0x{offset:X} failed: {ex.Message}", ex),
    };
}

using System.Runtime.InteropServices;

namespace VisaCs.Internal;

/// <summary>Minimal libc bindings (Linux and macOS).</summary>
internal static unsafe partial class Libc
{
    public static readonly int O_RDWR = 2;
    public static readonly int O_NOCTTY = OperatingSystem.IsMacOS() ? 0x20000 : 0x100;
    public static readonly int O_NONBLOCK = OperatingSystem.IsMacOS() ? 0x4 : 0x800;
    public static readonly int O_CLOEXEC = OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;

    // errno values that Linux and macOS share.
    public const int ENOENT = 2;
    public const int EINTR = 4;
    public const int EIO = 5;
    public const int ENXIO = 6;
    public const int EACCES = 13;
    public const int EBUSY = 16;
    public const int ENODEV = 19;
    public const int EPIPE = 32;
    public static int EAGAIN => OperatingSystem.IsMacOS() ? 35 : 11;

    // Linux values (macOS numbers them 60 and 58); only the Linux usbfs backend uses them.
    public const int ETIMEDOUT = 110;
    public const int ESHUTDOWN = 108;

    [LibraryImport(NativeLibraries.LibC, SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int open(string path, int flags);

    [LibraryImport(NativeLibraries.LibC, SetLastError = true)]
    public static partial int close(int fd);

    [LibraryImport(NativeLibraries.LibC, SetLastError = true)]
    public static partial nint read(int fd, byte* buffer, nint count);

    [LibraryImport(NativeLibraries.LibC, SetLastError = true)]
    public static partial nint write(int fd, byte* buffer, nint count);

    [LibraryImport(NativeLibraries.LibC, SetLastError = true, EntryPoint = "ioctl")]
    private static partial int ioctl_linux(int fd, nuint request, void* arg);

    // Apple ARM64 passes variadic arguments on the stack: fill the six remaining integer
    // argument registers so that 'arg' lands where ioctl's va_arg expects it.
    [LibraryImport(NativeLibraries.LibC, SetLastError = true, EntryPoint = "ioctl")]
    private static partial int ioctl_apple_arm64(int fd, nuint request, nint r2, nint r3, nint r4, nint r5, nint r6, nint r7, void* arg);

    public static int ioctl(int fd, nuint request, void* arg)
    {
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return ioctl_apple_arm64(fd, request, 0, 0, 0, 0, 0, 0, arg);
        return ioctl_linux(fd, request, arg);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    public const short POLLIN = 0x1;
    public const short POLLOUT = 0x4;
    public const short POLLERR = 0x8;
    public const short POLLHUP = 0x10;
    public const short POLLNVAL = 0x20;

    [LibraryImport(NativeLibraries.LibC, SetLastError = true)]
    public static partial int poll(PollFd* fds, nuint nfds, int timeout);

    [LibraryImport(NativeLibraries.LibC, SetLastError = true)]
    private static partial int pipe(int* fds);

    [LibraryImport(NativeLibraries.LibC, SetLastError = true)]
    private static partial int pipe2(int* fds, int flags);

    /// <summary>
    /// Creates a pipe whose ends are non-blocking and closed in child processes. Returns 0, or -1 with errno set.
    /// </summary>
    public static int CreatePipe(int* fds)
    {
        if (!OperatingSystem.IsMacOS()) return pipe2(fds, O_CLOEXEC | O_NONBLOCK);
        // macOS has no pipe2.
        if (pipe(fds) != 0) return -1;
        const nuint FIOCLEX = 0x20006601, FIONBIO = 0x8004667E;
        int on = 1;
        for (int i = 0; i < 2; i++)
        {
            ioctl(fds[i], FIOCLEX, null);
            ioctl(fds[i], FIONBIO, &on);
        }
        return 0;
    }
}

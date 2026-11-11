using System.Runtime.InteropServices;
using VisaCs.Internal;

namespace VisaCs.Serial;

// Line settings. Linux is configured through the kernel's termios2 ioctls (TCGETS2/TCSETS2), whose ABI is stable,
// instead of glibc's termios functions: glibc 2.42 changed what speed_t means for cfsetspeed and friends, and the
// symbol a P/Invoke binds to is the newest version. macOS uses its libc, which takes speeds as plain numbers.
internal sealed unsafe partial class UnixSerialPort
{
    private protected override void ConfigureCore(SerialSettings s)
    {
        if (s.FlowControl.HasFlag(SerialFlowControlModes.DtrDsr))
            throw NotSupported("DTR/DSR flow control is not supported by termios.");
        // termios delivers a byte received with a parity or framing error as NUL; it cannot substitute another character.
        if (s.ReplacementCharacter != 0)
            throw NotSupported("A replacement character is not supported by termios (errored bytes read as NUL).");
        if (IsMac && s.Parity is SerialParity.Mark or SerialParity.Space)
            throw NotSupported("Mark/space parity is not supported on macOS.");

        int fd = Acquire();
        try
        {
            if (IsMac) ConfigureMac(fd, s);
            else ConfigureLinux(fd, s);
        }
        finally
        {
            Release();
        }
        _discardNull = s.DiscardNull;
    }

    // ------------------------------------------------------------------ Linux

    /// <summary>
    /// Kernel <c>struct termios2</c> and the asm-generic constants, valid on x64, x86, Arm, Arm64, RISC-V, LoongArch
    /// and s390x (not on PowerPC, MIPS, SPARC or Alpha, which number them differently).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Termios2
    {
        public uint c_iflag, c_oflag, c_cflag, c_lflag;
        public byte c_line;
        public fixed byte c_cc[19];
        public uint c_ispeed, c_ospeed;
    }

    private static class L
    {
        public const uint IGNBRK = 0x1, BRKINT = 0x2, IGNPAR = 0x4, PARMRK = 0x8, INPCK = 0x10, ISTRIP = 0x20, INLCR = 0x40,
            IGNCR = 0x80, ICRNL = 0x100, IUCLC = 0x200, IXON = 0x400, IXANY = 0x800, IXOFF = 0x1000, IMAXBEL = 0x2000;
        public const uint OPOST = 0x1;
        public const uint ISIG = 0x1, ICANON = 0x2, ECHO = 0x8, ECHONL = 0x40, IEXTEN = 0x8000;
        public const uint CSIZE = 0x30, CS5 = 0x0, CS6 = 0x10, CS7 = 0x20, CS8 = 0x30, CSTOPB = 0x40, CREAD = 0x80, PARENB = 0x100,
            PARODD = 0x200, CLOCAL = 0x800, CBAUD = 0x100F, BOTHER = 0x1000, CIBAUD = CBAUD << 16, CMSPAR = 0x40000000, CRTSCTS = 0x80000000;
        public const int VTIME = 5, VMIN = 6, VSTART = 8, VSTOP = 9;
        public const nuint TCGETS2 = 0x802C542A, TCSETS2 = 0x402C542B;
    }

    /// <summary>The classic Bxxx codes; other rates use BOTHER with the number in c_ospeed.</summary>
    private static readonly (int Baud, uint Code)[] LinuxBauds =
    [
        (50, 1), (75, 2), (110, 3), (134, 4), (150, 5), (200, 6), (300, 7), (600, 8), (1200, 9), (1800, 10), (2400, 11),
        (4800, 12), (9600, 13), (19200, 14), (38400, 15), (57600, 0x1001), (115200, 0x1002), (230400, 0x1003),
        (460800, 0x1004), (500000, 0x1005), (576000, 0x1006), (921600, 0x1007), (1000000, 0x1008), (1152000, 0x1009),
        (1500000, 0x100A), (2000000, 0x100B), (2500000, 0x100C), (3000000, 0x100D), (3500000, 0x100E), (4000000, 0x100F),
    ];

    private static void ConfigureLinux(int fd, SerialSettings s)
    {
        Termios2 t;
        if (Libc.ioctl(fd, L.TCGETS2, &t) != 0) throw Error("TCGETS2");

        // Raw mode, as cfmakeraw.
        t.c_iflag &= ~(L.IGNBRK | L.BRKINT | L.IGNPAR | L.PARMRK | L.INPCK | L.ISTRIP | L.INLCR | L.IGNCR | L.ICRNL |
            L.IUCLC | L.IXON | L.IXANY | L.IXOFF | L.IMAXBEL);
        t.c_oflag &= ~L.OPOST;
        t.c_lflag &= ~(L.ISIG | L.ICANON | L.ECHO | L.ECHONL | L.IEXTEN);

        uint cflag = t.c_cflag & ~(L.CSIZE | L.CSTOPB | L.PARENB | L.PARODD | L.CMSPAR | L.CRTSCTS | L.CBAUD | L.CIBAUD);
        cflag |= L.CREAD | L.CLOCAL | s.DataBits switch { 5 => L.CS5, 6 => L.CS6, 7 => L.CS7, _ => L.CS8 };
        if (s.StopBits != SerialStopBitsMode.One) cflag |= L.CSTOPB;
        cflag |= s.Parity switch
        {
            SerialParity.Odd => L.PARENB | L.PARODD,
            SerialParity.Even => L.PARENB,
            SerialParity.Mark => L.PARENB | L.PARODD | L.CMSPAR,
            SerialParity.Space => L.PARENB | L.CMSPAR,
            _ => 0u,
        };
        if (s.FlowControl.HasFlag(SerialFlowControlModes.RtsCts)) cflag |= L.CRTSCTS;
        // CIBAUD stays 0: the input speed follows the output speed.
        var match = LinuxBauds.FirstOrDefault(b => b.Baud == s.BaudRate);
        cflag |= match.Baud != 0 ? match.Code : L.BOTHER;
        t.c_cflag = cflag;
        t.c_ispeed = t.c_ospeed = (uint)s.BaudRate;

        if (s.FlowControl.HasFlag(SerialFlowControlModes.XOnXOff)) t.c_iflag |= L.IXON | L.IXOFF;
        if (s.Parity != SerialParity.None) t.c_iflag |= L.INPCK;
        t.c_cc[L.VSTART] = s.XOnCharacter;
        t.c_cc[L.VSTOP] = s.XOffCharacter;
        t.c_cc[L.VMIN] = 0;
        t.c_cc[L.VTIME] = 0;

        // One call applies everything; on failure the port keeps its previous settings.
        if (Libc.ioctl(fd, L.TCSETS2, &t) != 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            throw NotSupported($"The serial port rejected the settings (baud {s.BaudRate}, errno {errno}).", errno);
        }
    }

    // ------------------------------------------------------------------ macOS (not tested on a Mac)

    private static class M
    {
        public const ulong INPCK = 0x10, ISTRIP = 0x20, IXON = 0x200, IXOFF = 0x400, IGNPAR = 0x4, PARMRK = 0x8;
        public const ulong CSIZE = 0x300, CS5 = 0x0, CS6 = 0x100, CS7 = 0x200, CS8 = 0x300, CSTOPB = 0x400, CREAD = 0x800, PARENB = 0x1000,
            PARODD = 0x2000, CLOCAL = 0x8000, CRTSCTS = 0x30000;
        public const int VSTART = 12, VSTOP = 13, VMIN = 16, VTIME = 17;
        // struct termios: four 8-byte flag words, then c_cc[20].
        public const int Iflag = 0, Cflag = 2, CcOffset = 32, Size = 256;
        public const nuint IOSSIOSPEED = 0x80085402;
    }

    /// <summary>Rates the macOS termios accepts directly; others are set with IOSSIOSPEED.</summary>
    private static readonly int[] MacBauds =
        [50, 75, 110, 134, 150, 200, 300, 600, 1200, 1800, 2400, 4800, 7200, 9600, 14400, 19200, 28800, 38400, 57600, 76800, 115200, 230400];

    private static void ConfigureMac(int fd, SerialSettings s)
    {
        byte* original = stackalloc byte[M.Size];
        byte* tio = stackalloc byte[M.Size];
        new Span<byte>(original, M.Size).Clear();
        if (Native.tcgetattr(fd, original) != 0) throw Error("tcgetattr");
        new Span<byte>(original, M.Size).CopyTo(new Span<byte>(tio, M.Size));
        Native.cfmakeraw(tio);

        ulong* flags = (ulong*)tio;
        ulong cflag = flags[M.Cflag] & ~(M.CSIZE | M.CSTOPB | M.PARENB | M.PARODD | M.CRTSCTS);
        cflag |= M.CREAD | M.CLOCAL | s.DataBits switch { 5 => M.CS5, 6 => M.CS6, 7 => M.CS7, _ => M.CS8 };
        if (s.StopBits != SerialStopBitsMode.One) cflag |= M.CSTOPB;
        if (s.Parity == SerialParity.Odd) cflag |= M.PARENB | M.PARODD;
        else if (s.Parity == SerialParity.Even) cflag |= M.PARENB;
        if (s.FlowControl.HasFlag(SerialFlowControlModes.RtsCts)) cflag |= M.CRTSCTS;
        flags[M.Cflag] = cflag;

        ulong iflag = flags[M.Iflag] & ~(M.IXON | M.IXOFF | M.INPCK | M.ISTRIP | M.IGNPAR | M.PARMRK);
        if (s.FlowControl.HasFlag(SerialFlowControlModes.XOnXOff)) iflag |= M.IXON | M.IXOFF;
        if (s.Parity != SerialParity.None) iflag |= M.INPCK;
        flags[M.Iflag] = iflag;

        tio[M.CcOffset + M.VSTART] = s.XOnCharacter;
        tio[M.CcOffset + M.VSTOP] = s.XOffCharacter;
        tio[M.CcOffset + M.VMIN] = 0;
        tio[M.CcOffset + M.VTIME] = 0;

        // cfsetspeed stores any number, but drivers may refuse a non-standard one in tcsetattr: set a standard
        // placeholder and the real rate with IOSSIOSPEED afterwards.
        bool custom = Array.IndexOf(MacBauds, s.BaudRate) < 0;
        Native.cfsetspeed(tio, (nuint)(custom ? 9600 : s.BaudRate));
        if (Native.tcsetattr(fd, 0, tio) != 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            throw NotSupported($"The serial port rejected the settings (errno {errno}).", errno);
        }
        if (!custom) return;
        nuint speed = (nuint)s.BaudRate;
        if (Libc.ioctl(fd, M.IOSSIOSPEED, &speed) != 0)
        {
            Native.tcsetattr(fd, 0, original);
            throw NotSupported($"Baud rate {s.BaudRate} is not supported by this port.");
        }
    }

    // ------------------------------------------------------------------ both

    /// <summary>ioctl requests and termios constants that Linux and macOS number differently.</summary>
    private static class Requests
    {
        private static readonly bool Mac = OperatingSystem.IsMacOS();
        public static readonly nuint TIOCEXCL = Mac ? 0x2000740Du : 0x540Cu;
        public static readonly nuint TIOCMGET = Mac ? 0x4004746Au : 0x5415u;
        public static readonly nuint TIOCMBIS = Mac ? 0x8004746Cu : 0x5416u;
        public static readonly nuint TIOCMBIC = Mac ? 0x8004746Bu : 0x5417u;
        public static readonly nuint FIONREAD = Mac ? 0x4004667Fu : 0x541Bu;
        public static readonly nuint TIOCOUTQ = Mac ? 0x40047473u : 0x5411u;
        public static readonly nuint TIOCSBRK = Mac ? 0x2000747Bu : 0x5427u;
        public static readonly nuint TIOCCBRK = Mac ? 0x2000747Au : 0x5428u;
        public const nuint TIOCSERGETLSR = 0x5459; // Linux only
        public static readonly int TCIFLUSH = Mac ? 1 : 0;
        public static readonly int TCOFLUSH = Mac ? 2 : 1;
        public static readonly int TCOOFF = Mac ? 1 : 0;
        public static readonly int TCOON = Mac ? 2 : 1;
    }

    private static partial class Native
    {
        [LibraryImport(NativeLibraries.LibC, SetLastError = true)] public static partial int isatty(int fd);
        [LibraryImport(NativeLibraries.LibC, SetLastError = true)] public static partial int tcflush(int fd, int queue);
        [LibraryImport(NativeLibraries.LibC, SetLastError = true)] public static partial int tcflow(int fd, int action);
        // macOS only: Linux is configured with TCGETS2/TCSETS2.
        [LibraryImport(NativeLibraries.LibC, SetLastError = true)] public static partial int tcgetattr(int fd, byte* termios);
        [LibraryImport(NativeLibraries.LibC, SetLastError = true)] public static partial int tcsetattr(int fd, int optionalActions, byte* termios);
        [LibraryImport(NativeLibraries.LibC, SetLastError = true)] public static partial void cfmakeraw(byte* termios);
        [LibraryImport(NativeLibraries.LibC, SetLastError = true)] public static partial int cfsetspeed(byte* termios, nuint speed);
    }
}

using System.Runtime.InteropServices;

namespace VisaCs.Usb;

public sealed partial class UsbFsBackend
{
    /// <summary>usbfs ioctl numbers and structures (generic Linux encoding used by x86, x64, ARM and ARM64).</summary>
    private static unsafe class Ioctl
    {
        private const uint IocNone = 0, IocWrite = 1, IocRead = 2;

        public const byte UrbTypeInterrupt = 1, UrbTypeControl = 2, UrbTypeBulk = 3;

        /// <summary>USBDEVFS_DISCONNECT_CLAIM flag: detach any driver except the one named.</summary>
        public const uint DisconnectClaimExceptDriver = 0x02;

        /// <summary>USBDEVFS_MAXDRIVERNAME + 1.</summary>
        public const int DriverNameSize = 256;

        private static nuint Make(uint dir, uint type, uint nr, int size) =>
            (nuint)((dir << 30) | ((uint)size << 16) | (type << 8) | nr);

        [StructLayout(LayoutKind.Sequential)]
        public struct UsbDevFsIoctl
        {
            public int ifno;
            public int ioctl_code;
            public nint data;
        }

        /// <summary>struct usbdevfs_getdriver.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct GetDriver
        {
            public uint @interface;
            public fixed byte driver[DriverNameSize];
        }

        /// <summary>struct usbdevfs_disconnect_claim.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct DisconnectClaimRequest
        {
            public uint @interface;
            public uint flags;
            public fixed byte driver[DriverNameSize];
        }

        /// <summary>struct usbdevfs_urb without the isochronous packet descriptors (56 bytes on 64-bit, 44 on 32-bit).</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Urb
        {
            public byte type;
            public byte endpoint;
            public int status;
            public uint flags;
            public nint buffer;
            public int buffer_length;
            public int actual_length;
            public int start_frame;
            public int number_of_packets; // union with stream_id
            public int error_count;
            public uint signr;
            public nint usercontext;
        }

        public static readonly nuint GetDriverName = Make(IocWrite, 'U', 8, sizeof(GetDriver));
        public static readonly nuint SubmitUrb = Make(IocRead, 'U', 10, sizeof(Urb));
        public static readonly nuint DiscardUrb = Make(IocNone, 'U', 11, 0);
        public static readonly nuint ReapUrbNoDelay = Make(IocWrite, 'U', 13, sizeof(nint));
        public static readonly nuint ClaimInterface = Make(IocRead, 'U', 15, sizeof(uint));
        public static readonly nuint ReleaseInterface = Make(IocRead, 'U', 16, sizeof(uint));
        public static readonly nuint IoctlCommand = Make(IocRead | IocWrite, 'U', 18, sizeof(UsbDevFsIoctl));
        public static readonly nuint ClearHalt = Make(IocRead, 'U', 21, sizeof(uint));
        public static readonly nuint Disconnect = Make(IocNone, 'U', 22, 0);
        public static readonly nuint Connect = Make(IocNone, 'U', 23, 0);
        public static readonly nuint DisconnectClaim = Make(IocRead, 'U', 27, sizeof(DisconnectClaimRequest));
    }
}

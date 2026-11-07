using System.Runtime.InteropServices;

namespace VisaCs.Serial;

internal sealed unsafe partial class WindowsSerialPort
{
    private static partial class Native
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        public const uint INFINITE = 0xFFFFFFFF;
        public const uint WAIT_OBJECT_0 = 0;

        public const int ERROR_FILE_NOT_FOUND = 2;
        public const int ERROR_PATH_NOT_FOUND = 3;
        public const int ERROR_ACCESS_DENIED = 5;
        public const int ERROR_BAD_COMMAND = 22;
        public const int ERROR_GEN_FAILURE = 31;
        public const int ERROR_SHARING_VIOLATION = 32;
        public const int ERROR_NO_SUCH_DEVICE = 433;
        public const int ERROR_OPERATION_ABORTED = 995;
        public const int ERROR_IO_PENDING = 997;
        public const int ERROR_DEVICE_NOT_CONNECTED = 1167;
        public const int ERROR_DEVICE_REMOVED = 1617;

        public const uint PURGE_TXABORT = 0x1;
        public const uint PURGE_TXCLEAR = 0x4;
        public const uint PURGE_RXCLEAR = 0x8;
        public const uint SETXOFF = 1;
        public const uint SETXON = 2;
        public const uint SETRTS = 3;
        public const uint CLRRTS = 4;
        public const uint SETDTR = 5;
        public const uint CLRDTR = 6;
        public const uint MS_CTS_ON = 0x10;
        public const uint MS_DSR_ON = 0x20;
        public const uint MS_RING_ON = 0x40;
        public const uint MS_RLSD_ON = 0x80;

        [StructLayout(LayoutKind.Sequential)]
        public struct DCB
        {
            public uint DCBlength;
            public uint BaudRate;
            public uint Flags;
            public ushort wReserved;
            public ushort XonLim;
            public ushort XoffLim;
            public byte ByteSize;
            public byte Parity;
            public byte StopBits;
            public byte XonChar;
            public byte XoffChar;
            public byte ErrorChar;
            public byte EofChar;
            public byte EvtChar;
            public ushort wReserved1;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct COMMTIMEOUTS
        {
            public uint ReadIntervalTimeout;
            public uint ReadTotalTimeoutMultiplier;
            public uint ReadTotalTimeoutConstant;
            public uint WriteTotalTimeoutMultiplier;
            public uint WriteTotalTimeoutConstant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct COMSTAT
        {
            public uint Flags;
            public uint cbInQue;
            public uint cbOutQue;
        }

        // Transfers use the raw handle while holding a reference on the CommHandle (see Acquire); the other calls
        // take the CommHandle and fail with ObjectDisposedException once the handle is closed.

        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        public static partial nint CreateFileW(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, nint templateFile);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool ReadFile(nint file, byte* buffer, uint count, uint* read, NativeOverlapped* overlapped);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool WriteFile(nint file, byte* buffer, uint count, uint* written, NativeOverlapped* overlapped);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetOverlappedResult(nint file, NativeOverlapped* overlapped, uint* transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CancelIoEx(nint file, NativeOverlapped* overlapped);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial nint CreateEventW(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, nint name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CloseHandle(nint handle);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetupComm(CommHandle file, uint inQueue, uint outQueue);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetCommTimeouts(CommHandle file, COMMTIMEOUTS* timeouts);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetCommState(CommHandle file, DCB* dcb);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetCommState(CommHandle file, DCB* dcb);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool EscapeCommFunction(CommHandle file, uint function);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetCommModemStatus(CommHandle file, out uint modemStatus);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool ClearCommError(CommHandle file, out uint errors, COMSTAT* stat);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool PurgeComm(CommHandle file, uint flags);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetCommBreak(CommHandle file);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool ClearCommBreak(CommHandle file);
    }
}

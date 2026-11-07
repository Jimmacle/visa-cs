using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VisaCs.Usb;

public sealed partial class WinUsbBackend
{
    private static unsafe partial class Native
    {
        public const uint DIGCF_PRESENT = 0x2;
        public const uint DIGCF_ALLCLASSES = 0x4;
        public const uint DIGCF_DEVICEINTERFACE = 0x10;
        public const uint SPDRP_DEVICEDESC = 0x0;
        public const uint SPDRP_HARDWAREID = 0x1;
        public const uint SPDRP_COMPATIBLEIDS = 0x2;
        public const uint SPDRP_SERVICE = 0x4;
        public const uint DICS_FLAG_GLOBAL = 1;
        public const uint DIREG_DEV = 1;
        public const uint KEY_READ = 0x20019;
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1;
        public const uint FILE_SHARE_WRITE = 2;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        public const uint PIPE_TRANSFER_TIMEOUT = 0x03;
        public const uint INFINITE = 0xFFFFFFFF;
        public const uint WAIT_TIMEOUT = 0x102;
        public const int ERROR_FILE_NOT_FOUND = 2;
        public const int ERROR_ACCESS_DENIED = 5;
        public const int ERROR_BAD_COMMAND = 22;
        public const int ERROR_SEM_TIMEOUT = 121;
        public const int ERROR_DEVICE_NOT_CONNECTED = 1167;
        public const int ERROR_OPERATION_ABORTED = 995;
        public const int ERROR_IO_PENDING = 997;
        public static readonly Guid GUID_DEVINTERFACE_USB_DEVICE = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public nint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public nint Reserved;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct WINUSB_SETUP_PACKET
        {
            public byte RequestType;
            public byte Request;
            public ushort Value;
            public ushort Index;
            public ushort Length;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct USB_INTERFACE_DESCRIPTOR
        {
            public byte bLength;
            public byte bDescriptorType;
            public byte bInterfaceNumber;
            public byte bAlternateSetting;
            public byte bNumEndpoints;
            public byte bInterfaceClass;
            public byte bInterfaceSubClass;
            public byte bInterfaceProtocol;
            public byte iInterface;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WINUSB_PIPE_INFORMATION
        {
            public int PipeType;
            public byte PipeId;
            public ushort MaximumPacketSize;
            public byte Interval;
        }

        [LibraryImport("setupapi.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        public static partial nint SetupDiGetClassDevsW(Guid* classGuid, string? enumerator, nint hwndParent, uint flags);

        [LibraryImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

        [LibraryImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetupDiEnumDeviceInfo(nint deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

        [LibraryImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetupDiGetDeviceRegistryPropertyW(nint deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint property,
            out uint propertyRegDataType, byte* propertyBuffer, uint propertyBufferSize, out uint requiredSize);

        [LibraryImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetupDiGetDeviceInstanceIdW(nint deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, char* deviceInstanceId,
            uint deviceInstanceIdSize, out uint requiredSize);

        [LibraryImport("setupapi.dll", SetLastError = true)]
        public static partial nint SetupDiOpenDevRegKey(nint deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint scope, uint hwProfile,
            uint keyType, uint samDesired);

        [LibraryImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetupDiEnumDeviceInterfaces(nint deviceInfoSet, SP_DEVINFO_DATA* deviceInfoData, ref Guid interfaceClassGuid,
            uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [LibraryImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetupDiGetDeviceInterfaceDetailW(nint deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
            byte* deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, SP_DEVINFO_DATA* deviceInfoData);

        [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
        public static partial int RegQueryValueExW(nint hKey, string valueName, nint reserved, out uint type, byte* data, ref uint dataSize);

        [LibraryImport("advapi32.dll")]
        public static partial int RegCloseKey(nint hKey);

        [LibraryImport("cfgmgr32.dll")]
        public static partial int CM_Get_Parent(out uint parent, uint devInst, uint flags);

        [LibraryImport("cfgmgr32.dll")]
        public static partial int CM_Get_Device_IDW(uint devInst, char* buffer, int bufferLength, uint flags);

        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        public static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, nint templateFile);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CancelIoEx(SafeFileHandle file, NativeOverlapped* overlapped);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetOverlappedResult(SafeFileHandle file, NativeOverlapped* overlapped, out uint transferred,
            [MarshalAs(UnmanagedType.Bool)] bool wait);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial nint CreateEventW(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset,
            [MarshalAs(UnmanagedType.Bool)] bool initialState, nint name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool ResetEvent(nint handle);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CloseHandle(nint handle);

        [LibraryImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool WinUsb_Initialize(SafeFileHandle deviceHandle, out nint interfaceHandle);

        [LibraryImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool WinUsb_Free(nint interfaceHandle);

        [LibraryImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool WinUsb_GetAssociatedInterface(nint interfaceHandle, byte associatedInterfaceIndex, out nint associatedInterfaceHandle);

        [LibraryImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool WinUsb_QueryInterfaceSettings(nint interfaceHandle, byte alternateInterfaceNumber,
            out USB_INTERFACE_DESCRIPTOR usbAltInterfaceDescriptor);

        [LibraryImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool WinUsb_QueryPipe(nint interfaceHandle, byte alternateInterfaceNumber, byte pipeIndex,
            out WINUSB_PIPE_INFORMATION pipeInformation);

        [LibraryImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool WinUsb_SetPipePolicy(nint interfaceHandle, byte pipeId, uint policyType, uint valueLength, ref uint value);

        [LibraryImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool WinUsb_ReadPipe(nint interfaceHandle, byte pipeId, byte* buffer, uint bufferLength,
            out uint lengthTransferred, nint overlapped);

        [LibraryImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool WinUsb_WritePipe(nint interfaceHandle, byte pipeId, byte* buffer, uint bufferLength,
            out uint lengthTransferred, nint overlapped);

        [LibraryImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool WinUsb_ControlTransfer(nint interfaceHandle, WINUSB_SETUP_PACKET setupPacket, byte* buffer,
            uint bufferLength, out uint lengthTransferred, nint overlapped);

        [LibraryImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool WinUsb_ResetPipe(nint interfaceHandle, byte pipeId);
    }
}

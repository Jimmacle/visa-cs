using System.Runtime.InteropServices;
using VisaCs.Internal;

namespace VisaCs.Usb;

public sealed partial class LibUsbBackend
{
    private static unsafe partial class Native
    {
        public const byte LIBUSB_TRANSFER_TYPE_CONTROL = 0;
        public const byte LIBUSB_TRANSFER_TYPE_BULK = 2;
        public const byte LIBUSB_TRANSFER_TYPE_INTERRUPT = 3;

        public const int LIBUSB_TRANSFER_COMPLETED = 0;
        public const int LIBUSB_TRANSFER_ERROR = 1;
        public const int LIBUSB_TRANSFER_TIMED_OUT = 2;
        public const int LIBUSB_TRANSFER_CANCELLED = 3;
        public const int LIBUSB_TRANSFER_STALL = 4;
        public const int LIBUSB_TRANSFER_NO_DEVICE = 5;
        public const int LIBUSB_TRANSFER_OVERFLOW = 6;

        public const int LIBUSB_ERROR_IO = -1;
        public const int LIBUSB_ERROR_ACCESS = -3;
        public const int LIBUSB_ERROR_NO_DEVICE = -4;
        public const int LIBUSB_ERROR_NOT_FOUND = -5;
        public const int LIBUSB_ERROR_BUSY = -6;
        public const int LIBUSB_ERROR_TIMEOUT = -7;
        public const int LIBUSB_ERROR_OVERFLOW = -8;
        public const int LIBUSB_ERROR_PIPE = -9;
        public const int LIBUSB_ERROR_INTERRUPTED = -10;
        public const int LIBUSB_ERROR_NOT_SUPPORTED = -12;
        public const int LIBUSB_ERROR_OTHER = -99;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct DeviceDescriptor
        {
            public byte bLength;
            public byte bDescriptorType;
            public ushort bcdUSB;
            public byte bDeviceClass;
            public byte bDeviceSubClass;
            public byte bDeviceProtocol;
            public byte bMaxPacketSize0;
            public ushort idVendor;
            public ushort idProduct;
            public ushort bcdDevice;
            public byte iManufacturer;
            public byte iProduct;
            public byte iSerialNumber;
            public byte bNumConfigurations;
        }

        /// <summary>struct libusb_transfer, without the isochronous packet descriptors.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Transfer
        {
            public nint dev_handle;
            public byte flags;
            public byte endpoint;
            public byte type;
            public uint timeout;
            public int status;
            public int length;
            public int actual_length;
            public delegate* unmanaged<Transfer*, void> callback;
            public nint user_data;
            public byte* buffer;
            public int num_iso_packets;
        }

        [LibraryImport(NativeLibraries.LibUsb)] public static partial int libusb_init(nint* context);
        [LibraryImport(NativeLibraries.LibUsb)] public static partial void libusb_exit(nint context);
        [LibraryImport(NativeLibraries.LibUsb)] public static partial nint libusb_get_device_list(nint context, nint** list);
        [LibraryImport(NativeLibraries.LibUsb)] public static partial void libusb_free_device_list(nint* list, int unrefDevices);
        [LibraryImport(NativeLibraries.LibUsb)] public static partial int libusb_get_device_descriptor(nint device, DeviceDescriptor* descriptor);
        [LibraryImport(NativeLibraries.LibUsb)] public static partial int libusb_get_active_config_descriptor(nint device, nint* config);
        [LibraryImport(NativeLibraries.LibUsb)] public static partial int libusb_get_config_descriptor(nint device, byte index, nint* config);
        [LibraryImport(NativeLibraries.LibUsb)] public static partial void libusb_free_config_descriptor(nint config);
        [LibraryImport(NativeLibraries.LibUsb)] public static partial byte libusb_get_bus_number(nint device);
        [LibraryImport(NativeLibraries.LibUsb)] public static partial byte libusb_get_device_address(nint device);
        [LibraryImport(NativeLibraries.LibUsb)] public static partial int libusb_open(nint device, nint* handle);
        [LibraryImport(NativeLibraries.LibUsb)] public static partial void libusb_close(nint handle);
        [LibraryImport(NativeLibraries.LibUsb)] public static partial int libusb_get_string_descriptor_ascii(nint handle, byte index, byte* data, int length);
        [LibraryImport(NativeLibraries.LibUsb)] public static partial int libusb_set_auto_detach_kernel_driver(nint handle, int enable);
        [LibraryImport(NativeLibraries.LibUsb)] public static partial int libusb_claim_interface(nint handle, int interfaceNumber);
        [LibraryImport(NativeLibraries.LibUsb)] public static partial int libusb_release_interface(nint handle, int interfaceNumber);
        [LibraryImport(NativeLibraries.LibUsb)] public static partial int libusb_clear_halt(nint handle, byte endpoint);
        [LibraryImport(NativeLibraries.LibUsb)] public static partial Transfer* libusb_alloc_transfer(int isoPackets);
        [LibraryImport(NativeLibraries.LibUsb)] public static partial void libusb_free_transfer(Transfer* transfer);
        [LibraryImport(NativeLibraries.LibUsb)] public static partial int libusb_submit_transfer(Transfer* transfer);
        [LibraryImport(NativeLibraries.LibUsb)] public static partial int libusb_cancel_transfer(Transfer* transfer);
        [LibraryImport(NativeLibraries.LibUsb)] public static partial int libusb_handle_events_timeout_completed(nint context, void* timeval, int* completed);
        [LibraryImport(NativeLibraries.LibUsb)] public static partial void libusb_interrupt_event_handler(nint context);
        [LibraryImport(NativeLibraries.LibUsb)] public static partial int libusb_pollfds_handle_timeouts(nint context);
    }
}

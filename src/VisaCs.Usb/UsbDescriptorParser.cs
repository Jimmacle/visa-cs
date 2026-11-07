using System.Buffers.Binary;
using System.Text;

namespace VisaCs.Usb;

/// <summary>Parses standard USB descriptors, and reads string descriptors from a device.</summary>
public static class UsbDescriptorParser
{
    /// <summary>Device descriptor type.</summary>
    public const byte DescriptorDevice = 1;
    /// <summary>Configuration descriptor type.</summary>
    public const byte DescriptorConfiguration = 2;
    /// <summary>String descriptor type.</summary>
    public const byte DescriptorString = 3;
    /// <summary>Interface descriptor type.</summary>
    public const byte DescriptorInterface = 4;
    /// <summary>Endpoint descriptor type.</summary>
    public const byte DescriptorEndpoint = 5;

    /// <summary>The fields of a device descriptor used by this library.</summary>
    /// <param name="VendorId">idVendor.</param>
    /// <param name="ProductId">idProduct.</param>
    /// <param name="DeviceRelease">bcdDevice.</param>
    /// <param name="DeviceClass">bDeviceClass.</param>
    /// <param name="ManufacturerIndex">iManufacturer string index.</param>
    /// <param name="ProductIndex">iProduct string index.</param>
    /// <param name="SerialNumberIndex">iSerialNumber string index.</param>
    /// <param name="NumConfigurations">bNumConfigurations.</param>
    /// <param name="MaxPacketSize0">bMaxPacketSize0.</param>
    public readonly record struct DeviceDescriptor(ushort VendorId, ushort ProductId, ushort DeviceRelease, byte DeviceClass,
        byte ManufacturerIndex, byte ProductIndex, byte SerialNumberIndex, byte NumConfigurations, byte MaxPacketSize0);

    /// <summary>
    /// Parses an 18-byte device descriptor. Throws <see cref="VisaException"/> (<see cref="VisaStatus.ErrorIO"/>)
    /// when the data is not a device descriptor.
    /// </summary>
    public static DeviceDescriptor ParseDevice(ReadOnlySpan<byte> d)
    {
        if (d.Length < 18 || d[1] != DescriptorDevice) throw new VisaException(VisaStatus.ErrorIO, "Invalid USB device descriptor.");
        return new DeviceDescriptor(
            BinaryPrimitives.ReadUInt16LittleEndian(d[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(d[10..]),
            BinaryPrimitives.ReadUInt16LittleEndian(d[12..]),
            d[4], d[14], d[15], d[16], d[17], d[7]);
    }

    /// <summary>
    /// Parses a full configuration descriptor (with its interface and endpoint descriptors) and returns alternate
    /// setting 0 of each interface. Malformed trailing data is ignored.
    /// </summary>
    public static List<UsbInterfaceInfo> ParseConfiguration(ReadOnlySpan<byte> data)
    {
        var interfaces = new List<UsbInterfaceInfo>();
        int offset = 0;
        byte number = 0, alt = 0, cls = 0, sub = 0, proto = 0;
        List<UsbEndpointInfo>? endpoints = null;

        void Flush()
        {
            if (endpoints is null) return;
            if (alt == 0 && interfaces.All(i => i.Number != number))
                interfaces.Add(new UsbInterfaceInfo { Number = number, AlternateSetting = alt, Class = cls, SubClass = sub, Protocol = proto, Endpoints = endpoints });
            endpoints = null;
        }

        while (offset + 2 <= data.Length)
        {
            int length = data[offset];
            if (length < 2 || offset + length > data.Length) break;
            byte type = data[offset + 1];
            var d = data.Slice(offset, length);
            if (type == DescriptorInterface && length >= 9)
            {
                Flush();
                number = d[2];
                alt = d[3];
                cls = d[5];
                sub = d[6];
                proto = d[7];
                endpoints = new List<UsbEndpointInfo>();
            }
            else if (type == DescriptorEndpoint && length >= 7 && endpoints is not null)
            {
                endpoints.Add(new UsbEndpointInfo
                {
                    Address = d[2],
                    Type = (UsbTransferType)(d[3] & 3),
                    MaxPacketSize = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(d[4..]) & 0x7FF),
                    Interval = d[6],
                });
            }
            offset += length;
        }
        Flush();
        interfaces.Sort((a, b) => a.Number.CompareTo(b.Number));
        return interfaces;
    }

    /// <summary>Decodes a UTF-16LE string descriptor; returns "" for anything else.</summary>
    public static string ParseString(ReadOnlySpan<byte> d)
    {
        if (d.Length < 2 || d[1] != DescriptorString) return "";
        int length = Math.Min(d[0], d.Length);
        return length <= 2 ? "" : Encoding.Unicode.GetString(d[2..length]).TrimEnd('\0');
    }

    /// <summary>
    /// Reads string descriptor <paramref name="index"/> with GET_DESCRIPTOR requests, in the first language the device
    /// lists (US English if it lists none). Returns null for index 0 and when the device does not answer: transfer
    /// errors are not reported.
    /// </summary>
    public static string? ReadString(IUsbDeviceHandle handle, byte index, int timeoutMilliseconds = 1000)
    {
        if (index == 0) return null;
        try
        {
            Span<byte> buffer = stackalloc byte[255];
            int n = handle.ControlTransfer(0x80, 6, 0x0300, 0, buffer[..4], timeoutMilliseconds);
            ushort language = n >= 4 ? BinaryPrimitives.ReadUInt16LittleEndian(buffer[2..]) : (ushort)0x0409;
            n = handle.ControlTransfer(0x80, 6, (ushort)(0x0300 | index), language, buffer, timeoutMilliseconds);
            return ParseString(buffer[..n]);
        }
        catch (VisaException)
        {
            return null;
        }
    }
}

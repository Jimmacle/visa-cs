namespace VisaCs;

/// <summary>Byte order of binary data and register access (VI_BIG_ENDIAN / VI_LITTLE_ENDIAN).</summary>
public enum ByteOrder
{
    /// <summary>Most significant byte first (VI_BIG_ENDIAN).</summary>
    BigEndian = 0,
    /// <summary>Least significant byte first (VI_LITTLE_ENDIAN); the PCI byte order.</summary>
    LittleEndian = 1,
}

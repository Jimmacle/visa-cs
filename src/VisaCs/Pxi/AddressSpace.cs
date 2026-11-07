namespace VisaCs;

/// <summary>Address spaces for register-based access (VI_PXI_CFG_SPACE, VI_PXI_BAR0_SPACE ... VI_PXI_BAR5_SPACE).</summary>
public enum AddressSpace
{
    /// <summary>PCI configuration space (VI_PXI_CFG_SPACE).</summary>
    PxiConfig = 10,
    /// <summary>Base address register 0 (VI_PXI_BAR0_SPACE).</summary>
    PxiBar0 = 11,
    /// <summary>Base address register 1.</summary>
    PxiBar1 = 12,
    /// <summary>Base address register 2.</summary>
    PxiBar2 = 13,
    /// <summary>Base address register 3.</summary>
    PxiBar3 = 14,
    /// <summary>Base address register 4.</summary>
    PxiBar4 = 15,
    /// <summary>Base address register 5 (VI_PXI_BAR5_SPACE).</summary>
    PxiBar5 = 16,
}

/// <summary>Byte order of register data (VI_BIG_ENDIAN / VI_LITTLE_ENDIAN).</summary>
public enum ByteOrder
{
    /// <summary>Most significant byte first (VI_BIG_ENDIAN).</summary>
    BigEndian = 0,
    /// <summary>Least significant byte first (VI_LITTLE_ENDIAN); the PCI byte order.</summary>
    LittleEndian = 1,
}

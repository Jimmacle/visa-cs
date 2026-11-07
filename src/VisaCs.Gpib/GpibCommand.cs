namespace VisaCs.Gpib;

/// <summary>IEEE 488.1 command bytes, sent with ATN asserted through <see cref="IGpibBoard.SendCommand"/>.</summary>
public static class GpibCommand
{
    /// <summary>Go To Local (GTL): addressed listeners return to local control.</summary>
    public const byte GoToLocal = 0x01;

    /// <summary>Selected Device Clear (SDC): clears the addressed listeners.</summary>
    public const byte SelectedDeviceClear = 0x04;

    /// <summary>Group Execute Trigger (GET): triggers the addressed listeners.</summary>
    public const byte GroupExecuteTrigger = 0x08;

    /// <summary>Local Lockout (LLO): disables the local (front panel) controls of all devices.</summary>
    public const byte LocalLockout = 0x11;

    /// <summary>Device Clear (DCL): clears all devices.</summary>
    public const byte DeviceClear = 0x14;

    /// <summary>Unlisten (UNL): unaddresses all listeners.</summary>
    public const byte Unlisten = 0x3F;

    /// <summary>Untalk (UNT): unaddresses the talker.</summary>
    public const byte Untalk = 0x5F;

    /// <summary>My Listen Address (MLA): addresses the device at <paramref name="primaryAddress"/> (0 to 30) to listen.</summary>
    public static byte Listen(int primaryAddress) => (byte)(0x20 | CheckAddress(primaryAddress));

    /// <summary>My Talk Address (MTA): addresses the device at <paramref name="primaryAddress"/> (0 to 30) to talk.</summary>
    public static byte Talk(int primaryAddress) => (byte)(0x40 | CheckAddress(primaryAddress));

    /// <summary>Secondary address (MSA) <paramref name="secondaryAddress"/> (0 to 30), sent after a listen or talk address.</summary>
    public static byte Secondary(int secondaryAddress) => (byte)(0x60 | CheckAddress(secondaryAddress));

    private static int CheckAddress(int address)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(address);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(address, 30);
        return address;
    }
}

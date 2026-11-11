using System.Buffers.Binary;
using VisaCs.Internal;

namespace VisaCs.Usb;

/// <summary>Checks, messages and the setup packet layout shared by the built-in backends.</summary>
internal static class UsbTransferHelpers
{
    /// <summary>Size of a control transfer's setup packet.</summary>
    public const int SetupPacketSize = 8;

    public static string OperationName(byte endpoint) => (endpoint & 0x80) != 0 ? "read" : "write";

    /// <summary>A control transfer's data stage is limited by the 16-bit wLength field.</summary>
    public static void CheckControlLength(int length)
    {
        if (length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException("buffer", length, "A control transfer carries at most 65535 bytes.");
    }

    /// <summary>Writes the 8-byte setup packet of a control transfer (little-endian fields).</summary>
    public static void WriteSetupPacket(Span<byte> destination, byte requestType, byte request, ushort value, ushort index, int length)
    {
        destination[0] = requestType;
        destination[1] = request;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], value);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], index);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], (ushort)length);
    }

    /// <summary>The deadline of one transfer: negative waits without limit, and 0 has expired at once.</summary>
    public static Deadline TransferDeadline(int timeoutMilliseconds, CancellationToken cancellationToken) =>
        Deadline.FromMilliseconds(timeoutMilliseconds).WithCancellation(cancellationToken);

    /// <summary>A transfer timeout in the form of <see cref="Task.Wait(int)"/>: negative waits without limit, 0 does not wait.</summary>
    public static int WaitMilliseconds(int timeoutMilliseconds) => timeoutMilliseconds < 0 ? Timeout.Infinite : timeoutMilliseconds;

    /// <summary>Waits for a task without throwing its exception; false on timeout.</summary>
    public static bool Wait(Task task, int milliseconds)
    {
        try
        {
            return task.Wait(milliseconds);
        }
        catch (AggregateException)
        {
            return true;
        }
    }

}

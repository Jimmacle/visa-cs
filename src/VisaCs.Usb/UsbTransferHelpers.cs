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

    /// <summary>The deadline of one transfer: 0 or less waits without limit.</summary>
    public static Deadline TransferDeadline(int timeoutMilliseconds, CancellationToken cancellationToken) =>
        Deadline.FromMilliseconds(timeoutMilliseconds <= 0 ? -1 : timeoutMilliseconds).WithCancellation(cancellationToken);

    /// <summary>A transfer timeout in the form of <see cref="Task.Wait(int)"/>: 0 or less waits without limit.</summary>
    public static int WaitMilliseconds(int timeoutMilliseconds) => timeoutMilliseconds <= 0 ? Timeout.Infinite : timeoutMilliseconds;

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

    public static VisaTimeoutException TimedOut(string operation) => new($"USB {operation} timed out.");

    public static VisaException Disconnected(string operation) =>
        new(VisaStatus.ErrorConnectionLost, $"USB device disconnected during {operation}.");

    public static VisaException Stalled(string operation) => new(VisaStatus.ErrorIO, $"USB endpoint stalled during {operation}.");

    public static VisaException Failed(string operation, string detail) => new(VisaStatus.ErrorIO, $"USB {operation} failed ({detail}).");

    public static ObjectDisposedException Disposed() =>
        new(nameof(IUsbDeviceHandle), "The USB device handle was disposed during the transfer.");

    /// <summary>
    /// The exception for a transfer that the backend cancelled before it completed: the caller's token, the handle
    /// being disposed, or else the transfer's own timeout. Data that an IN transfer had received is lost.
    /// </summary>
    public static Exception Interrupted(string operation, CancellationToken cancellationToken, bool disposed)
    {
        if (cancellationToken.IsCancellationRequested) return AsyncIO.Canceled(TimedOut(operation), cancellationToken);
        if (disposed) return Disposed();
        return TimedOut(operation);
    }
}

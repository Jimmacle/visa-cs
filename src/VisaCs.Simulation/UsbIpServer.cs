using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VisaCs.Usb;

namespace VisaCs.Simulation;

/// <summary>
/// Exports a <see cref="SimulatedUsbTmcDevice"/> over USB/IP (the protocol of the Linux usbip tools and usbip-win).
/// Attached to a virtual host controller (Linux vhci_hcd, usbip-win), the simulated instrument appears as a real
/// USB device to the operating system, so the WinUSB, usbfs and libusb backends can be tested end to end.
/// </summary>
public sealed class UsbIpServer : IDisposable
{
    private const ushort Version = 0x0111;
    private const ushort OpRequestDeviceList = 0x8005, OpReplyDeviceList = 0x0005;
    private const ushort OpRequestImport = 0x8003, OpReplyImport = 0x0003;
    private const uint CmdSubmit = 1, CmdUnlink = 2, RetSubmit = 3, RetUnlink = 4;
    private const int EConnReset = 104, EPipe = 32, ETimedOut = 110;

    private readonly SimulatedUsbTmcDevice _device;
    private readonly TcpListener _listener;
    private readonly List<Socket> _connections = new();
    private volatile bool _disposed;

    /// <summary>Creates a USB/IP server exporting a simulated USBTMC device. Port 0 picks a free port.</summary>
    public UsbIpServer(SimulatedUsbTmcDevice device, IPAddress? bindAddress = null, int port = 0)
    {
        _device = device;
        _listener = new TcpListener(bindAddress ?? IPAddress.Loopback, port);
    }

    /// <summary>Bus ID under which the device is exported.</summary>
    public string BusId { get; init; } = "1-1";

    /// <summary>The TCP port the server listens on (valid after Start).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Number of URBs processed (for diagnostics).</summary>
    public int UrbCount => Volatile.Read(ref _urbCount);

    /// <summary>Number of URBs the host cancelled with CMD_UNLINK.</summary>
    public int UnlinkCount => Volatile.Read(ref _unlinkCount);

    private int _urbCount;
    private int _unlinkCount;

    /// <summary>Starts listening; returns this server.</summary>
    public UsbIpServer Start()
    {
        _listener.Start();
        new Thread(AcceptLoop) { IsBackground = true, Name = "USB/IP accept" }.Start();
        return this;
    }

    private void AcceptLoop()
    {
        while (!_disposed)
        {
            Socket socket;
            try { socket = _listener.AcceptSocket(); }
            catch { if (_disposed) return; continue; }
            socket.NoDelay = true;
            lock (_connections) _connections.Add(socket);
            new Thread(() =>
            {
                try { Serve(socket); }
                catch { /* host detached */ }
                finally
                {
                    lock (_connections) _connections.Remove(socket);
                    try { socket.Dispose(); } catch { /* ignore */ }
                }
            }) { IsBackground = true, Name = "USB/IP connection" }.Start();
        }
    }

    private static void ReceiveExact(Socket socket, Span<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            int n = socket.Receive(buffer);
            if (n <= 0) throw new IOException("USB/IP connection closed.");
            buffer = buffer[n..];
        }
    }

    /// <summary>The usbip_usb_device record (312 bytes) describing the exported device.</summary>
    private byte[] DeviceRecord()
    {
        var record = new byte[312];
        Encoding.ASCII.GetBytes($"/sys/devices/platform/visacs/usb1/{BusId}").CopyTo(record, 0);
        Encoding.ASCII.GetBytes(BusId).CopyTo(record, 256);
        var span = record.AsSpan(288);
        BinaryPrimitives.WriteUInt32BigEndian(span, 1);          // busnum
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], 2);     // devnum
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], 3);     // speed: USB_SPEED_HIGH
        BinaryPrimitives.WriteUInt16BigEndian(span[12..], _device.Info.VendorId);
        BinaryPrimitives.WriteUInt16BigEndian(span[14..], _device.Info.ProductId);
        BinaryPrimitives.WriteUInt16BigEndian(span[16..], 0x0100); // bcdDevice
        span[18] = 0; // bDeviceClass (per interface)
        span[19] = 0;
        span[20] = 0;
        span[21] = 1; // bConfigurationValue
        span[22] = 1; // bNumConfigurations
        span[23] = 1; // bNumInterfaces
        return record;
    }

    private void Serve(Socket socket)
    {
        Span<byte> op = stackalloc byte[8];
        ReceiveExact(socket, op);
        ushort code = BinaryPrimitives.ReadUInt16BigEndian(op[2..]);
        if (code == OpRequestDeviceList)
        {
            var reply = new byte[12 + 312 + 4];
            BinaryPrimitives.WriteUInt16BigEndian(reply, Version);
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), OpReplyDeviceList);
            BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(8), 1);
            DeviceRecord().CopyTo(reply, 12);
            // One interface: class, subclass, protocol, padding.
            reply[324] = 0xFE;
            reply[325] = 0x03;
            reply[326] = (byte)(_device.Usb488 ? 1 : 0);
            socket.Send(reply);
            return;
        }
        if (code != OpRequestImport) throw new IOException($"Unexpected USB/IP operation 0x{code:X4}.");

        var busId = new byte[32];
        ReceiveExact(socket, busId);
        string requested = Encoding.ASCII.GetString(busId).TrimEnd('\0');
        var importReply = new byte[8 + 312];
        BinaryPrimitives.WriteUInt16BigEndian(importReply, Version);
        BinaryPrimitives.WriteUInt16BigEndian(importReply.AsSpan(2), OpReplyImport);
        if (requested != BusId)
        {
            BinaryPrimitives.WriteUInt32BigEndian(importReply.AsSpan(4), 1); // status: error
            socket.Send(importReply.AsSpan(0, 8));
            return;
        }
        DeviceRecord().CopyTo(importReply, 8);
        socket.Send(importReply);
        UrbLoop(socket);
    }

    private sealed class PendingUrb
    {
        public required uint SequenceNumber { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }
        public bool Completed; // completed or unlinked
    }

    private void UrbLoop(Socket socket)
    {
        var sendLock = new Lock();
        var pending = new ConcurrentDictionary<uint, PendingUrb>();
        var header = new byte[48];

        void Reply(uint command, uint sequence, int status, int actualLength, ReadOnlySpan<byte> data)
        {
            var packet = new byte[48 + data.Length];
            BinaryPrimitives.WriteUInt32BigEndian(packet, command);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), sequence);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(20), status);
            if (command == RetSubmit) BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(24), actualLength);
            data.CopyTo(packet.AsSpan(48));
            lock (sendLock) socket.Send(packet);
        }

        while (!_disposed)
        {
            ReceiveExact(socket, header);
            uint command = BinaryPrimitives.ReadUInt32BigEndian(header);
            uint sequence = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            uint direction = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
            uint endpoint = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16));
            Interlocked.Increment(ref _urbCount);

            if (command == CmdUnlink)
            {
                uint target = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20));
                int status = 0;
                if (pending.TryRemove(target, out var urb) && !Interlocked.Exchange(ref urb.Completed, true))
                {
                    urb.Cancellation.Cancel();
                    status = -EConnReset;
                    Interlocked.Increment(ref _unlinkCount);
                }
                Reply(RetUnlink, sequence, status, 0, ReadOnlySpan<byte>.Empty);
                continue;
            }
            if (command != CmdSubmit) throw new IOException($"Unexpected USB/IP command {command}.");

            int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(24));
            int packets = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(32));
            var setup = header.AsSpan(40, 8).ToArray();
            byte[] outData = [];
            if (direction == 0 && length > 0)
            {
                outData = new byte[length];
                ReceiveExact(socket, outData);
            }
            if (packets > 0 && packets != -1)
            {
                var skip = new byte[16 * packets]; // ISO descriptors: not supported, drain them
                ReceiveExact(socket, skip);
            }

            if (endpoint == 0)
            {
                // Control transfer: answered immediately.
                byte requestType = setup[0], request = setup[1];
                ushort value = BinaryPrimitives.ReadUInt16LittleEndian(setup.AsSpan(2));
                ushort wLength = BinaryPrimitives.ReadUInt16LittleEndian(setup.AsSpan(6));
                try
                {
                    if ((requestType & 0x80) != 0)
                    {
                        var buffer = new byte[Math.Min(wLength, length)];
                        int n = _device.Control(requestType, request, value, buffer);
                        Reply(RetSubmit, sequence, 0, n, buffer.AsSpan(0, n));
                    }
                    else
                    {
                        _device.Control(requestType, request, value, outData);
                        Reply(RetSubmit, sequence, 0, outData.Length, ReadOnlySpan<byte>.Empty);
                    }
                }
                catch (VisaException)
                {
                    Reply(RetSubmit, sequence, -EPipe, 0, ReadOnlySpan<byte>.Empty);
                }
                continue;
            }

            if (direction == 0)
            {
                try
                {
                    int n = _device.HandleBulkOut(outData);
                    Reply(RetSubmit, sequence, 0, n, ReadOnlySpan<byte>.Empty);
                }
                catch (VisaException)
                {
                    Reply(RetSubmit, sequence, -EPipe, 0, ReadOnlySpan<byte>.Empty);
                }
                continue;
            }

            // IN transfers wait for data; the host may cancel them with CMD_UNLINK.
            var urbState = new PendingUrb { SequenceNumber = sequence, Cancellation = new CancellationTokenSource() };
            pending[sequence] = urbState;
            byte ep = (byte)(0x80 | endpoint);
            bool interrupt = _device.Info.Interfaces[0].Endpoints.Any(e => e.Address == ep && e.Type == UsbTransferType.Interrupt);
            ThreadPool.QueueUserWorkItem(state =>
            {
                var buffer = new byte[length];
                int status = 0, n = 0;
                try
                {
                    n = interrupt
                        ? _device.TakeInterrupt(buffer, 0, urbState.Cancellation.Token)
                        : _device.HandleBulkIn(buffer, 0, urbState.Cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return; // unlinked: RET_UNLINK already sent
                }
                catch (VisaTimeoutException)
                {
                    status = -ETimedOut;
                }
                catch (VisaException)
                {
                    status = -EPipe;
                }
                if (Interlocked.Exchange(ref urbState.Completed, true)) return;
                pending.TryRemove(sequence, out _);
                try { Reply(RetSubmit, sequence, status, n, buffer.AsSpan(0, n)); } catch { /* detached */ }
            });
        }
    }

    /// <summary>Stops the server and closes its connections.</summary>
    public void Dispose()
    {
        _disposed = true;
        try { _listener.Stop(); } catch { /* ignore */ }
        lock (_connections)
        {
            foreach (var c in _connections)
            {
                try { c.Dispose(); } catch { /* ignore */ }
            }
        }
    }
}

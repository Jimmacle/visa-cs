using System.Buffers.Binary;
using System.Text;
using VisaCs.Internal;
using VisaCs.Usb;

namespace VisaCs.Simulation;

/// <summary>
/// A USB backend whose devices are simulated USBTMC/USB488 instruments. A device can have one open handle at a time,
/// like a real one; several backends may list the same <see cref="SimulatedUsbTmcDevice"/>.
/// </summary>
public sealed class SimulatedUsbBackend : IUsbBackend
{
    /// <summary>The attached simulated devices. Change the list only while no other thread uses the backend.</summary>
    public List<SimulatedUsbTmcDevice> Devices { get; } = new();

    /// <inheritdoc/>
    public string Name => "Simulated USB";

    /// <inheritdoc/>
    public IReadOnlyList<UsbDeviceInfo> EnumerateDevices() => Devices.Select(d => d.Info).ToList();

    /// <inheritdoc/>
    public IUsbDeviceHandle Open(UsbDeviceInfo device, int interfaceNumber)
    {
        var sim = Devices.FirstOrDefault(d => d.Info.VendorId == device.VendorId && d.Info.ProductId == device.ProductId &&
                                              d.Info.SerialNumber == device.SerialNumber)
            ?? throw new UsbException(UsbError.NotFound, "Simulated device not found.");
        if (interfaceNumber != sim.Info.Interfaces[0].Number)
            throw new UsbException(UsbError.NotFound, $"Interface {interfaceNumber} of {device} not found.");
        return sim.Open();
    }

    /// <summary>Does nothing: the simulated devices outlive the backend.</summary>
    public void Dispose()
    {
    }
}

/// <summary>Implements the device side of USBTMC / USB488 on top of a <see cref="SimulatedInstrument"/>.</summary>
public sealed class SimulatedUsbTmcDevice
{
    private const byte BulkOut = 0x01, BulkIn = 0x82, InterruptIn = 0x83;
    private readonly SimulatedInstrument _instrument;
    private readonly object _sync = new();
    private readonly ChangeSignal _changed = new();
    private readonly List<byte> _input = new();
    private readonly Queue<byte[]> _output = new();
    private readonly Queue<byte[]> _interrupts = new();
    private byte[]? _current;
    private int _currentOffset;
    private (byte Tag, int Size, bool TermChar, byte TermCharValue, bool Vendor)? _request;
    private readonly List<byte> _vendorData = new();
    private byte[]? _transfer;
    private int _transferOffset;
    private bool _opened;
    private Task _pending = Task.CompletedTask;
    private int _generation;

    /// <summary>Creates a simulated USBTMC device that forwards messages to an instrument.</summary>
    public SimulatedUsbTmcDevice(SimulatedInstrument instrument, ushort vendorId = 0x1234, ushort productId = 0x5678, string serialNumber = "SIM0001",
        bool usb488 = true, bool interruptEndpoint = true, bool supportsTermChar = true, ushort maxPacketSize = 512)
    {
        _instrument = instrument;
        Usb488 = usb488;
        HasInterruptEndpoint = interruptEndpoint;
        SupportsTermChar = supportsTermChar;
        MaxPacketSize = maxPacketSize;
        var endpoints = new List<UsbEndpointInfo>
        {
            new() { Address = BulkOut, Type = UsbTransferType.Bulk, MaxPacketSize = maxPacketSize },
            new() { Address = BulkIn, Type = UsbTransferType.Bulk, MaxPacketSize = maxPacketSize },
        };
        if (interruptEndpoint) endpoints.Add(new() { Address = InterruptIn, Type = UsbTransferType.Interrupt, MaxPacketSize = 8, Interval = 1 });
        Info = new UsbDeviceInfo
        {
            VendorId = vendorId,
            ProductId = productId,
            SerialNumber = serialNumber,
            Manufacturer = "VisaCs",
            Product = "Simulated USBTMC Instrument",
            Interfaces =
            [
                new UsbInterfaceInfo { Number = 0, Class = 0xFE, SubClass = 0x03, Protocol = (byte)(usb488 ? 1 : 0), Endpoints = endpoints },
            ],
            Location = $"sim:{serialNumber}",
        };
        instrument.ServiceRequested += stb => AddInterrupt([0x81, stb]);
    }

    /// <summary>Device and interface descriptors as returned by enumeration.</summary>
    public UsbDeviceInfo Info { get; }
    /// <summary>True if the interface reports the USB488 subclass protocol.</summary>
    public bool Usb488 { get; }
    /// <summary>True if the interface has an interrupt-IN endpoint for SRQ.</summary>
    public bool HasInterruptEndpoint { get; }
    /// <summary>True if the device supports TermChar-terminated reads.</summary>
    public bool SupportsTermChar { get; }
    /// <summary>Bulk endpoint maximum packet size.</summary>
    public ushort MaxPacketSize { get; }
    /// <summary>True while the device is in remote state (USB488 REN_CONTROL).</summary>
    public bool IsRemote { get; private set; }
    /// <summary>True while local lockout is in effect.</summary>
    public bool LocalLockout { get; private set; }
    /// <summary>Number of INITIATE_CLEAR requests received.</summary>
    public int ClearCount { get; private set; }
    /// <summary>Number of INITIATE_ABORT_BULK_OUT/IN requests received.</summary>
    public int AbortCount { get; private set; }

    /// <summary>Number of VENDOR_SPECIFIC_OUT messages received. Their data is returned by REQUEST_VENDOR_SPECIFIC_IN.</summary>
    public int VendorMessageCount { get; private set; }

    /// <summary>Delay before responding to bulk-in requests, to exercise timeouts.</summary>
    public int ResponseDelayMilliseconds { get; set; }

    /// <summary>
    /// Number of Bulk-OUT headers whose bTag repeated the one of the previous header. USBTMC requires a new bTag for
    /// each header, whichever session sends it.
    /// </summary>
    public int RepeatedTagCount { get; private set; }

    private int _lastTag = -1;

    /// <summary>Queues a packet on the interrupt-IN endpoint, as the device would send it.</summary>
    public void SendInterrupt(byte[] packet) => AddInterrupt(packet);

    internal IUsbDeviceHandle Open()
    {
        lock (_sync)
        {
            if (_opened) throw new UsbException(UsbError.Busy, "The simulated device is already open.");
            _opened = true;
        }
        return new Handle(this, Info.Interfaces[0]);
    }

    private void ResetBuffers()
    {
        _generation++;
        _input.Clear();
        _output.Clear();
        _vendorData.Clear();
        _current = null;
        _currentOffset = 0;
        _request = null;
        _transfer = null;
        _transferOffset = 0;
    }

    /// <summary>
    /// A handle on the simulated device. Control transfers and bulk-OUT complete at once; bulk-IN and interrupt-IN
    /// transfers wait (like a device answering with NAK) until data is available, the timeout passes, the token is
    /// cancelled or the handle is disposed.
    /// </summary>
    private sealed class Handle(SimulatedUsbTmcDevice device, UsbInterfaceInfo info) : IUsbDeviceHandle
    {
        private readonly CancellationTokenSource _closed = new();

        public UsbDeviceInfo Device => device.Info;

        public UsbInterfaceInfo Interface => info;

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);

        private static ObjectDisposedException Disposed() =>
            new(nameof(IUsbDeviceHandle), "The USB device handle was disposed during the transfer.");

        public int ControlTransfer(byte requestType, byte request, ushort value, ushort index, Span<byte> buffer, int timeoutMilliseconds)
        {
            ThrowIfDisposed();
            return device.Control(requestType, request, value, buffer);
        }

        public int BulkTransfer(byte endpoint, Span<byte> buffer, int timeoutMilliseconds)
        {
            ThrowIfDisposed();
            if (endpoint == BulkOut) return device.HandleBulkOut(buffer);
            try
            {
                return device.HandleBulkIn(buffer, timeoutMilliseconds, _closed.Token);
            }
            catch (OperationCanceledException)
            {
                throw Disposed();
            }
        }

        public int InterruptTransfer(byte endpoint, Span<byte> buffer, int timeoutMilliseconds)
        {
            ThrowIfDisposed();
            try
            {
                return device.TakeInterrupt(buffer, timeoutMilliseconds, _closed.Token);
            }
            catch (OperationCanceledException)
            {
                throw Disposed();
            }
        }

        public ValueTask<int> ControlTransferAsync(byte requestType, byte request, ushort value, ushort index, Memory<byte> buffer,
            int timeoutMilliseconds, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            return new(device.Control(requestType, request, value, buffer.Span));
        }

        public ValueTask<int> BulkTransferAsync(byte endpoint, Memory<byte> buffer, int timeoutMilliseconds, CancellationToken cancellationToken = default)
        {
            if (endpoint != BulkOut) return WaitAsync(token => device.HandleBulkInAsync(buffer, timeoutMilliseconds, token, async: true), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            return new(device.HandleBulkOut(buffer.Span));
        }

        public ValueTask<int> InterruptTransferAsync(byte endpoint, Memory<byte> buffer, int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
            WaitAsync(token => device.TakeInterruptAsync(buffer, timeoutMilliseconds, token, async: true), cancellationToken);

        /// <summary>Runs a wait that also ends, with <see cref="ObjectDisposedException"/>, when the handle is disposed.</summary>
        private async ValueTask<int> WaitAsync(Func<CancellationToken, ValueTask<int>> wait, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            try
            {
                return await wait(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(ex.Message, ex, cancellationToken);
                if (Volatile.Read(ref _disposed)) throw Disposed();
                throw;
            }
        }

        public void ClearHalt(byte endpoint) => ThrowIfDisposed();

        public void Dispose()
        {
            lock (device._sync)
            {
                if (_disposed) return;
                _disposed = true;
                device._opened = false;
            }
            _closed.Cancel();
        }

        private bool _disposed;
    }

    /// <summary>Standard USB device descriptor (USB 2.0, one configuration).</summary>
    public byte[] DeviceDescriptor =>
    [
        18, 1, 0x00, 0x02, 0, 0, 0, 64, (byte)Info.VendorId, (byte)(Info.VendorId >> 8),
        (byte)Info.ProductId, (byte)(Info.ProductId >> 8), 0x00, 0x01, 1, 2, 3, 1,
    ];

    /// <summary>Full configuration descriptor: one USBTMC interface with its endpoints.</summary>
    public byte[] ConfigurationDescriptor
    {
        get
        {
            var endpoints = Info.Interfaces[0].Endpoints;
            int total = 9 + 9 + 7 * endpoints.Count;
            var d = new List<byte> { 9, 2, (byte)total, (byte)(total >> 8), 1, 1, 0, 0x80, 50 };
            d.AddRange([9, 4, 0, 0, (byte)endpoints.Count, 0xFE, 0x03, (byte)(Usb488 ? 1 : 0), 0]);
            foreach (var e in endpoints)
                d.AddRange([7, 5, e.Address, (byte)e.Type, (byte)e.MaxPacketSize, (byte)(e.MaxPacketSize >> 8), (byte)(e.Type == UsbTransferType.Interrupt ? 4 : 0)]);
            return d.ToArray();
        }
    }

    /// <summary>Handles a control transfer (standard and USBTMC class requests). Throws for a stall.</summary>
    internal int Control(byte requestType, byte request, ushort value, Span<byte> buffer)
    {
        if (requestType == 0x80 && request == 6)
        {
            byte[] descriptor = (value >> 8) switch
            {
                1 => DeviceDescriptor,
                2 => ConfigurationDescriptor,
                3 => StringDescriptor(value & 0xFF),
                _ => throw new UsbException(UsbError.Stall, "Stall"),
            };
            int n = Math.Min(descriptor.Length, buffer.Length);
            descriptor.AsSpan(0, n).CopyTo(buffer);
            return n;
        }
        switch ((requestType, request))
        {
            case (0x00, 9): // SET_CONFIGURATION
            case (0x01, 11): // SET_INTERFACE
            case (0x00, 1): // CLEAR_FEATURE (device)
            case (0x02, 1): // CLEAR_FEATURE (endpoint halt)
            case (0x00, 3): // SET_FEATURE
                return 0;
            case (0x80, 0): // GET_STATUS
            case (0x81, 0):
            case (0x82, 0):
                buffer[..Math.Min(2, buffer.Length)].Clear();
                return Math.Min(2, buffer.Length);
            case (0x80, 8): // GET_CONFIGURATION
                buffer[0] = 1;
                return 1;
            case (0x81, 10): // GET_INTERFACE
                buffer[0] = 0;
                return 1;
        }

        Span<byte> response = stackalloc byte[24];
        response.Clear();
        int length;
        lock (_sync)
        {
            switch ((requestType, request))
            {
                case (0xA1, 7): // GET_CAPABILITIES
                    response[0] = 1;
                    response[2] = 0x00; response[3] = 0x01;
                    response[4] = 0x04;
                    response[5] = (byte)(SupportsTermChar ? 1 : 0);
                    if (Usb488)
                    {
                        response[12] = 0x00; response[13] = 0x01;
                        response[14] = 0x07;
                        response[15] = 0x0F;
                    }
                    length = 24;
                    break;
                case (0xA1, 5): // INITIATE_CLEAR
                    ResetBuffers();
                    ClearCount++;
                    _instrument.DeviceClear();
                    response[0] = 1;
                    length = 1;
                    break;
                case (0xA1, 6): // CHECK_CLEAR_STATUS
                    response[0] = 1;
                    length = 2;
                    break;
                case (0xA1, 128): // READ_STATUS_BYTE
                {
                    if (!Usb488) throw new UsbException(UsbError.Stall, "Stall");
                    byte tag = (byte)value;
                    byte stb = _instrument.GetStatusByte(_current is not null || _output.Count > 0);
                    response[0] = 1;
                    response[1] = tag;
                    if (HasInterruptEndpoint) AddInterrupt([(byte)(0x80 | tag), stb]);
                    else response[2] = stb;
                    length = 3;
                    break;
                }
                case (0xA1, 160): // REN_CONTROL
                    IsRemote = (value & 1) != 0;
                    response[0] = 1;
                    length = 1;
                    break;
                case (0xA1, 161): // GO_TO_LOCAL
                    IsRemote = false;
                    response[0] = 1;
                    length = 1;
                    break;
                case (0xA1, 162): // LOCAL_LOCKOUT
                    LocalLockout = true;
                    response[0] = 1;
                    length = 1;
                    break;
                case (0xA1, 64): // INDICATOR_PULSE
                    response[0] = 1;
                    length = 1;
                    break;
                case (0xA2, 1): // INITIATE_ABORT_BULK_OUT
                case (0xA2, 3): // INITIATE_ABORT_BULK_IN
                    AbortCount++;
                    _request = null;
                    _transfer = null;
                    response[0] = 1;
                    response[1] = (byte)value;
                    length = 2;
                    break;
                case (0xA2, 2):
                case (0xA2, 4):
                    response[0] = 1;
                    length = 8;
                    break;
                default:
                    throw new UsbException(UsbError.Stall, "Stall");
            }
        }
        int count = Math.Min(length, buffer.Length);
        response[..count].CopyTo(buffer);
        return count;
    }

    private void AddInterrupt(byte[] packet)
    {
        lock (_sync)
        {
            _interrupts.Enqueue(packet);
            _changed.Pulse();
        }
    }

    /// <summary>Waits for an interrupt-IN packet (status byte responses and SRQ notifications).</summary>
    internal int TakeInterrupt(Span<byte> buffer, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        var temp = new byte[buffer.Length];
        int n = AsyncIO.Complete(TakeInterruptAsync(temp, timeoutMilliseconds, cancellationToken, async: false));
        temp.AsSpan(0, n).CopyTo(buffer);
        return n;
    }

    /// <summary>Waits for an interrupt-IN packet. Cancelling the token throws <see cref="OperationCanceledException"/>.</summary>
    internal async ValueTask<int> TakeInterruptAsync(Memory<byte> buffer, int timeoutMilliseconds, CancellationToken cancellationToken, bool async)
    {
        var deadline = Deadline.FromMilliseconds(timeoutMilliseconds).WithCancellation(cancellationToken);
        while (true)
        {
            Task next;
            lock (_sync)
            {
                // A cancelled transfer (a USB/IP URB the host unlinked) must not take a packet meant for the next one.
                cancellationToken.ThrowIfCancellationRequested();
                if (_interrupts.TryDequeue(out var packet))
                {
                    int n = Math.Min(packet.Length, buffer.Length);
                    packet.AsSpan(0, n).CopyTo(buffer.Span);
                    return n;
                }
                next = _changed.Next;
            }
            if (!await ChangeSignal.WaitAsync(next, deadline, async).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new UsbTimeoutException("USB interrupt read timed out.");
            }
        }
    }

    private byte[] StringDescriptor(int index)
    {
        if (index == 0) return [4, 3, 0x09, 0x04];
        string s = index switch { 1 => Info.Manufacturer ?? "", 2 => Info.Product ?? "", 3 => Info.SerialNumber, _ => "" };
        var bytes = Encoding.Unicode.GetBytes(s);
        return [(byte)(bytes.Length + 2), 3, .. bytes];
    }

    internal int HandleBulkOut(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 12) throw new UsbException(UsbError.Stall, "Stall: short USBTMC header.");
        byte msgId = packet[0], tag = packet[1];
        if (packet[2] != (byte)~tag) throw new UsbException(UsbError.Stall, "Stall: bad bTagInverse.");
        lock (_sync)
        {
            if (tag == _lastTag) RepeatedTagCount++;
            _lastTag = tag;
        }
        switch (msgId)
        {
            case 1: // DEV_DEP_MSG_OUT
            {
                int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]);
                bool eom = (packet[8] & 1) != 0;
                lock (_sync)
                {
                    _input.AddRange(packet.Slice(12, size));
                    if (eom)
                    {
                        // Execute in the background, like a real instrument.
                        var message = _input.ToArray();
                        _input.Clear();
                        int generation = _generation;
                        _pending = _pending.ContinueWith(_ =>
                        {
                            var responses = _instrument.Process(message);
                            lock (_sync)
                            {
                                if (_generation != generation) return;
                                foreach (var r in responses) _output.Enqueue([.. r, (byte)'\n']);
                                _changed.Pulse();
                            }
                        }, TaskScheduler.Default);
                    }
                }
                break;
            }
            case 2: // REQUEST_DEV_DEP_MSG_IN
                lock (_sync)
                {
                    _request = (tag, (int)BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]), (packet[8] & 2) != 0, packet[9], false);
                    _transfer = null;
                    _changed.Pulse();
                }
                break;
            case 126: // VENDOR_SPECIFIC_OUT: looped back to REQUEST_VENDOR_SPECIFIC_IN
            {
                int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]);
                lock (_sync)
                {
                    _vendorData.AddRange(packet.Slice(12, size));
                    VendorMessageCount++;
                    _changed.Pulse();
                }
                break;
            }
            case 127: // REQUEST_VENDOR_SPECIFIC_IN
                lock (_sync)
                {
                    _request = (tag, (int)BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]), false, 0, true);
                    _transfer = null;
                    _changed.Pulse();
                }
                break;
            case 128: // USB488 TRIGGER
                _instrument.Trigger();
                break;
            default:
                throw new UsbException(UsbError.Stall, $"Stall: unsupported MsgID {msgId}.");
        }
        return packet.Length;
    }

    internal int HandleBulkIn(Span<byte> buffer, int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        var temp = new byte[buffer.Length];
        int n = AsyncIO.Complete(HandleBulkInAsync(temp, timeoutMilliseconds, cancellationToken, async: false));
        temp.AsSpan(0, n).CopyTo(buffer);
        return n;
    }

    /// <summary>
    /// Answers a bulk-IN transfer, waiting (like a device NAKing) until a response is available. Cancelling the token
    /// throws <see cref="OperationCanceledException"/>.
    /// </summary>
    internal async ValueTask<int> HandleBulkInAsync(Memory<byte> buffer, int timeoutMilliseconds, CancellationToken cancellationToken, bool async)
    {
        if (ResponseDelayMilliseconds > 0)
        {
            if (async) await Task.Delay(ResponseDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            else Thread.Sleep(ResponseDelayMilliseconds);
        }
        var deadline = Deadline.FromMilliseconds(timeoutMilliseconds).WithCancellation(cancellationToken);
        while (true)
        {
            Task next;
            lock (_sync)
            {
                // A cancelled transfer (a USB/IP URB the host unlinked) must not take a response meant for the next one.
                cancellationToken.ThrowIfCancellationRequested();
                if (_transfer is not null || !(_request is null || (_request.Value.Vendor ? _vendorData.Count == 0 : _current is null && _output.Count == 0)))
                    return TakeBulkIn(buffer.Span);
                next = _changed.Next;
            }
            if (!await ChangeSignal.WaitAsync(next, deadline, async).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new UsbTimeoutException("USB bulk-in timed out (device NAK).");
            }
        }
    }

    /// <summary>Copies the next piece of the current bulk-IN transfer, building it first if needed. Called under the lock.</summary>
    private int TakeBulkIn(Span<byte> buffer)
    {
        if (_transfer is null)
        {
            var request = _request!.Value;
            _request = null;
            if (request.Vendor)
            {
                int vendorSize = Math.Min(_vendorData.Count, request.Size);
                _transfer = new byte[(12 + vendorSize + 3) & ~3];
                _transfer[0] = 127;
                _transfer[1] = request.Tag;
                _transfer[2] = (byte)~request.Tag;
                BinaryPrimitives.WriteUInt32LittleEndian(_transfer.AsSpan(4), (uint)vendorSize);
                _vendorData.CopyTo(0, _transfer, 12, vendorSize);
                _vendorData.RemoveRange(0, vendorSize);
                _transferOffset = 0;
            }
            else
            {
                _current ??= _output.Dequeue();
                int available = _current.Length - _currentOffset;
                int size = Math.Min(available, request.Size);
                byte attributes = 0;
                if (request.TermChar && SupportsTermChar)
                {
                    int index = Array.IndexOf(_current, request.TermCharValue, _currentOffset, size);
                    if (index >= 0)
                    {
                        size = index - _currentOffset + 1;
                        attributes |= 2;
                    }
                }
                var data = _current.AsSpan(_currentOffset, size);
                _currentOffset += size;
                if (_currentOffset >= _current.Length)
                {
                    attributes |= 1; // EOM
                    _current = null;
                    _currentOffset = 0;
                }
                int total = (12 + size + 3) & ~3;
                var transfer = new byte[total];
                transfer[0] = 2;
                transfer[1] = request.Tag;
                transfer[2] = (byte)~request.Tag;
                BinaryPrimitives.WriteUInt32LittleEndian(transfer.AsSpan(4), (uint)size);
                transfer[8] = attributes;
                data.CopyTo(transfer.AsSpan(12));
                _transfer = transfer;
                _transferOffset = 0;
            }
        }

        int count = Math.Min(buffer.Length, _transfer.Length - _transferOffset);
        _transfer.AsSpan(_transferOffset, count).CopyTo(buffer);
        _transferOffset += count;
        if (_transferOffset >= _transfer.Length) _transfer = null;
        return count;
    }
}

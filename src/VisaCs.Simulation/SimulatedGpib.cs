using VisaCs.Gpib;
using VisaCs.Internal;

namespace VisaCs.Simulation;

/// <summary>A GPIB driver with simulated instruments on board 0.</summary>
public sealed class SimulatedGpibDriver : IGpibDriver
{
    /// <summary>Instruments by primary address.</summary>
    public Dictionary<int, SimulatedInstrument> Instruments { get; } = new();

    /// <summary>Current state of the simulated REN line.</summary>
    public bool RemoteEnable { get; private set; }

    /// <summary>Command bytes sent with ATN asserted, in order.</summary>
    public List<byte[]> SentCommands { get; } = new();

    /// <inheritdoc/>
    public string Name => "Simulated GPIB";

    /// <inheritdoc/>
    public bool IsAvailable => true;

    /// <inheritdoc/>
    public IReadOnlyList<int> ListBoards() => [0];

    /// <inheritdoc/>
    public IReadOnlyList<(int Primary, int? Secondary)> FindListeners(int board) =>
        board == 0 ? Instruments.Keys.Order().Select(p => (p, (int?)null)).ToList() : [];

    /// <inheritdoc/>
    public IGpibDevice OpenDevice(int board, int primaryAddress, int? secondaryAddress)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(primaryAddress);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(primaryAddress, 30);
        if (board != 0 || !Instruments.TryGetValue(primaryAddress, out var instrument))
            throw new GpibException(GpibError.NoListeners, $"No device at GPIB{board}::{primaryAddress}.");
        return new Device(instrument);
    }

    /// <inheritdoc/>
    public IGpibBoard OpenBoard(int board)
    {
        if (board != 0) throw new GpibException(GpibError.NoBoard, $"GPIB board {board} does not exist.");
        return new Board(this);
    }

    /// <summary>
    /// A simulated device. Reads and SRQ waits wait on a change signal, so the asynchronous methods hold no thread.
    /// Cancelling their token throws <see cref="OperationCanceledException"/>; a timeout throws <see cref="GpibTimeoutException"/>.
    /// After disposal every call throws <see cref="ObjectDisposedException"/>.
    /// </summary>
    private sealed class Device : IGpibDevice
    {
        private readonly SimulatedInstrument _instrument;
        private readonly Lock _sync = new();
        private readonly ChangeSignal _changed = new();
        private readonly List<byte> _input = new();
        private readonly Queue<byte[]> _output = new();
        private byte[]? _current;
        private int _offset;
        private bool _srq;
        private byte _srqStb;
        private bool _disposed;

        public Device(SimulatedInstrument instrument)
        {
            _instrument = instrument;
            _instrument.ServiceRequested += OnSrq;
        }

        private void OnSrq(byte stb)
        {
            lock (_sync)
            {
                _srq = true;
                _srqStb = stb;
                _changed.Pulse();
            }
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);

        public bool ReaddressingEnabled
        {
            get
            {
                ThrowIfDisposed();
                return field;
            }
            set
            {
                ThrowIfDisposed();
                field = value;
            }
        } = true;

        public bool UnaddressingEnabled
        {
            get
            {
                ThrowIfDisposed();
                return field;
            }
            set
            {
                ThrowIfDisposed();
                field = value;
            }
        }

        public void Write(ReadOnlySpan<byte> data, bool end, int timeoutMilliseconds)
        {
            ThrowIfDisposed();
            byte[]? message = null;
            lock (_sync)
            {
                _input.AddRange(data);
                if (end)
                {
                    message = _input.ToArray();
                    _input.Clear();
                }
            }
            if (message is null) return;
            var responses = _instrument.Process(message);
            lock (_sync)
            {
                foreach (var r in responses) _output.Enqueue([.. r, (byte)'\n']);
                _changed.Pulse();
            }
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, bool end, int timeoutMilliseconds, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(data.Span, end, timeoutMilliseconds);
            return default;
        }

        public GpibReadResult Read(Span<byte> buffer, byte? termChar, int timeoutMilliseconds)
        {
            var temp = new byte[buffer.Length];
            var result = AsyncIO.Complete(ReadAsync(temp, termChar, timeoutMilliseconds, CancellationToken.None, async: false));
            temp.AsSpan(0, result.Count).CopyTo(buffer);
            return result;
        }

        public ValueTask<GpibReadResult> ReadAsync(Memory<byte> buffer, byte? termChar, int timeoutMilliseconds,
            CancellationToken cancellationToken = default) =>
            ReadAsync(buffer, termChar, timeoutMilliseconds, cancellationToken, async: true);

        private async ValueTask<GpibReadResult> ReadAsync(Memory<byte> buffer, byte? termChar, int timeoutMilliseconds,
            CancellationToken cancellationToken, bool async)
        {
            ThrowIfDisposed();
            var deadline = Deadline.FromMilliseconds(timeoutMilliseconds).WithCancellation(cancellationToken);
            while (true)
            {
                Task next;
                lock (_sync)
                {
                    if (_current is not null || _output.Count > 0)
                    {
                        _current ??= _output.Dequeue();
                        int count = Math.Min(buffer.Length, _current.Length - _offset);
                        if (termChar is { } c)
                        {
                            int index = Array.IndexOf(_current, c, _offset, count);
                            if (index >= 0) count = index - _offset + 1;
                        }
                        _current.AsSpan(_offset, count).CopyTo(buffer.Span);
                        _offset += count;
                        bool end = _offset >= _current.Length;
                        if (end)
                        {
                            _current = null;
                            _offset = 0;
                        }
                        return new GpibReadResult(count, end);
                    }
                    next = _changed.Next;
                }
                if (!await ChangeSignal.WaitAsync(next, deadline, async).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ThrowIfDisposed();
                    throw new GpibTimeoutException("GPIB read timed out.");
                }
            }
        }

        public byte SerialPoll(int timeoutMilliseconds)
        {
            ThrowIfDisposed();
            lock (_sync)
            {
                byte stb = _instrument.GetStatusByte(_current is not null || _output.Count > 0);
                _srq = false;
                return stb;
            }
        }

        public ValueTask<byte> SerialPollAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(SerialPoll(timeoutMilliseconds));
        }

        public void Clear(int timeoutMilliseconds)
        {
            ThrowIfDisposed();
            lock (_sync)
            {
                _input.Clear();
                _output.Clear();
                _current = null;
                _offset = 0;
            }
            _instrument.DeviceClear();
        }

        public ValueTask ClearAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Clear(timeoutMilliseconds);
            return default;
        }

        public void Trigger(int timeoutMilliseconds)
        {
            ThrowIfDisposed();
            _instrument.Trigger();
        }

        public ValueTask TriggerAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Trigger(timeoutMilliseconds);
            return default;
        }

        public void GoToLocal()
        {
            ThrowIfDisposed();
            _instrument.IsRemote = false;
        }

        public byte? WaitForServiceRequest(int timeoutMilliseconds) =>
            AsyncIO.Complete(WaitForServiceRequestAsync(timeoutMilliseconds, CancellationToken.None, async: false));

        public ValueTask<byte?> WaitForServiceRequestAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
            WaitForServiceRequestAsync(timeoutMilliseconds, cancellationToken, async: true);

        private async ValueTask<byte?> WaitForServiceRequestAsync(int timeoutMilliseconds, CancellationToken cancellationToken, bool async)
        {
            ThrowIfDisposed();
            var deadline = Deadline.FromMilliseconds(timeoutMilliseconds).WithCancellation(cancellationToken);
            while (true)
            {
                Task next;
                lock (_sync)
                {
                    if (_srq)
                    {
                        _srq = false;
                        return _srqStb;
                    }
                    next = _changed.Next;
                }
                bool changed = await ChangeSignal.WaitAsync(next, deadline, async).ConfigureAwait(false);
                ThrowIfDisposed();
                if (!changed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return null;
                }
            }
        }

        public void Dispose()
        {
            if (Volatile.Read(ref _disposed)) return;
            _instrument.ServiceRequested -= OnSrq;
            lock (_sync)
            {
                Volatile.Write(ref _disposed, true);
                _changed.Pulse(); // pending reads and waits end with ObjectDisposedException
            }
        }
    }

    private sealed class Board(SimulatedGpibDriver driver) : IGpibBoard
    {
        public void Write(ReadOnlySpan<byte> data, bool end, int timeoutMilliseconds) { }

        public GpibReadResult Read(Span<byte> buffer, byte? termChar, int timeoutMilliseconds) =>
            throw new GpibTimeoutException("No data on the simulated board.");

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, bool end, int timeoutMilliseconds, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return default;
        }

        public ValueTask<GpibReadResult> ReadAsync(Memory<byte> buffer, byte? termChar, int timeoutMilliseconds,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<GpibReadResult>(new GpibTimeoutException("No data on the simulated board."));

        public void SendCommand(ReadOnlySpan<byte> commandBytes, int timeoutMilliseconds) => driver.SentCommands.Add(commandBytes.ToArray());

        public ValueTask SendCommandAsync(ReadOnlyMemory<byte> commandBytes, int timeoutMilliseconds, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendCommand(commandBytes.Span, timeoutMilliseconds);
            return default;
        }

        public void SendInterfaceClear() { }

        public void SetRemoteEnable(bool asserted) => driver.RemoteEnable = asserted;

        public void ControlAtn(GpibAtnMode mode) { }

        public void PassControl(int primaryAddress, int? secondaryAddress, int timeoutMilliseconds) { }

        public GpibLineStatus GetLines() => new((GpibLines)0xFF, driver.RemoteEnable ? GpibLines.Ren : GpibLines.None);

        public int PrimaryAddress { get; set; }

        public int? SecondaryAddress { get; set; }

        public bool IsSystemController => true;

        public bool IsControllerInCharge => true;

        public GpibAddressState AddressState => GpibAddressState.Unaddressed;

        // The simulated bus never asserts SRQ on its own.
        public bool WaitForServiceRequest(int timeoutMilliseconds)
        {
            Thread.Sleep(timeoutMilliseconds < 0 ? Timeout.Infinite : timeoutMilliseconds);
            return false;
        }

        public async ValueTask<bool> WaitForServiceRequestAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default)
        {
            await Task.Delay(timeoutMilliseconds < 0 ? Timeout.Infinite : timeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            return false;
        }

        public void Dispose() { }
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using VisaCs.Gpib.Native;
using VisaCs.Internal;
using static VisaCs.Gpib.Native.Ni488;

namespace VisaCs.Gpib;

// The device and board objects of Ni488Driver. Synchronous calls run on the caller's thread; the asynchronous
// machinery is in Ni488Driver.Transfers.cs, Ni488Driver.Poller.cs and Ni488Driver.BoardWorker.cs.
public sealed unsafe partial class Ni488Driver
{
    /// <summary>A device or board descriptor.</summary>
    private abstract class IOBase(Ni488Driver driver, Ni488Api api, UdState state, int board) : IGpibIO
    {
        public readonly Ni488Driver Driver = driver;
        public readonly Ni488Api Api = api;
        public readonly UdState State = state;
        public readonly int Ud = state.Ud;

        /// <summary>The board this descriptor talks through; its worker thread runs this object's blocking calls.</summary>
        public readonly int BoardIndex = board;

        private int _disposed;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        protected void ThrowIfDisposed()
        {
            if (IsDisposed) throw Disposed(this);
        }

        protected void SetTimeout(int milliseconds) => Check(State.SetTimeout(Api, TimeoutCode(milliseconds)), "timeout setting");

        protected void Check(int status, string operation) => Ni488Driver.Check(Api, status, operation);

        /// <summary>Reads an ibask option, throwing if the driver refuses.</summary>
        protected int Ask(int option, string what)
        {
            ThrowIfDisposed();
            int value = 0;
            Check(Api.ibask(Ud, option, &value), what);
            return value;
        }

        protected void Configure(int option, int value, string what)
        {
            ThrowIfDisposed();
            Check(Api.ibconfig(Ud, option, value), what);
        }

        /// <summary>Sets the timeout and EOI mode of a write.</summary>
        private void PrepareWrite(bool end, int timeoutMilliseconds)
        {
            SetTimeout(timeoutMilliseconds);
            Check(State.SetEndOfTransfer(Api, end), "EOI setting");
        }

        /// <summary>Sets the timeout and termination character of a read.</summary>
        private void PrepareRead(byte? termChar, int timeoutMilliseconds)
        {
            SetTimeout(timeoutMilliseconds);
            Check(State.SetTermChar(Api, termChar), "termination character setting");
        }

        public void Write(ReadOnlySpan<byte> data, bool end, int timeoutMilliseconds)
        {
            ThrowIfDisposed();
            PrepareWrite(end, timeoutMilliseconds);
            fixed (byte* p = data)
            {
                int status = Api.ibwrt(Ud, p, data.Length);
                if ((status & ERR) != 0) throw Error(Api.ThreadIberr(), "write", Api.ThreadIbcnt());
            }
        }

        public GpibReadResult Read(Span<byte> buffer, byte? termChar, int timeoutMilliseconds)
        {
            ThrowIfDisposed();
            PrepareRead(termChar, timeoutMilliseconds);
            fixed (byte* p = buffer)
            {
                int status = Api.ibrd(Ud, p, buffer.Length);
                int count = Api.ThreadIbcnt();
                if ((status & ERR) == 0) return new GpibReadResult(count, (status & END) != 0);
                int iberr = Api.ThreadIberr();
                // Partial data before a timeout: return it, the next read times out.
                if (iberr == EABO && count > 0) return new GpibReadResult(count, false);
                throw Error(iberr, "read");
            }
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, bool end, int timeoutMilliseconds, CancellationToken cancellationToken = default)
        {
            if (!CanStartAsync(Api, TransferKind.Write))
                return RunBlocking(() => Write(data.Span, end, timeoutMilliseconds), cancellationToken);
            return new(Driver.StartTransfer(this, TransferKind.Write, MemoryMarshal.AsMemory(data),
                () => PrepareWrite(end, timeoutMilliseconds), cancellationToken));
        }

        public ValueTask<GpibReadResult> ReadAsync(Memory<byte> buffer, byte? termChar, int timeoutMilliseconds,
            CancellationToken cancellationToken = default)
        {
            if (!CanStartAsync(Api, TransferKind.Read))
                return RunBlocking(() => Read(buffer.Span, termChar, timeoutMilliseconds), cancellationToken);
            return new(Driver.StartTransfer(this, TransferKind.Read, buffer, () => PrepareRead(termChar, timeoutMilliseconds),
                cancellationToken));
        }

        /// <summary>Runs a blocking call on the board's worker thread.</summary>
        protected ValueTask RunBlocking(Action call, CancellationToken cancellationToken) =>
            new(Driver.WorkerFor(BoardIndex).Run(this, () =>
            {
                call();
                return true;
            }, cancellationToken));

        /// <summary>Runs a blocking call on the board's worker thread.</summary>
        protected ValueTask<T> RunBlocking<T>(Func<T> call, CancellationToken cancellationToken) =>
            new(Driver.WorkerFor(BoardIndex).Run(this, call, cancellationToken));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Driver.Release(this);
        }

        /// <summary>Closes the descriptor; called once no blocking call of this object runs any more.</summary>
        public abstract void Close();
    }

    private sealed class Device(Ni488Driver driver, Ni488Api api, UdState state, int board) : IOBase(driver, api, state, board), IGpibDevice
    {
        /// <summary>
        /// When this device was last serial-polled without a request while SRQ or RQS stayed signalled (a Stopwatch
        /// timestamp), or 0 when that has not happened since the signal last went away.
        /// </summary>
        public long SrqPolledAt;

        public byte SerialPoll(int timeoutMilliseconds)
        {
            ThrowIfDisposed();
            SetTimeout(timeoutMilliseconds);
            byte stb;
            Check(Api.ibrsp(Ud, &stb), "serial poll");
            return stb;
        }

        public void Clear(int timeoutMilliseconds)
        {
            ThrowIfDisposed();
            SetTimeout(timeoutMilliseconds);
            Check(Api.ibclr(Ud), "device clear");
        }

        public void Trigger(int timeoutMilliseconds)
        {
            ThrowIfDisposed();
            SetTimeout(timeoutMilliseconds);
            Check(Api.ibtrg(Ud), "trigger");
        }

        public void GoToLocal()
        {
            ThrowIfDisposed();
            Check(Api.ibloc(Ud), "go to local");
        }

        public bool ReaddressingEnabled
        {
            get => Ask(IbcREADDR, "readdressing query") != 0;
            set => Configure(IbcREADDR, value ? 1 : 0, "readdressing setting");
        }

        public bool UnaddressingEnabled
        {
            get => Ask(IbcUnAddr, "unaddressing query") != 0;
            set => Configure(IbcUnAddr, value ? 1 : 0, "unaddressing setting");
        }

        public byte? WaitForServiceRequest(int timeoutMilliseconds)
        {
            ThrowIfDisposed();
            var deadline = Deadline.FromMilliseconds(timeoutMilliseconds);
            while (true)
            {
                if (ShouldPoll(this, Api.ibwait(BoardIndex, 0), Api.ibwait(Ud, 0)) && PollRequest() is { } stb) return stb;
                if (deadline.HasExpired) return null;
                Thread.Sleep(deadline.Slice(PollSlowMs));
            }
        }

        /// <summary>
        /// Serial-polls the device after a service request was signalled: its status byte if it requested service,
        /// otherwise null (then it is not polled again for <see cref="SrqRepollMs"/> while the signal stays).
        /// </summary>
        public byte? PollRequest()
        {
            byte stb;
            int status = Api.ibrsp(Ud, &stb);
            if ((status & ERR) == 0 && (stb & 0x40) != 0)
            {
                Volatile.Write(ref SrqPolledAt, 0);
                return stb;
            }
            Volatile.Write(ref SrqPolledAt, Stopwatch.GetTimestamp());
            return null;
        }

        public ValueTask<byte> SerialPollAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
            RunBlocking(() => SerialPoll(timeoutMilliseconds), cancellationToken);

        public ValueTask ClearAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
            RunBlocking(() => Clear(timeoutMilliseconds), cancellationToken);

        public ValueTask TriggerAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
            RunBlocking(() => Trigger(timeoutMilliseconds), cancellationToken);

        public ValueTask<byte?> WaitForServiceRequestAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
            Driver.WaitForDeviceRequestAsync(this, timeoutMilliseconds, cancellationToken);

        public override void Close() => Api.ibonl(Ud, 0);
    }

    private sealed class Board(Ni488Driver driver, Ni488Api api, UdState state) : IOBase(driver, api, state, state.Ud), IGpibBoard
    {
        public void SendCommand(ReadOnlySpan<byte> commandBytes, int timeoutMilliseconds)
        {
            ThrowIfDisposed();
            SetTimeout(timeoutMilliseconds);
            fixed (byte* p = commandBytes)
            {
                int status = Api.ibcmd(Ud, p, commandBytes.Length);
                if ((status & ERR) != 0) throw Error(Api.ThreadIberr(), "send command", Api.ThreadIbcnt());
            }
        }

        public ValueTask SendCommandAsync(ReadOnlyMemory<byte> commandBytes, int timeoutMilliseconds, CancellationToken cancellationToken = default)
        {
            if (!CanStartAsync(Api, TransferKind.Command))
                return RunBlocking(() => SendCommand(commandBytes.Span, timeoutMilliseconds), cancellationToken);
            return new(Driver.StartTransfer(this, TransferKind.Command, MemoryMarshal.AsMemory(commandBytes),
                () => SetTimeout(timeoutMilliseconds), cancellationToken));
        }

        public void SendInterfaceClear()
        {
            ThrowIfDisposed();
            Check(Api.ibsic(Ud), "interface clear");
        }

        public void SetRemoteEnable(bool asserted)
        {
            ThrowIfDisposed();
            Check(Api.ibsre(Ud, asserted ? 1 : 0), "REN control");
        }

        public void ControlAtn(GpibAtnMode mode)
        {
            ThrowIfDisposed();
            int status = mode switch
            {
                GpibAtnMode.Assert => Api.ibcac(Ud, 1),
                GpibAtnMode.AssertImmediate => Api.ibcac(Ud, 0),
                GpibAtnMode.DeassertHandshake => Api.ibgts(Ud, 1),
                GpibAtnMode.Deassert => Api.ibgts(Ud, 0),
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            };
            Check(status, "ATN control");
        }

        public void PassControl(int primaryAddress, int? secondaryAddress, int timeoutMilliseconds)
        {
            CheckAddress(primaryAddress, secondaryAddress);
            ThrowIfDisposed();
            int ud = Api.ibdev(Ud, primaryAddress, SadArgument(secondaryAddress), TimeoutCode(timeoutMilliseconds), 1, 0);
            if (ud < 0) throw Error(Api.ThreadIberr(), "pass control");
            try
            {
                Check(Api.ibpct(ud), "pass control");
            }
            finally
            {
                Api.ibonl(ud, 0);
            }
        }

        public GpibLineStatus GetLines()
        {
            ThrowIfDisposed();
            short lines;
            Check(Api.iblines(Ud, &lines), "line status");
            return new GpibLineStatus((GpibLines)(lines & 0xFF), (GpibLines)((lines >> 8) & 0xFF));
        }

        public int PrimaryAddress
        {
            get => Ask(IbcPAD, "primary address query");
            set
            {
                CheckAddress(value, null);
                Configure(IbcPAD, value, "primary address setting");
            }
        }

        public int? SecondaryAddress
        {
            get => Ask(IbcSAD, "secondary address query") is var v && v >= SadBase ? v - SadBase : null;
            set
            {
                CheckAddress(0, value);
                Configure(IbcSAD, SadArgument(value), "secondary address setting");
            }
        }

        public bool IsSystemController => Ask(IbcSC, "system controller query") != 0;

        private int CurrentStatus()
        {
            ThrowIfDisposed();
            int status = Api.ibwait(Ud, 0);
            Check(status, "status query");
            return status;
        }

        public bool IsControllerInCharge => (CurrentStatus() & CIC) != 0;

        public GpibAddressState AddressState
        {
            get
            {
                int s = CurrentStatus();
                return (s & TACS) != 0 ? GpibAddressState.Talker : (s & LACS) != 0 ? GpibAddressState.Listener : GpibAddressState.Unaddressed;
            }
        }

        public bool WaitForServiceRequest(int timeoutMilliseconds)
        {
            ThrowIfDisposed();
            if (timeoutMilliseconds == 0) return (Api.ibwait(Ud, 0) & (ERR | SRQI)) == SRQI;
            SetTimeout(timeoutMilliseconds);
            return (Api.ibwait(Ud, SRQI | TIMO) & SRQI) != 0;
        }

        public ValueTask<bool> WaitForServiceRequestAsync(int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
            new(Driver.WaitForBoardRequestAsync(this, timeoutMilliseconds, cancellationToken));

        public override void Close()
        {
            // Board descriptors are shared; leave the board online.
        }
    }
}

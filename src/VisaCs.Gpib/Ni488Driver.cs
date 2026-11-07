using System.Collections.Concurrent;
using VisaCs.Gpib.Native;
using static VisaCs.Gpib.Native.Ni488;

namespace VisaCs.Gpib;

/// <summary>
/// NI-488.2 compatible driver: NI-488.2, Keysight 488 (gpib-32.dll), linux-gpib, and other boards whose vendors ship
/// the same C API. The library is loaded at run time, on first use; pass a path to use a specific one.
/// </summary>
/// <remarks>
/// <para>
/// Use one driver object for the whole process (a <c>ResourceManager</c> exposes its own as <c>GpibDriver</c>). The
/// object is thread safe; it starts helper threads on demand, which end after a few idle seconds, and needs no disposal.
/// <see cref="ListBoards"/> checks boards 0 to 3. New device descriptors have a 10 s timeout and assert EOI with the
/// last byte of a write; the other settings (readdressing, unaddressing, automatic serial polling) are the driver's.
/// </para>
/// <para>
/// Asynchronous transfers (<see cref="IGpibIO.ReadAsync"/>, <see cref="IGpibIO.WriteAsync"/>,
/// <see cref="IGpibBoard.SendCommandAsync"/>) use the library's asynchronous calls (ibrda, ibwrta, ibcmda) and hold
/// no thread while they run. Where the library has ibnotify (NI-488.2, Keysight), its callback reports completion.
/// Otherwise (linux-gpib) one background thread per driver polls the pending transfers with ibwait: every
/// millisecond during a transfer's first 50 ms, every 5 ms after that, which delays completion by up to that much
/// (on Windows short waits last a timer tick, up to 15.6 ms). Cancelling stops the transfer with ibstop.
/// </para>
/// <para>
/// Serial poll, device clear and trigger have no asynchronous form in the API. Their asynchronous methods queue the
/// call to one thread per board, which runs that board's blocking calls in order (the bus carries one transfer at a
/// time anyway) and ends when idle. Cancelling removes a queued call; a call that has started ends when the driver
/// returns, at the latest after its timeout. Transfers also run on that thread when the library has no
/// asynchronous calls.
/// </para>
/// <para>
/// Service request waits check the bus every 5 ms (asynchronous waits on the polling thread, synchronous ones on the
/// caller's thread): board SRQI for a board, and for a device also RQS, which the driver's automatic serial polling
/// sets. NI-488.2 reports SRQI only while automatic serial polling is off.
/// </para>
/// </remarks>
public sealed unsafe partial class Ni488Driver : IGpibDriver
{
    /// <summary>Board descriptors by (library handle, board): shared by all drivers that use the same library.</summary>
    private static readonly ConcurrentDictionary<(nint Library, int Board), UdState> BoardStates = new();

    private readonly string? _libraryPath;
    private readonly Lock _sync = new();
    private Ni488Api? _api;
    private string? _loadError;
    private bool _loadAttempted;

    /// <summary>Uses the platform's standard library names (see the GPIB documentation).</summary>
    public Ni488Driver() : this(null)
    {
    }

    /// <summary>Uses the given library (file name or full path), or the platform's standard names when null.</summary>
    public Ni488Driver(string? libraryPath) => _libraryPath = libraryPath;

    /// <inheritdoc/>
    public string Name => "NI-488.2";

    /// <summary>The name or path the library was loaded by, or null if none was loaded. Loads it on first access.</summary>
    public string? LoadedLibrary => Api?.LibraryPath;

    /// <summary>Why no library was loaded (each library tried and the reason), or null if one was. Loads it on first access.</summary>
    public string? LoadError
    {
        get
        {
            _ = Api;
            lock (_sync) return _loadError;
        }
    }

    /// <summary>True if a compatible library was loaded. Loads it on first access; the result, also a failure, stays for this object.</summary>
    public bool IsAvailable => Api is not null;

    private Ni488Api? Api
    {
        get
        {
            lock (_sync)
            {
                if (_loadAttempted) return _api;
                _loadAttempted = true;
                _api = Ni488Api.TryLoad(_libraryPath is not null ? [_libraryPath] : Ni488Api.DefaultCandidates(), out _loadError);
                return _api;
            }
        }
    }

    private Ni488Api RequireApi() => Api
        ?? throw new VisaException(VisaStatus.ErrorLibraryNotFound, $"No NI-488.2 compatible GPIB library was loaded ({LoadError}).");

    internal static void Check(Ni488Api api, int status, string operation)
    {
        if ((status & ERR) != 0) throw Error(api.ThreadIberr(), operation);
    }

    /// <summary>
    /// The exception for an operation that failed with <paramref name="iberr"/>. Read iberr on the thread that made
    /// the call: the library keeps it per thread.
    /// </summary>
    internal static VisaException Error(int iberr, string operation, int count = 0)
    {
        if (iberr == EABO) return new VisaTimeoutException($"GPIB {operation} timed out.") { BytesTransferred = count, NativeError = iberr };
        return new VisaException(Status(iberr), $"GPIB {operation} failed: {Describe(iberr)}.") { NativeError = iberr };
    }

    internal static void CheckAddress(int primaryAddress, int? secondaryAddress)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(primaryAddress);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(primaryAddress, 30);
        if (secondaryAddress is { } sad)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sad, nameof(secondaryAddress));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(sad, 30, nameof(secondaryAddress));
        }
    }

    internal static int SadArgument(int? secondaryAddress) => secondaryAddress is { } s ? SadBase + s : NoSad;

    /// <inheritdoc/>
    /// <remarks>Checks boards 0 to 3; returns an empty list when no library is loaded.</remarks>
    public IReadOnlyList<int> ListBoards()
    {
        var boards = new List<int>();
        var api = Api;
        if (api is null) return boards;
        for (int board = 0; board < 4; board++)
        {
            int value;
            int status = api.ibask(board, IbcPAD, &value);
            if ((status & ERR) == 0) boards.Add(board);
        }
        return boards;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Asks every primary address except the board's own with ibln (about 30 ms each, so about 1 s per board).
    /// Secondary addresses are not scanned. Throws <see cref="VisaStatus.ErrorLibraryNotFound"/> when no library is loaded.
    /// </remarks>
    public IReadOnlyList<(int Primary, int? Secondary)> FindListeners(int board)
    {
        var api = RequireApi();
        var result = new List<(int, int?)>();
        int own = 0;
        api.ibask(board, IbcPAD, &own);
        var state = BoardState(api, board);
        Check(api, state.SetTimeout(api, TimeoutCode(30)), "listener search");
        for (int pad = 0; pad <= 30; pad++)
        {
            if (pad == own) continue;
            short listen = 0;
            int status = api.ibln(board, pad, NoSad, &listen);
            if ((status & ERR) == 0 && listen != 0) result.Add((pad, null));
        }
        return result;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Does not talk to the bus: success does not mean that a device answers at the address (the first write then
    /// fails with <see cref="VisaStatus.ErrorNoListeners"/>). Disposing the device takes its descriptor offline (ibonl).
    /// </remarks>
    public IGpibDevice OpenDevice(int board, int primaryAddress, int? secondaryAddress)
    {
        CheckAddress(primaryAddress, secondaryAddress);
        var api = RequireApi();
        int ud = api.ibdev(board, primaryAddress, SadArgument(secondaryAddress), TimeoutCode(10000), 1, 0);
        if (ud < 0)
        {
            int iberr = api.ThreadIberr();
            throw new VisaException(VisaStatus.ErrorResourceNotFound,
                $"Cannot open GPIB{board}::{primaryAddress}: {Describe(iberr)}.") { NativeError = iberr };
        }
        return new Device(this, api, new UdState(ud), board);
    }

    /// <inheritdoc/>
    public IGpibBoard OpenBoard(int board)
    {
        var api = RequireApi();
        int value;
        Check(api, api.ibask(board, IbcPAD, &value), "board open");
        return new Board(this, api, BoardState(api, board));
    }

    /// <summary>State of a board descriptor, shared by every object (of any driver) that uses the board through the same library.</summary>
    private static UdState BoardState(Ni488Api api, int board) =>
        BoardStates.GetOrAdd((api.Handle, board), static key => new UdState(key.Board));
}

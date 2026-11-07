using System.Runtime.InteropServices;

namespace VisaCs.Gpib.Native;

/// <summary>
/// Function table of an NI-488.2 compatible library, resolved at run time. Every call uses the library's calling
/// convention (NI488CC: __stdcall on 32-bit Windows, the platform default elsewhere), which is what
/// <c>delegate* unmanaged</c> without a convention means.
/// </summary>
internal sealed unsafe class Ni488Api
{
    public delegate* unmanaged<int, int, int, int, int, int, int> ibdev;
    public delegate* unmanaged<int, int, int> ibonl;
    public delegate* unmanaged<int, byte*, nint, int> ibwrt;
    public delegate* unmanaged<int, byte*, nint, int> ibrd;
    public delegate* unmanaged<int, byte*, nint, int> ibcmd;
    public delegate* unmanaged<int, int> ibclr;
    public delegate* unmanaged<int, int> ibtrg;
    public delegate* unmanaged<int, byte*, int> ibrsp;
    public delegate* unmanaged<int, int> ibloc;
    public delegate* unmanaged<int, int> ibsic;
    public delegate* unmanaged<int, int, int> ibsre;
    public delegate* unmanaged<int, int, int> ibtmo;
    public delegate* unmanaged<int, int, int, int> ibconfig;
    public delegate* unmanaged<int, int, int*, int> ibask;
    public delegate* unmanaged<int, int, int, short*, int> ibln;
    public delegate* unmanaged<int, int, int> ibwait;
    public delegate* unmanaged<int, int, int> ibgts;
    public delegate* unmanaged<int, int, int> ibcac;
    public delegate* unmanaged<int, int> ibpct;
    public delegate* unmanaged<int, short*, int> iblines;
    public delegate* unmanaged<int> ThreadIberr;
    public delegate* unmanaged<int> ThreadIbcnt;

    // Optional exports (null when the library lacks them). Asynchronous transfers: NI-488.2, Keysight, linux-gpib.
    public delegate* unmanaged<int, byte*, nint, int> ibrda;
    public delegate* unmanaged<int, byte*, nint, int> ibwrta;
    public delegate* unmanaged<int, byte*, nint, int> ibcmda;
    public delegate* unmanaged<int, int> ibstop;

    // Event callbacks: NI-488.2 and Keysight, not linux-gpib.
    public delegate* unmanaged<int, int, delegate* unmanaged<int, int, int, int, nint, int>, nint, int> ibnotify;

    // Status of the last asynchronous transfer resynchronized on the calling thread: a linux-gpib extension.
    public delegate* unmanaged<int> AsyncIbsta;
    public delegate* unmanaged<int> AsyncIberr;
    public delegate* unmanaged<int> AsyncIbcnt;

    /// <summary>The name or path the library was loaded by.</summary>
    public required string LibraryPath { get; init; }

    /// <summary>The library's module handle; the same for every load of one library.</summary>
    public required nint Handle { get; init; }

    /// <summary>True if <see cref="AsyncIbsta"/>, <see cref="AsyncIberr"/> and <see cref="AsyncIbcnt"/> are available.</summary>
    public bool HasAsyncStatus => AsyncIbsta != null && AsyncIberr != null && AsyncIbcnt != null;

    /// <summary>The libraries tried when no path is given, in order.</summary>
    public static string[] DefaultCandidates() =>
        OperatingSystem.IsWindows()
            ? (Environment.Is64BitProcess ? ["ni4882.dll", "gpib-32.dll"] : ["gpib-32.dll", "ni4882.dll"])
            : OperatingSystem.IsMacOS()
                ? ["/Library/Frameworks/NI4882.framework/NI4882", "libgpib.dylib"]
                : ["libgpib.so.0", "libgpib.so"];

    /// <summary>
    /// Loads the first candidate library that exports the whole required API. Returns null when none does, with the
    /// reason for each candidate in <paramref name="error"/>.
    /// </summary>
    public static Ni488Api? TryLoad(IEnumerable<string> candidates, out string? error)
    {
        var reasons = new List<string>();
        foreach (var candidate in candidates)
        {
            if (!NativeLibrary.TryLoad(candidate, typeof(Ni488Api).Assembly, null, out var handle) &&
                !NativeLibrary.TryLoad(candidate, out handle))
            {
                reasons.Add($"{candidate}: not found or not loadable");
                continue;
            }
            var missing = new List<string>();
            nint Optional(string name) => NativeLibrary.TryGetExport(handle, name, out var p) ? p : 0;
            nint Require(string name)
            {
                nint p = Optional(name);
                if (p == 0) missing.Add(name);
                return p;
            }

            var api = new Ni488Api
            {
                LibraryPath = candidate,
                Handle = handle,
                ibdev = (delegate* unmanaged<int, int, int, int, int, int, int>)Require("ibdev"),
                ibonl = (delegate* unmanaged<int, int, int>)Require("ibonl"),
                ibwrt = (delegate* unmanaged<int, byte*, nint, int>)Require("ibwrt"),
                ibrd = (delegate* unmanaged<int, byte*, nint, int>)Require("ibrd"),
                ibcmd = (delegate* unmanaged<int, byte*, nint, int>)Require("ibcmd"),
                ibclr = (delegate* unmanaged<int, int>)Require("ibclr"),
                ibtrg = (delegate* unmanaged<int, int>)Require("ibtrg"),
                ibrsp = (delegate* unmanaged<int, byte*, int>)Require("ibrsp"),
                ibloc = (delegate* unmanaged<int, int>)Require("ibloc"),
                ibsic = (delegate* unmanaged<int, int>)Require("ibsic"),
                ibsre = (delegate* unmanaged<int, int, int>)Require("ibsre"),
                ibtmo = (delegate* unmanaged<int, int, int>)Require("ibtmo"),
                ibconfig = (delegate* unmanaged<int, int, int, int>)Require("ibconfig"),
                ibask = (delegate* unmanaged<int, int, int*, int>)Require("ibask"),
                ibln = (delegate* unmanaged<int, int, int, short*, int>)Require("ibln"),
                ibwait = (delegate* unmanaged<int, int, int>)Require("ibwait"),
                ibgts = (delegate* unmanaged<int, int, int>)Require("ibgts"),
                ibcac = (delegate* unmanaged<int, int, int>)Require("ibcac"),
                ibpct = (delegate* unmanaged<int, int>)Require("ibpct"),
                iblines = (delegate* unmanaged<int, short*, int>)Require("iblines"),
                ThreadIberr = (delegate* unmanaged<int>)Require("ThreadIberr"),
                ThreadIbcnt = (delegate* unmanaged<int>)Require("ThreadIbcnt"),
                ibrda = (delegate* unmanaged<int, byte*, nint, int>)Optional("ibrda"),
                ibwrta = (delegate* unmanaged<int, byte*, nint, int>)Optional("ibwrta"),
                ibcmda = (delegate* unmanaged<int, byte*, nint, int>)Optional("ibcmda"),
                ibstop = (delegate* unmanaged<int, int>)Optional("ibstop"),
                ibnotify = (delegate* unmanaged<int, int, delegate* unmanaged<int, int, int, int, nint, int>, nint, int>)Optional("ibnotify"),
                AsyncIbsta = (delegate* unmanaged<int>)Optional("AsyncIbsta"),
                AsyncIberr = (delegate* unmanaged<int>)Optional("AsyncIberr"),
                AsyncIbcnt = (delegate* unmanaged<int>)Optional("AsyncIbcnt"),
            };
            if (missing.Count == 0)
            {
                error = null;
                return api;
            }
            NativeLibrary.Free(handle);
            reasons.Add($"{candidate}: does not export {string.Join(", ", missing)}");
        }
        error = reasons.Count == 0 ? "no library to try" : string.Join("; ", reasons);
        return null;
    }
}

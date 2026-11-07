using System.Reflection;
using System.Runtime.InteropServices;

namespace VisaCs.Internal;

/// <summary>
/// Resolves the logical native library names (libc, libusb) used by this assembly's P/Invoke declarations to
/// the right file for the current OS. Resolvers are registered per assembly, so this file is compiled into each
/// library that declares such imports. Call <see cref="EnsureRegistered"/> before the first such P/Invoke.
/// </summary>
internal static class NativeLibraries
{
    public const string LibC = "visacs-libc";
    public const string LibUsb = "visacs-libusb";

    private static readonly Dictionary<string, bool> Availability = new();

    // The static constructor runs once, and other threads calling EnsureRegistered wait until it has finished, so no
    // P/Invoke can run before the resolver is in place.
    static NativeLibraries()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // This assembly already has a resolver; fall back to default probing.
        }
    }

    /// <summary>Registers the resolver for this assembly (once; later calls return at once).</summary>
    public static void EnsureRegistered()
    {
        // Touching the class runs the static constructor.
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        string[] candidates = name switch
        {
            LibC => OperatingSystem.IsMacOS()
                ? ["libSystem.dylib", "/usr/lib/libSystem.dylib"]
                : ["libc.so.6", "libc.so", "libc"],
            LibUsb => OperatingSystem.IsWindows()
                ? ["libusb-1.0.dll"]
                : OperatingSystem.IsMacOS()
                    ? ["libusb-1.0.0.dylib", "/opt/homebrew/lib/libusb-1.0.0.dylib", "/usr/local/lib/libusb-1.0.0.dylib", "libusb-1.0.dylib"]
                    : ["libusb-1.0.so.0", "libusb-1.0.so"],
            _ => [],
        };

        foreach (var candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle)) return handle;
            if (NativeLibrary.TryLoad(candidate, out handle)) return handle;
        }
        return IntPtr.Zero;
    }

    /// <summary>True if the library can be loaded. The answer is cached for the life of the process.</summary>
    public static bool IsAvailable(string name)
    {
        EnsureRegistered();
        lock (Availability)
        {
            if (Availability.TryGetValue(name, out bool available)) return available;
            available = Resolve(name, typeof(NativeLibraries).Assembly, null) != IntPtr.Zero;
            Availability[name] = available;
            return available;
        }
    }
}

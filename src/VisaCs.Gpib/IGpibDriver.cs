namespace VisaCs.Gpib;

/// <summary>
/// A GPIB controller driver: finds boards and listeners and opens device and board descriptors. The built-in
/// <see cref="Ni488Driver"/> binds to the NI-488.2 C API that GPIB board vendors ship (ni4882.dll or gpib-32.dll on
/// Windows, linux-gpib's libgpib.so on Linux, the NI-488.2 framework on macOS).
/// </summary>
public interface IGpibDriver
{
    /// <summary>Display name of the driver, for diagnostics.</summary>
    string Name { get; }

    /// <summary>True if the driver can be used (for <see cref="Ni488Driver"/>: a compatible library was loaded).</summary>
    bool IsAvailable { get; }

    /// <summary>The indices of the boards that are present; empty when the driver is not available.</summary>
    IReadOnlyList<int> ListBoards();

    /// <summary>
    /// The addresses on <paramref name="board"/> where a device answers as a listener. The board must be controller in
    /// charge; otherwise the list is empty.
    /// </summary>
    IReadOnlyList<(int Primary, int? Secondary)> FindListeners(int board);

    /// <summary>
    /// Opens a device descriptor for <paramref name="primaryAddress"/> (0 to 30) and the optional
    /// <paramref name="secondaryAddress"/> (0 to 30) on <paramref name="board"/>. Disposing the device closes it.
    /// </summary>
    IGpibDevice OpenDevice(int board, int primaryAddress, int? secondaryAddress);

    /// <summary>Opens the board (interface) itself. Disposing the object does not take the board offline.</summary>
    IGpibBoard OpenBoard(int board);
}

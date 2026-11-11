using System.Reflection;
using VisaCs.Formatting;
using VisaCs.Gpib;
using VisaCs.Serial;
using VisaCs.Usb;

namespace VisaCs.Tests;

/// <summary>Checks the split into libraries: the component libraries must not depend on VisaCs or on each other.</summary>
public sealed class PackageLayoutTests
{
    private static readonly Assembly[] Components =
    [
        typeof(VisaException).Assembly,
        typeof(NativeSerialPort).Assembly,
        typeof(IUsbBackend).Assembly,
        typeof(IGpibDriver).Assembly,
        typeof(VisaFormatter).Assembly,
    ];

    [Fact]
    public void ComponentLibrariesStandAlone()
    {
        Assert.Equal(5, Components.Distinct().Count());
        foreach (var assembly in Components)
        {
            // The framework and VisaCs.Primitives only: never VisaCs itself or another component library.
            var own = assembly.GetReferencedAssemblies().Select(r => r.Name!).Where(n => n.StartsWith("VisaCs", StringComparison.Ordinal));
            Assert.All(own, name => Assert.Equal("VisaCs.Primitives", name));
        }
    }
}

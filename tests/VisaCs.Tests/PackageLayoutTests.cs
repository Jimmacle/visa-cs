using System.Reflection;
using VisaCs.Formatting;
using VisaCs.Gpib;
using VisaCs.Lan;
using VisaCs.Pci;
using VisaCs.Serial;
using VisaCs.Usb;

namespace VisaCs.Tests;

/// <summary>
/// Checks the split into libraries (CONTRIBUTING.md, "Component libraries"): a component library references no other
/// VisaCs assembly, and all of its public types are in its own namespace.
/// </summary>
public sealed class PackageLayoutTests
{
    private static readonly string[] ComponentTypes =
    [
        nameof(NativeSerialPort),
        nameof(IUsbBackend),
        nameof(IGpibDriver),
        nameof(HislipClient),
        nameof(PciRegion),
        nameof(VisaFormatter),
    ];

    public static TheoryData<string> Components => new(ComponentTypes);

    private static Assembly AssemblyOf(string typeName) => typeName switch
    {
        nameof(NativeSerialPort) => typeof(NativeSerialPort).Assembly,
        nameof(IUsbBackend) => typeof(IUsbBackend).Assembly,
        nameof(IGpibDriver) => typeof(IGpibDriver).Assembly,
        nameof(HislipClient) => typeof(HislipClient).Assembly,
        nameof(PciRegion) => typeof(PciRegion).Assembly,
        nameof(VisaFormatter) => typeof(VisaFormatter).Assembly,
        _ => throw new ArgumentOutOfRangeException(nameof(typeName)),
    };

    [Fact]
    public void ComponentsAreSeparateAssemblies()
    {
        var assemblies = ComponentTypes.Select(AssemblyOf).Append(typeof(VisaException).Assembly).ToArray();
        Assert.Equal(assemblies.Length, assemblies.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Components))]
    public void ComponentReferencesNoVisaCsAssembly(string typeName)
    {
        var assembly = AssemblyOf(typeName);
        var own = assembly.GetReferencedAssemblies()
            .Select(r => r.Name!)
            .Where(n => n.StartsWith("VisaCs", StringComparison.Ordinal));
        Assert.Empty(own);
    }

    [Theory]
    [MemberData(nameof(Components))]
    public void ComponentPublicTypesAreInItsNamespace(string typeName)
    {
        var assembly = AssemblyOf(typeName);
        string root = assembly.GetName().Name!;
        var outside = assembly.GetExportedTypes()
            .Where(t => t.Namespace != root && t.Namespace?.StartsWith(root + ".", StringComparison.Ordinal) != true)
            .Select(t => t.FullName);
        Assert.Empty(outside);
    }
}

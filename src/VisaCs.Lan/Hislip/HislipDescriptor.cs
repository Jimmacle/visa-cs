using System.Text;

namespace VisaCs;

/// <summary>Types of HiSLIP 2.0 descriptors (GetDescriptors).</summary>
public enum HislipDescriptorType : byte
{
    /// <summary>The TLS versions the server supports, as 2-byte protocol codes such as 0x0303 (TLS 1.2) and 0x0304 (TLS 1.3).</summary>
    SupportedTlsVersions = 0,
    /// <summary>Free-form ASCII text about the server's TLS configuration.</summary>
    TlsInformation = 1,
    /// <summary>ASCII text describing the last TLS error on the server.</summary>
    TlsLastError = 2,
}

/// <summary>A HiSLIP 2.0 descriptor, as returned by <see cref="VisaCs.Lan.HislipClient.GetDescriptors"/>.</summary>
/// <remarks>Equality compares <see cref="Content"/> by reference, not by its bytes.</remarks>
/// <param name="Type">The descriptor type. Servers may also send types this enumeration does not name.</param>
/// <param name="Content">The raw descriptor content.</param>
public readonly record struct HislipDescriptor(HislipDescriptorType Type, byte[] Content)
{
    /// <summary>The content as ASCII text (for <see cref="HislipDescriptorType.TlsInformation"/> and <see cref="HislipDescriptorType.TlsLastError"/>).</summary>
    public string Text => Encoding.ASCII.GetString(Content);
}

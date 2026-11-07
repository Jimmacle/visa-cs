using VisaCs.Internal;

namespace VisaCs;

// Generic attribute access (viGetAttribute/viSetAttribute).
public abstract partial class VisaSession
{
    /// <summary>VISA vendor ID reported in VI_ATTR_RSRC_MANF_ID: 0x3FFF, the ID reserved for unregistered vendors.</summary>
    private const int ManufacturerIdUnregistered = 0x3FFF;

    private static readonly Version SpecificationVersion = new(7, 2, 0);

    private AttributeTable Attributes => field ??= CreateAttributeTable();

    private AttributeTable CreateAttributeTable()
    {
        var table = new AttributeTable();
        RegisterAttributes(table);
        return table;
    }

    /// <summary>
    /// The attributes this session supports through <see cref="GetAttribute(VisaAttribute)"/> and
    /// <see cref="SetAttribute"/>. docs/attributes.md lists them for every session type.
    /// </summary>
    public IReadOnlyCollection<VisaAttribute> SupportedAttributes => Attributes.Keys;

    /// <summary>True if the session supports the attribute.</summary>
    public bool IsAttributeSupported(VisaAttribute attribute) => Attributes.Contains(attribute);

    /// <summary>True if the session supports the attribute and it can be set.</summary>
    public bool IsAttributeWritable(VisaAttribute attribute) => Attributes.IsWritable(attribute);

    /// <summary>
    /// Gets an attribute value (viGetAttribute). The value has the type documented on the <see cref="VisaAttribute"/>
    /// member: enumerations as their .NET enum, flags as <see cref="bool"/>, strings as <see cref="string"/>, numbers as
    /// <see cref="int"/> or <see cref="long"/>. <see cref="VisaAttribute.UserData"/> may be null. Most attributes also
    /// have a typed property on the session class. Unsupported attributes throw VI_ERROR_NSUP_ATTR. Reading never
    /// waits for a running operation; a few attributes query the hardware (serial and GPIB line states).
    /// </summary>
    public object? GetAttribute(VisaAttribute attribute)
    {
        ThrowIfDisposed();
        return Attributes.Get(attribute);
    }

    /// <summary>
    /// Gets an attribute value converted to <typeparamref name="T"/>: any numeric type for numbers and enums, an enum
    /// for numbers or names, <see cref="object"/> for the value as is. Throws <see cref="InvalidCastException"/> or
    /// <see cref="FormatException"/> if the value cannot be converted.
    /// </summary>
    public T GetAttribute<T>(VisaAttribute attribute) => ValueConverter.Convert<T>(GetAttribute(attribute));

    /// <summary>
    /// Sets an attribute value (viSetAttribute). <paramref name="value"/> is converted to the attribute's type, so
    /// an <see cref="int"/> works for a <see cref="long"/> attribute and a number or name for an enum. Unsupported
    /// attributes throw VI_ERROR_NSUP_ATTR, read-only ones VI_ERROR_ATTR_READONLY, and values that cannot be converted
    /// or are out of range VI_ERROR_NSUP_ATTR_STATE. Setting a few attributes talks to the device and waits for a
    /// running operation (HiSLIP encryption and overlap mode, serial port settings).
    /// </summary>
    public void SetAttribute(VisaAttribute attribute, object? value)
    {
        ThrowIfDisposed();
        Attributes.Set(attribute, value);
    }

    /// <summary>Registers this session's attributes. Overrides call the base implementation first.</summary>
    private protected virtual void RegisterAttributes(AttributeTable table)
    {
        table.Add(VisaAttribute.ResourceName, () => ResourceName);
        table.Add(VisaAttribute.ResourceClass, () => ResourceClass.ToVisaString());
        table.Add(VisaAttribute.InterfaceType, () => HardwareInterfaceType);
        table.Add(VisaAttribute.InterfaceNumber, () => HardwareInterfaceNumber);
        table.Add(VisaAttribute.ResourceManufacturerName, () => "VisaCs");
        table.Add(VisaAttribute.ResourceManufacturerId, () => ManufacturerIdUnregistered);
        table.Add(VisaAttribute.ResourceSpecVersion, () => EncodeVersion(SpecificationVersion));
        table.Add(VisaAttribute.ResourceImplementationVersion, () => EncodeVersion(ResourceManager.ImplementationVersion));
        table.Add(VisaAttribute.ResourceLockState, () => ResourceLockState);
        table.Add(VisaAttribute.InterfaceInstanceName, () => Resource.InterfaceName);
        table.Add<long>(VisaAttribute.TimeoutValue,
            () => TimeoutMilliseconds < 0 ? 0xFFFFFFFFL : TimeoutMilliseconds,
            v => TimeoutMilliseconds = v is < 0 or >= 0xFFFFFFFFL ? -1 : (int)Math.Min(v, int.MaxValue));
        table.Add(VisaAttribute.MaxQueueLength, () => MaximumQueueLength, v => MaximumQueueLength = v);
        table.Add<object?>(VisaAttribute.UserData, () => UserData, v => UserData = v);
        table.Add<object?>(VisaAttribute.UserData32, () => UserData, v => UserData = v);
    }

    /// <summary>The VISA version encoding: major in bits 20-31, minor in bits 8-19, sub-minor in bits 0-7.</summary>
    private static int EncodeVersion(Version v) =>
        (Math.Max(0, v.Major) & 0xFFF) << 20 | (Math.Max(0, v.Minor) & 0xFFF) << 8 | (Math.Max(0, v.Build) & 0xFF);

    /// <summary>Attribute registry used by <see cref="GetAttribute(VisaAttribute)"/> and <see cref="SetAttribute"/>.</summary>
    private protected sealed class AttributeTable
    {
        private readonly Dictionary<VisaAttribute, (Func<object?> Get, Action<object?>? Set)> _entries = new();

        public IReadOnlyCollection<VisaAttribute> Keys => _entries.Keys;

        public bool Contains(VisaAttribute attribute) => _entries.ContainsKey(attribute);

        public bool IsWritable(VisaAttribute attribute) => _entries.TryGetValue(attribute, out var entry) && entry.Set is not null;

        /// <summary>Registers an attribute; values passed to the setter are converted to <typeparamref name="T"/> first.</summary>
        public void Add<T>(VisaAttribute attribute, Func<T> get, Action<T>? set = null)
        {
            _entries[attribute] = (() => get(), set is null ? null : v => set(ValueConverter.Convert<T>(v)));
        }

        public object? Get(VisaAttribute attribute)
        {
            if (!_entries.TryGetValue(attribute, out var entry))
                throw new VisaException(VisaStatus.ErrorAttributeNotSupported, $"{VisaAttributeNames.GetName(attribute)} is not supported.");
            try
            {
                return entry.Get();
            }
            catch (Exception ex) when (ex is not VisaException)
            {
                throw VisaException.From(ex);
            }
        }

        public void Set(VisaAttribute attribute, object? value)
        {
            if (!_entries.TryGetValue(attribute, out var entry))
                throw new VisaException(VisaStatus.ErrorAttributeNotSupported, $"{VisaAttributeNames.GetName(attribute)} is not supported.");
            if (entry.Set is null)
                throw new VisaException(VisaStatus.ErrorAttributeReadOnly, $"{VisaAttributeNames.GetName(attribute)} is read-only.");
            try
            {
                entry.Set(value);
            }
            catch (VisaException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new VisaException(VisaStatus.ErrorAttributeStateNotSupported,
                    $"Invalid value '{value}' for {VisaAttributeNames.GetName(attribute)}: {ex.Message}", ex);
            }
        }
    }
}

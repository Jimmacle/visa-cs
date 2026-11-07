using System.Diagnostics.CodeAnalysis;
using VisaCs.Discovery;
using VisaCs.Gpib;
using VisaCs.Internal;
using VisaCs.Usb;

namespace VisaCs;

/// <summary>
/// The VISA resource manager (viOpenDefaultRM): parses resource names, finds resources and opens sessions.
/// Opening, finding and closing have asynchronous forms that hold no thread while waiting for the network.
/// </summary>
/// <remarks>
/// Every member may be called from any thread. Disposing the resource manager closes every session it opened
/// (as <see cref="VisaSession.Dispose"/> does, without waiting for running operations) and makes later
/// <c>Open</c>/<c>Find</c> calls throw <see cref="ObjectDisposedException"/>; a session still being opened at that
/// moment is closed when its open completes. Locks are shared by all resource managers of the process.
/// </remarks>
public sealed class ResourceManager : IDisposable, IAsyncDisposable
{
    private const string DefaultFindExpression = "?*::INSTR";

    private readonly Lock _sync = new();
    private readonly List<VisaSession> _sessions = new();
    private IUsbBackend? _usbBackend;
    private bool _usbResolved;
    private IGpibDriver? _gpibDriver;
    private bool _gpibResolved;
    private bool _disposed;

    /// <summary>Creates a resource manager (viOpenDefaultRM) with default options.</summary>
    public ResourceManager() : this(new ResourceManagerOptions())
    {
    }

    /// <summary>Creates a resource manager (viOpenDefaultRM) with the given options, which it keeps using.</summary>
    public ResourceManager(ResourceManagerOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Settings used by this resource manager. See <see cref="ResourceManagerOptions"/> for when changes apply.</summary>
    public ResourceManagerOptions Options { get; }

    /// <summary>Version of this VISA implementation (also reported as VI_ATTR_RSRC_IMPL_VERSION).</summary>
    public static Version ImplementationVersion => typeof(ResourceManager).Assembly.GetName().Version ?? new Version(1, 0);

    /// <summary>A snapshot of the sessions currently open through this resource manager.</summary>
    public IReadOnlyList<VisaSession> Sessions
    {
        get
        {
            lock (_sync) return _sessions.ToArray();
        }
    }

    /// <summary>
    /// The USB backend in use: <see cref="ResourceManagerOptions.UsbBackend"/>, or the process-wide default from
    /// <see cref="UsbBackends.CreateDefault"/> (created on first use and shared by every resource manager, so sessions of
    /// different resource managers can share a device). Null if the system has no USB support. The resource manager
    /// never disposes it.
    /// </summary>
    public IUsbBackend? UsbBackend
    {
        get
        {
            lock (_sync)
            {
                if (_usbResolved) return _usbBackend;
                _usbResolved = true;
                _usbBackend = Options.UsbBackend ?? DefaultUsbBackend.Value;
                return _usbBackend;
            }
        }
    }

    /// <summary>
    /// The GPIB driver in use: <see cref="ResourceManagerOptions.GpibDriver"/> or the NI-488.2 compatible driver, chosen
    /// on first use. Null if no driver is installed.
    /// </summary>
    public IGpibDriver? GpibDriver
    {
        get
        {
            lock (_sync)
            {
                if (_gpibResolved) return _gpibDriver;
                _gpibResolved = true;
                var driver = Options.GpibDriver ?? new Ni488Driver();
                _gpibDriver = driver.IsAvailable ? driver : null;
                return _gpibDriver;
            }
        }
    }

    // One default backend for the process: the shared-device registry is per backend instance, and a libusb context
    // lives as long as the process anyway.
    private static readonly Lazy<IUsbBackend?> DefaultUsbBackend = new(() =>
    {
        try
        {
            return UsbBackends.CreateDefault();
        }
        catch (Exception)
        {
            return null; // No USB support on this system.
        }
    });

    // ------------------------------------------------------------------ parsing

    /// <summary>
    /// Parses a resource name or an alias from <see cref="ResourceManagerOptions.Aliases"/> (viParseRsrcEx). Invalid
    /// names throw VI_ERROR_INV_RSRC_NAME.
    /// </summary>
    public ResourceName Parse(string resourceName)
    {
        ArgumentNullException.ThrowIfNull(resourceName);
        string trimmed = resourceName.Trim();
        if (Options.Aliases.TryGetValue(trimmed, out var target))
            return ResourceName.Parse(target) with { Alias = trimmed, OriginalName = resourceName };
        return ResourceName.Parse(trimmed);
    }

    /// <summary>Parses a resource name or alias; returns false instead of throwing.</summary>
    public bool TryParse(string resourceName, [NotNullWhen(true)] out ResourceName? result)
    {
        try
        {
            result = Parse(resourceName);
            return true;
        }
        catch (VisaException)
        {
            result = null;
            return false;
        }
    }

    // ------------------------------------------------------------------ opening

    /// <summary>
    /// Opens a message-based session (viOpen), e.g. <c>Open("TCPIP::192.168.1.5::INSTR")</c>. Connecting is bounded by
    /// <see cref="ResourceManagerOptions.ConnectTimeoutMilliseconds"/>. With <see cref="AccessModes.ExclusiveLock"/> or
    /// <see cref="AccessModes.SharedLock"/> the session is locked before it is returned, waiting up to
    /// <paramref name="openTimeoutMilliseconds"/> (-1: no limit) for other sessions' locks. Register-based resources
    /// (PXI) throw VI_ERROR_INV_OBJECT; open them with <see cref="Open{T}"/>.
    /// </summary>
    public MessageBasedSession Open(string resourceName, AccessModes accessModes = AccessModes.None, int openTimeoutMilliseconds = 2000) =>
        Open<MessageBasedSession>(resourceName, accessModes, openTimeoutMilliseconds);

    /// <summary>Opens a message-based session without blocking; see <see cref="Open(string, AccessModes, int)"/>.</summary>
    public Task<MessageBasedSession> OpenAsync(string resourceName, CancellationToken cancellationToken = default) =>
        OpenAsync<MessageBasedSession>(resourceName, AccessModes.None, 0, cancellationToken, async: true).AsTask();

    /// <summary>Opens and optionally locks a message-based session without blocking; see <see cref="Open(string, AccessModes, int)"/>.</summary>
    public Task<MessageBasedSession> OpenAsync(string resourceName, AccessModes accessModes, int openTimeoutMilliseconds,
        CancellationToken cancellationToken = default) =>
        OpenAsync<MessageBasedSession>(resourceName, accessModes, openTimeoutMilliseconds, cancellationToken, async: true).AsTask();

    /// <summary>
    /// Opens a session of a specific type, e.g. <c>Open&lt;TcpipHislipSession&gt;(...)</c>, or
    /// <c>Open&lt;VisaSession&gt;(...)</c> for any resource including register-based ones (<see cref="PxiSession"/>).
    /// The resource is connected first, so that a resource that cannot be opened reports why; if it is not a
    /// <typeparamref name="T"/>, it is closed again and VI_ERROR_INV_OBJECT is thrown. See
    /// <see cref="Open(string, AccessModes, int)"/> for the other parameters.
    /// </summary>
    public T Open<T>(string resourceName, AccessModes accessModes = AccessModes.None, int openTimeoutMilliseconds = 2000)
        where T : VisaSession =>
        AsyncIO.Complete(OpenAsync<T>(resourceName, accessModes, openTimeoutMilliseconds, CancellationToken.None, async: false));

    /// <summary>
    /// Opens a session of a specific type without blocking; see <see cref="Open{T}"/>. Cancelling
    /// <paramref name="cancellationToken"/> abandons the connection attempt with <see cref="OperationCanceledException"/>.
    /// </summary>
    public Task<T> OpenAsync<T>(string resourceName, CancellationToken cancellationToken = default) where T : VisaSession =>
        OpenAsync<T>(resourceName, AccessModes.None, 0, cancellationToken, async: true).AsTask();

    /// <summary>Opens and optionally locks a session of a specific type without blocking; see <see cref="Open{T}"/>.</summary>
    public Task<T> OpenAsync<T>(string resourceName, AccessModes accessModes, int openTimeoutMilliseconds,
        CancellationToken cancellationToken = default) where T : VisaSession =>
        OpenAsync<T>(resourceName, accessModes, openTimeoutMilliseconds, cancellationToken, async: true).AsTask();

    private async ValueTask<T> OpenAsync<T>(string resourceName, AccessModes accessModes, int openTimeoutMilliseconds,
        CancellationToken cancellationToken, bool async) where T : VisaSession
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if ((accessModes & ~(AccessModes.ExclusiveLock | AccessModes.SharedLock | AccessModes.LoadConfig)) != 0 ||
            (accessModes.HasFlag(AccessModes.ExclusiveLock) && accessModes.HasFlag(AccessModes.SharedLock)))
            throw new VisaException(VisaStatus.ErrorInvalidAccessMode);

        var name = Parse(resourceName);
        VisaSession session = name switch
        {
            TcpipInstrumentResourceName t when t.IsHislip => new TcpipHislipSession(this, t),
            TcpipInstrumentResourceName t => new TcpipVxi11Session(this, t),
            TcpipSocketResourceName s => new TcpipSocketSession(this, s),
            UsbResourceName { ResourceClass: ResourceClass.Raw } u => new UsbRawSession(this, u),
            UsbResourceName u => new UsbSession(this, u),
            SerialResourceName a => new SerialSession(this, a),
            GpibInstrumentResourceName g => new GpibSession(this, g),
            GpibInterfaceResourceName g => new GpibInterfaceSession(this, g),
            VicpResourceName v => new VicpSession(this, v),
            PxiResourceName x => new PxiSession(this, x),
            _ => throw new VisaException(VisaStatus.ErrorResourceNotFound,
                $"{name.InterfaceType} resources need vendor hardware drivers and are not supported by this implementation."),
        };

        try
        {
            // Open before checking the type, so that a resource that cannot be opened here reports why.
            await session.OpenAsync(Options.ConnectTimeoutMilliseconds, cancellationToken, async).ConfigureAwait(false);
            if (session is not T typed)
            {
                string message = typeof(T) == typeof(MessageBasedSession)
                    ? $"{resourceName} is a register-based resource; open it with Open<{session.GetType().Name}>() or Open<VisaSession>()."
                    : $"{resourceName} opens as {session.GetType().Name}, not {typeof(T).Name}.";
                throw new VisaException(VisaStatus.ErrorInvalidObject, message);
            }
            lock (_sync)
            {
                // Checked again here: Dispose may have run while the connection was being set up.
                ObjectDisposedException.ThrowIf(_disposed, this);
                _sessions.Add(session);
            }
            if (accessModes.HasFlag(AccessModes.ExclusiveLock))
            {
                if (async) await session.LockExclusiveAsync(openTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
                else session.LockExclusive(openTimeoutMilliseconds);
            }
            else if (accessModes.HasFlag(AccessModes.SharedLock))
            {
                if (async) await session.LockSharedAsync(openTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
                else session.LockShared(openTimeoutMilliseconds);
            }
            return typed;
        }
        catch
        {
            try
            {
                if (async) await session.DisposeAsync().ConfigureAwait(false);
                else session.Dispose();
            }
            catch
            {
                // The original error matters more.
            }
            throw;
        }
    }

    internal void Unregister(VisaSession session)
    {
        lock (_sync) _sessions.Remove(session);
    }

    // ------------------------------------------------------------------ finding

    /// <summary>
    /// Finds resources matching a VISA expression (viFindRsrc), e.g. <c>?*::INSTR</c> (the default), <c>USB?*</c>,
    /// <c>TCPIP?*{VI_ATTR_TCPIP_ADDR=="192.168.1.5"}</c>, and returns their canonical names, sorted. Returns an empty list
    /// when nothing matches; an invalid expression throws VI_ERROR_INV_EXPR.
    /// </summary>
    /// <remarks>
    /// Sources: LAN discovery (VXI-11 broadcast and mDNS, which listen for
    /// <see cref="ResourceManagerOptions.LanDiscoveryTimeoutMilliseconds"/>), USB devices, serial ports, GPIB boards and
    /// their listeners, PXI functions (Linux) and <see cref="ResourceManagerOptions.StaticResources"/>, each switched by
    /// its <see cref="ResourceManagerOptions"/> setting. Sources the expression cannot match are skipped. A source that
    /// fails is left out silently. The synchronous form runs LAN discovery on thread-pool threads while it enumerates the
    /// local interfaces on the calling thread.
    /// </remarks>
    public IReadOnlyList<string> Find(string expression = DefaultFindExpression) =>
        FindResources(expression).Select(r => r.ResourceName).ToList();

    /// <summary>Finds every <c>?*::INSTR</c> resource without blocking; see <see cref="Find"/>.</summary>
    public Task<IReadOnlyList<string>> FindAsync(CancellationToken cancellationToken = default) =>
        FindAsync(DefaultFindExpression, cancellationToken);

    /// <summary>
    /// Finds resources matching a VISA expression without blocking; see <see cref="Find"/>. LAN discovery holds no
    /// thread while it listens; the local enumerations run on a thread-pool thread.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindAsync(string expression, CancellationToken cancellationToken = default) =>
        (await FindResourcesAsync(expression, cancellationToken).ConfigureAwait(false)).Select(r => r.ResourceName).ToList();

    /// <summary>Like <see cref="Find"/>, with descriptions and the attributes known from discovery.</summary>
    public IReadOnlyList<ResourceInfo> FindResources(string expression = DefaultFindExpression) =>
        AsyncIO.Complete(FindResourcesAsync(expression, CancellationToken.None, async: false));

    /// <summary>Like <see cref="FindAsync(CancellationToken)"/>, with descriptions and the attributes known from discovery.</summary>
    public Task<IReadOnlyList<ResourceInfo>> FindResourcesAsync(CancellationToken cancellationToken = default) =>
        FindResourcesAsync(DefaultFindExpression, cancellationToken);

    /// <summary>Like <see cref="FindAsync(string, CancellationToken)"/>, with descriptions and the attributes known from discovery.</summary>
    public async Task<IReadOnlyList<ResourceInfo>> FindResourcesAsync(string expression, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await FindResourcesAsync(expression, cancellationToken, async: true).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private async ValueTask<IReadOnlyList<ResourceInfo>> FindResourcesAsync(string expression, CancellationToken cancellationToken, bool async)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var pattern = VisaPattern.Parse(expression);
        bool Wants(string intf) => CouldMatchInterface(expression, intf);

        // LAN discovery waits for network answers; it runs while the local sources are enumerated.
        var lan = Wants("TCPIP") ? LanSearch.Start(Options, cancellationToken, async) : null;

        var all = new List<ResourceInfo>();
        foreach (var r in Options.StaticResources)
        {
            if (TryParse(r, out var parsed)) all.Add(ResourceDiscovery.Describe(parsed, null, new Dictionary<VisaAttribute, object>()));
        }
        all.AddRange(async ? await Task.Run(EnumerateLocal).ConfigureAwait(false) : EnumerateLocal());
        if (lan is not null) all.AddRange(await lan.CollectAsync(async).ConfigureAwait(false));

        var aliasesByTarget = Options.Aliases
            .GroupBy(kv => TryParse(kv.Value, out var p) ? p.Canonical : kv.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ResourceInfo>();
        foreach (var info in all)
        {
            if (!seen.Add(info.ResourceName)) continue;
            if (!pattern.IsMatch(info.ResourceName, info.Attributes)) continue;
            result.Add(aliasesByTarget.TryGetValue(info.ResourceName, out var alias) ? info with { Alias = alias } : info);
        }
        result.Sort((a, b) => string.Compare(a.ResourceName, b.ResourceName, StringComparison.OrdinalIgnoreCase));
        return result;

        List<ResourceInfo> EnumerateLocal()
        {
            var found = new List<ResourceInfo>();
            void Add(Func<List<ResourceInfo>> source)
            {
                try { found.AddRange(source()); }
                catch (Exception) { /* a failing source must not hide the others */ }
            }
            if (Wants("USB") && Options.EnableUsb) Add(() => ResourceDiscovery.DiscoverUsb(UsbBackend));
            if (Wants("ASRL") && Options.EnableSerial) Add(ResourceDiscovery.DiscoverSerial);
            if (Wants("GPIB") && Options.EnableGpib) Add(() => ResourceDiscovery.DiscoverGpib(GpibDriver));
            if (Wants("PXI") && Options.EnablePxi && OperatingSystem.IsLinux()) Add(() => ResourceDiscovery.DiscoverPxi(Options.PciDevicesPath));
            return found;
        }
    }

    /// <summary>
    /// False only when <paramref name="expression"/> certainly cannot match a resource of interface
    /// <paramref name="interfacePrefix"/> (e.g. <c>USB?*</c> and <c>TCPIP</c>), so that source can be skipped.
    /// </summary>
    internal static bool CouldMatchInterface(string expression, string interfacePrefix)
    {
        // Only a plain literal prefix is used; alternatives and groups could match anything.
        int i = 0;
        while (i < expression.Length && (char.IsLetter(expression[i]) || expression[i] == '-')) i++;
        if (i == 0 || expression.IndexOfAny(['|', '(']) >= 0) return true;
        // A quantifier right after the prefix makes its last character optional ("TCPIPX*").
        if (i < expression.Length && expression[i] is '*' or '+') i--;
        var prefix = expression.AsSpan(0, i);
        return interfacePrefix.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
               prefix.StartsWith(interfacePrefix, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ disposal

    /// <summary>Closes every session opened by this resource manager.</summary>
    public void Dispose() => AsyncIO.Complete(DisposeAsync(async: false));

    /// <summary>Closes every session opened by this resource manager, without blocking; see <see cref="Dispose"/>.</summary>
    public ValueTask DisposeAsync() => DisposeAsync(async: true);

    private async ValueTask DisposeAsync(bool async)
    {
        VisaSession[] sessions;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            sessions = _sessions.ToArray();
        }
        foreach (var s in sessions)
        {
            try
            {
                if (async) await s.DisposeAsync().ConfigureAwait(false);
                else s.Dispose();
            }
            catch
            {
                // Keep closing the others.
            }
        }
    }
}

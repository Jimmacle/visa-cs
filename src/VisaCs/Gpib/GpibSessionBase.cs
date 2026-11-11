using VisaCs.Gpib;
using VisaCs.Internal;

namespace VisaCs;

/// <summary>
/// Common base of the GPIB sessions (<see cref="GpibSession"/>, <see cref="GpibInterfaceSession"/>). They talk through
/// the resource manager's GPIB driver (<see cref="ResourceManager.GpibDriver"/>); END is EOI, and the termination
/// character is handled by the driver. Each driver call gets the time left of the session timeout, rounded up by the
/// driver (NI-488.2 timeouts go in steps: 10 ms, 30 ms, 100 ms, 300 ms, 1 s, 3 s, ... 1000 s).
/// </summary>
public abstract class GpibSessionBase : MessageBasedSession
{
    private GpibSrqWatcher? _srq;

    private protected GpibSessionBase(ResourceManager manager, ResourceName name) : base(manager, name, 64 * 1024)
    {
    }

    /// <inheritdoc/>
    public override IReadOnlyCollection<EventType> SupportedEventTypes { get; } = [EventType.ServiceRequest];

    private protected IGpibDriver Driver => ResourceManager.GpibDriver
        ?? throw new VisaException(VisaStatus.ErrorLibraryNotFound, "No GPIB driver is available.");

    /// <summary>The device or board descriptor transfers go through; throws when the session is not open.</summary>
    private protected abstract IGpibIO IO { get; }

    /// <summary>Releases the descriptors; called once the service request watcher has stopped.</summary>
    private protected abstract void CloseDescriptors();

    /// <summary>A wait for the next service request event: null when none came within the wait.</summary>
    private protected abstract Func<CancellationToken, ValueTask<ServiceRequestEventArgs?>> CreateServiceRequestWait();

    private protected override async ValueTask CloseCoreAsync(bool async)
    {
        if (Interlocked.Exchange(ref _srq, null) is { } srq) await srq.StopAsync(async).ConfigureAwait(false);
        CloseDescriptors();
    }

    private protected override ValueTask WriteCoreAsync(ReadOnlyMemory<byte> data, bool end, Deadline deadline, bool async)
    {
        var io = IO;
        int timeout = GpibTimeouts.Milliseconds(deadline);
        if (async) return GpibErrorMapping.CallAsync(() => io.WriteAsync(data, end, timeout, deadline.CancellationToken));
        GpibErrorMapping.Call(() => io.Write(data.Span, end, timeout));
        return default;
    }

    private protected override async ValueTask<FillResult> FillCoreAsync(Memory<byte> target, Deadline deadline, bool async)
    {
        byte? termChar = EffectiveTermChar is >= 0 and var c ? (byte)c : null;
        var io = IO;
        int timeout = GpibTimeouts.Milliseconds(deadline);
        var result = async
            ? await GpibErrorMapping.CallAsync(() => io.ReadAsync(target, termChar, timeout, deadline.CancellationToken)).ConfigureAwait(false)
            : GpibErrorMapping.Call(() => io.Read(target.Span, termChar, timeout));
        return new FillResult(result.Count, result.End);
    }

    private protected override ValueTask OnEventEnabledAsync(EventType eventType, bool async)
    {
        var srq = new GpibSrqWatcher(CreateServiceRequestWait(), RaiseEvent);
        _srq = srq;
        srq.Start();
        return default;
    }

    private protected override ValueTask OnEventDisabledAsync(EventType eventType, bool async) =>
        Interlocked.Exchange(ref _srq, null) is { } srq ? srq.StopAsync(async) : default;

    /// <summary>The state of one bus line of <paramref name="board"/>.</summary>
    private protected static LineState LineFrom(IGpibBoard board, GpibLines line) =>
        GpibErrorMapping.Call(board.GetLines).IsAsserted(line) switch
    {
        null => LineState.Unknown,
        true => LineState.Asserted,
        false => LineState.Unasserted,
    };
}

/// <summary>
/// Raises service request events for a GPIB session: an asynchronous loop over the session's wait, which holds no
/// thread while waiting.
/// </summary>
internal sealed class GpibSrqWatcher(Func<CancellationToken, ValueTask<ServiceRequestEventArgs?>> wait, Action<ServiceRequestEventArgs> raise)
{
    /// <summary>Pause after a failed wait before trying again.</summary>
    private const int RetryMs = 100;

    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public void Start() => _loop = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (await wait(_stop.Token).ConfigureAwait(false) is { } e) raise(e);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or VisaException { Status: VisaStatus.ErrorLibraryNotFound })
            {
                return; // the descriptor was closed, or there is no driver: waiting again cannot succeed
            }
            catch (Exception)
            {
                try
                {
                    await Task.Delay(RetryMs, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    public async ValueTask StopAsync(bool async)
    {
        _stop.Cancel();
        if (_loop is null) return;
        try
        {
            if (async) await _loop.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            else _loop.Wait(1000);
        }
        catch
        {
            // The loop ends on its own; errors during shutdown do not matter.
        }
    }
}

/// <summary>Driver timeouts for the GPIB sessions.</summary>
internal static class GpibTimeouts
{
    /// <summary>Driver timeout for one call: the time left, or <see cref="Timeout.Infinite"/>; throws when already expired.</summary>
    public static int Milliseconds(Deadline deadline)
    {
        if (deadline.IsInfinite && !deadline.IsCanceled) return Timeout.Infinite;
        int remaining = deadline.RemainingMilliseconds;
        if (remaining <= 0) throw new VisaTimeoutException();
        return remaining;
    }

    public static ValueTask SendCommandAsync(IGpibBoard board, ReadOnlyMemory<byte> bytes, Deadline deadline, bool async)
    {
        int timeout = Milliseconds(deadline);
        if (async) return GpibErrorMapping.CallAsync(() => board.SendCommandAsync(bytes, timeout, deadline.CancellationToken));
        GpibErrorMapping.Call(() => board.SendCommand(bytes.Span, timeout));
        return default;
    }
}

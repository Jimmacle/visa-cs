using System.Text;
using VisaCs.Formatting;
using VisaCs.Internal;

namespace VisaCs;

/// <summary>
/// A message-based VISA session (viRead/viWrite/viReadSTB/viClear/viAssertTrigger plus formatted I/O helpers).
/// Every session type returned by <see cref="ResourceManager.Open(string, AccessModes, int)"/> derives from this class.
/// </summary>
/// <remarks>
/// <para>
/// Every operation that talks to the device exists in a synchronous and an asynchronous form. The asynchronous
/// methods use asynchronous I/O all the way down (sockets, overlapped and polled device I/O, USB transfers) and
/// hold no thread while waiting. Operations on one session run one at a time; see <see cref="VisaSession"/> for the
/// thread-safety, error and disposal rules.
/// </para>
/// <para>
/// <see cref="VisaSession.Timeout"/> bounds each transfer, not the whole call: <see cref="Query(string)"/> may wait up to the
/// timeout for the write and again for the read, and <see cref="ReadToEnd"/> up to the timeout for each
/// <see cref="ReadChunkSize"/> chunk. A timeout throws <see cref="VisaTimeoutException"/>. Cancelling the token of an
/// asynchronous call stops the transfer in progress (VXI-11 also sends device_abort, USBTMC runs its abort sequence)
/// and throws <see cref="OperationCanceledException"/>. Either way the session stays open, but the device may still
/// send (the rest of) the response: call <see cref="Clear"/> before the next command.
/// </para>
/// </remarks>
public abstract partial class MessageBasedSession : VisaSession
{
    private readonly InputBuffer _input;
    private readonly byte[] _scratch = new byte[64];
    private bool _suppressTermChar;
    private int _termOverride = -2;

    private readonly FillCallback _fill;
    private readonly FillCallback _fillSuppressingEnd;

    private protected MessageBasedSession(ResourceManager manager, ResourceName resource, int inputBufferSize = 64 * 1024)
        : base(manager, resource)
    {
        _input = new InputBuffer(inputBufferSize);
        _fill = FillCoreAsync;
        _fillSuppressingEnd = FillSuppressingEndAsync;
    }

    private async ValueTask<FillResult> FillSuppressingEndAsync(Memory<byte> target, Deadline deadline, bool async)
    {
        var result = await FillCoreAsync(target, deadline, async).ConfigureAwait(false);
        return result with { End = false };
    }

    // ------------------------------------------------------------------ configuration

    /// <summary>
    /// Termination character (VI_ATTR_TERMCHAR). Reads stop after it when <see cref="TerminationCharacterEnabled"/>
    /// is true. Default <c>'\n'</c>.
    /// </summary>
    public byte TerminationCharacter { get; set; } = (byte)'\n';

    /// <summary>
    /// Whether reads stop at <see cref="TerminationCharacter"/> (VI_ATTR_TERMCHAR_EN). Default false, except for
    /// TCPIP SOCKET sessions, whose protocol has no END indicator. Serial sessions also stop there when their END input
    /// mode is the termination character.
    /// </summary>
    public bool TerminationCharacterEnabled { get; set; }

    /// <summary>Whether the END indicator is sent with the last byte of each write (VI_ATTR_SEND_END_EN). Default true.</summary>
    public bool SendEndEnabled { get; set; } = true;

    /// <summary>
    /// Whether reads ignore the END indicator (VI_ATTR_SUPPRESS_END_EN), so that only the termination character or a
    /// full buffer ends them. Default false.
    /// </summary>
    public bool SuppressEndEnabled { get; set; }

    /// <summary>
    /// I/O protocol (VI_ATTR_IO_PROT). Every session supports <see cref="IOProtocol.Normal"/>; serial and socket
    /// sessions also support <see cref="IOProtocol.Ieee4882Strings"/>, and USB INSTR sessions
    /// <see cref="IOProtocol.UsbTmcVendor"/>. Other values throw VI_ERROR_NSUP_ATTR_STATE.
    /// </summary>
    public IOProtocol IOProtocol
    {
        get;
        set
        {
            if (!SupportsIOProtocol(value))
                throw new VisaException(VisaStatus.ErrorAttributeStateNotSupported, $"I/O protocol {value} is not supported by {GetType().Name}.");
            field = value;
        }
    } = IOProtocol.Normal;

    /// <summary>True if the session implements the I/O protocol.</summary>
    private protected virtual bool SupportsIOProtocol(IOProtocol protocol) => protocol == IOProtocol.Normal;

    /// <summary>
    /// Text encoding of the string methods (<see cref="Query(string)"/>, <see cref="WriteLine"/>, <see cref="ReadString"/>,
    /// <see cref="Printf"/>, ...). Default Latin-1, which maps bytes to characters one to one.
    /// </summary>
    public Encoding Encoding
    {
        get;
        set => field = value ?? throw new ArgumentNullException(nameof(value));
    } = Encoding.Latin1;

    /// <summary>
    /// Appended to commands by <see cref="WriteLine"/>, every <c>Query*</c> method, <see cref="WriteBinaryBlock"/>,
    /// <see cref="WriteBinaryValues{T}"/> and <see cref="WriteAsciiValues{T}"/>; not by <see cref="Write(string)"/>,
    /// <see cref="Printf"/> or <see cref="Queryf(string, string, object?[])"/>, whose format supplies it. Default "\n".
    /// </summary>
    public string WriteTermination
    {
        get;
        set => field = value ?? throw new ArgumentNullException(nameof(value));
    } = "\n";

    /// <summary>
    /// How long every <c>Query*</c> method (and <see cref="Queryf(string, string, object?[])"/>) waits between writing
    /// the command and reading the response, for instruments that need time before they answer. Default zero.
    /// The wait counts toward neither transfer's timeout; negative values throw <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    public TimeSpan QueryDelay
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            field = value;
        }
    }

    /// <summary>
    /// Size of each read when a complete response is read (<see cref="ReadToEnd"/> and the methods built on it); the
    /// timeout applies to each chunk. Also exposed as VI_ATTR_RD_BUF_SIZE. A buffer of this size is allocated for each
    /// response, so keep it moderate. At least 1; default 20 KiB.
    /// </summary>
    public int ReadChunkSize
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 20 * 1024;

    /// <summary>
    /// Separator of the ASCII arrays read and written by <see cref="ReadAsciiValues()"/>,
    /// <see cref="QueryAsciiValues(string)"/> and <see cref="WriteAsciiValues{T}"/>. When reading, white space
    /// around values and empty fields are ignored, so <c>' '</c> also reads space-separated lists. Default <c>','</c>.
    /// </summary>
    public char AsciiSeparator { get; set; } = ',';

    /// <summary>
    /// Byte order of the binary arrays read and written by <see cref="ReadBinaryValues{T}"/>,
    /// <see cref="QueryBinaryValues{T}"/> and <see cref="WriteBinaryValues{T}"/>. Set it to match the instrument:
    /// <see cref="ByteOrder.BigEndian"/> for SCPI <c>FORM:BORD NORM</c> (the IEEE 488.2 default),
    /// <see cref="ByteOrder.LittleEndian"/> for <c>FORM:BORD SWAP</c>. Default <see cref="ByteOrder.LittleEndian"/>.
    /// </summary>
    public ByteOrder BinaryByteOrder { get; set; } = ByteOrder.LittleEndian;

    /// <summary>Whether the device follows IEEE 488.2 (VI_ATTR_4882_COMPLIANT). True for every message-based session.</summary>
    public virtual bool Is4882Compliant => true;

    /// <summary>Whether reads stop at the termination character (serial also stops when END_IN is TERMCHAR).</summary>
    private protected virtual bool TermCharActive => TerminationCharacterEnabled;

    /// <summary>The termination character the transport should honor for the current read, or -1.</summary>
    private protected int EffectiveTermChar =>
        _termOverride != -2 ? _termOverride : TermCharActive && !_suppressTermChar ? TerminationCharacter : -1;

    /// <summary>True if the protocol has an END indicator (false for raw sockets and serial without END).</summary>
    private protected virtual bool HasEndIndicator => true;

    /// <summary>Report a termination character match as END (serial END_IN = TERMCHAR).</summary>
    private protected virtual bool TermCharIsEnd => false;

    // ------------------------------------------------------------------ transport contract

    /// <summary>Sends <paramref name="data"/>; asserts END with the last byte when <paramref name="end"/> is true.</summary>
    private protected abstract ValueTask WriteCoreAsync(ReadOnlyMemory<byte> data, bool end, Deadline deadline, bool async);

    /// <summary>Returns the next chunk of input; the result's End marks END on its last byte.</summary>
    private protected abstract ValueTask<FillResult> FillCoreAsync(Memory<byte> target, Deadline deadline, bool async);

    private protected virtual async ValueTask<ReadResult> ReadCoreAsync(Memory<byte> buffer, Deadline deadline, bool async)
    {
        var result = await _input.ReadAsync(buffer, EffectiveTermChar, TermCharIsEnd,
            SuppressEndEnabled ? _fillSuppressingEnd : _fill, deadline, async).ConfigureAwait(false);
        OnReadCompleted(result.Status);
        return result;
    }

    /// <summary>Hook after each read (HiSLIP uses it to track RMT delivery).</summary>
    private protected virtual void OnReadCompleted(ReadStatus status)
    {
    }

    private protected virtual ValueTask<byte> ReadStatusByteCoreAsync(Deadline deadline, bool async) =>
        throw VisaException.NotSupported("Reading the status byte");

    private protected virtual ValueTask ClearCoreAsync(Deadline deadline, bool async) => default;

    private protected virtual ValueTask TriggerCoreAsync(Deadline deadline, bool async) => throw VisaException.NotSupported("Assert trigger");

    private protected virtual ValueTask ControlRenCoreAsync(RemoteLocalMode mode, Deadline deadline, bool async) =>
        throw VisaException.NotSupported("Remote/local control");

    /// <summary>Discards input held by the transport below the session's own buffer.</summary>
    private protected virtual void FlushTransportInput()
    {
    }

    /// <summary>Waits until written data has left the transport.</summary>
    private protected virtual ValueTask FlushTransportOutputAsync(Deadline deadline, bool async) => default;

    /// <summary>Discards data that is still waiting in the transport's transmit buffer (viFlush with VI_IO_OUT_BUF_DISCARD).</summary>
    private protected virtual ValueTask DiscardTransportOutputAsync(bool async) => default;

    private protected void DiscardInputBuffer() => _input.Clear();

    // ------------------------------------------------------------------ running operations

    /// <summary>
    /// <see cref="VisaSession.ExecuteAsync{TState, TResult}"/> with the session typed as a
    /// <see cref="MessageBasedSession"/>.
    /// </summary>
    private protected ValueTask<TResult> ExecuteAsync<TState, TResult>(TState state,
        Func<MessageBasedSession, TState, CancellationToken, bool, ValueTask<TResult>> operation, CancellationToken cancellationToken, bool async) =>
        base.ExecuteAsync((operation, state), static (s, a, ct, async) => a.operation((MessageBasedSession)s, a.state, ct, async),
            cancellationToken, async);

    /// <summary>
    /// <see cref="VisaSession.ExecuteAsync{TState}"/> with the session typed as a <see cref="MessageBasedSession"/>.
    /// </summary>
    private protected ValueTask ExecuteAsync<TState>(TState state,
        Func<MessageBasedSession, TState, CancellationToken, bool, ValueTask> operation, CancellationToken cancellationToken, bool async) =>
        base.ExecuteAsync((operation, state), static (s, a, ct, async) => a.operation((MessageBasedSession)s, a.state, ct, async),
            cancellationToken, async);

    // ------------------------------------------------------------------ attributes

    private protected override void RegisterAttributes(AttributeTable table)
    {
        base.RegisterAttributes(table);
        table.Add(VisaAttribute.TerminationCharacter, () => TerminationCharacter, v => TerminationCharacter = v);
        table.Add(VisaAttribute.TerminationCharacterEnabled, () => TerminationCharacterEnabled, v => TerminationCharacterEnabled = v);
        table.Add(VisaAttribute.SendEndEnabled, () => SendEndEnabled, v => SendEndEnabled = v);
        table.Add(VisaAttribute.SuppressEndEnabled, () => SuppressEndEnabled, v => SuppressEndEnabled = v);
        table.Add(VisaAttribute.IOProtocol, () => IOProtocol, v => IOProtocol = v);
        table.Add(VisaAttribute.Is4882Compliant, () => Is4882Compliant);
        table.Add(VisaAttribute.ReadBufferSize, () => ReadChunkSize, v => ReadChunkSize = v);
    }
}

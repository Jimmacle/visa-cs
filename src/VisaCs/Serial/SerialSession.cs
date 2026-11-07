using VisaCs.Internal;
using VisaCs.Serial;

namespace VisaCs;

/// <summary>
/// Serial instrument session (<c>ASRL1::INSTR</c>, <c>ASRLCOM3::INSTR</c>, <c>ASRL/dev/ttyUSB0::INSTR</c>) over a
/// <see cref="NativeSerialPort"/>.
/// </summary>
/// <remarks>
/// <para>
/// Defaults follow VISA: 9600 baud, 8 data bits, no parity, one stop bit, no flow control, END_IN = termination
/// character ('\n'), END_OUT = none. Opening the session applies them and asserts DTR and RTS.
/// </para>
/// <para>
/// Setting a line setting (<see cref="BaudRate"/> to <see cref="DiscardNull"/>) reconfigures the open port at once;
/// the setter waits for an I/O operation in progress to finish. A value out of range, or one the platform or port
/// does not support, throws <see cref="VisaException"/> (<see cref="VisaStatus.ErrorAttributeStateNotSupported"/>)
/// and the property keeps its previous value. The modem lines (<see cref="RtsState"/>, <see cref="DtrState"/>,
/// <see cref="BreakState"/>) and <see cref="AllowTransmit"/> act immediately, even while a transfer is in progress.
/// After the session is closed, these properties throw <see cref="VisaException"/>
/// (<see cref="VisaStatus.ErrorInvalidObject"/>).
/// </para>
/// </remarks>
public sealed class SerialSession : MessageBasedSession
{
    private readonly SerialResourceName _name;
    private NativeSerialPort? _port;
    private SerialSettings _settings = SerialSettings.Default;
    private bool _allowTransmit = true;
    private byte[] _pending = Array.Empty<byte>();
    private int _pendingOffset;
    private int _pendingCount;

    internal SerialSession(ResourceManager manager, SerialResourceName name) : base(manager, name, 16 * 1024)
    {
        _name = name;
    }

    /// <summary>Operating system port name, e.g. <c>COM3</c> or <c>/dev/ttyUSB0</c>.</summary>
    public string PortName => _name.PortName;

    /// <inheritdoc/>
    public override bool Is4882Compliant => false;

    private NativeSerialPort Port => _port ?? throw new VisaException(VisaStatus.ErrorInvalidObject, "The session is closed.");

    /// <summary>
    /// Baud rate in bits per second (VI_ATTR_ASRL_BAUD). Default 9600. Non-standard rates work on Linux and, where the
    /// driver accepts them, on Windows and macOS.
    /// </summary>
    public int BaudRate
    {
        get => _settings.BaudRate;
        set
        {
            if (value < 1) throw new VisaException(VisaStatus.ErrorAttributeStateNotSupported, "The baud rate must be at least 1.");
            Reconfigure(static (s, v) => s with { BaudRate = v }, value);
        }
    }

    /// <summary>Data bits per character, 5 to 8 (VI_ATTR_ASRL_DATA_BITS). Default 8.</summary>
    public int DataBits
    {
        get => _settings.DataBits;
        set
        {
            if (value is < 5 or > 8) throw new VisaException(VisaStatus.ErrorAttributeStateNotSupported, "Data bits must be between 5 and 8.");
            Reconfigure(static (s, v) => s with { DataBits = v }, value);
        }
    }

    /// <summary>Parity (VI_ATTR_ASRL_PARITY). Default none. Mark and space parity are not supported on macOS.</summary>
    public SerialParity Parity
    {
        get => _settings.Parity;
        set => Reconfigure(static (s, v) => s with { Parity = v }, value);
    }

    /// <summary>Stop bits (VI_ATTR_ASRL_STOP_BITS). Default one. Linux and macOS send two stop bits for 1.5.</summary>
    public SerialStopBitsMode StopBits
    {
        get => _settings.StopBits;
        set => Reconfigure(static (s, v) => s with { StopBits = v }, value);
    }

    /// <summary>Flow control (VI_ATTR_ASRL_FLOW_CNTRL). Default none. DTR/DSR flow control is Windows only.</summary>
    public SerialFlowControlModes FlowControl
    {
        get => _settings.FlowControl;
        set => Reconfigure(static (s, v) => s with { FlowControl = v }, value);
    }

    /// <summary>Character that resumes transmission under XON/XOFF flow control (VI_ATTR_ASRL_XON_CHAR). Default 0x11.</summary>
    public byte XOnCharacter
    {
        get => _settings.XOnCharacter;
        set => Reconfigure(static (s, v) => s with { XOnCharacter = v }, value);
    }

    /// <summary>Character that suspends transmission under XON/XOFF flow control (VI_ATTR_ASRL_XOFF_CHAR). Default 0x13.</summary>
    public byte XOffCharacter
    {
        get => _settings.XOffCharacter;
        set => Reconfigure(static (s, v) => s with { XOffCharacter = v }, value);
    }

    /// <summary>
    /// Character substituted for bytes received with parity errors (VI_ATTR_ASRL_REPLACE_CHAR); 0, the default, keeps
    /// them. Windows only: Linux and macOS read such bytes as NUL and reject a non-zero value.
    /// </summary>
    public byte ReplacementCharacter
    {
        get => _settings.ReplacementCharacter;
        set => Reconfigure(static (s, v) => s with { ReplacementCharacter = v }, value);
    }

    /// <summary>Drop received NUL bytes (VI_ATTR_ASRL_DISCARD_NULL). Default false.</summary>
    public bool DiscardNull
    {
        get => _settings.DiscardNull;
        set => Reconfigure(static (s, v) => s with { DiscardNull = v }, value);
    }

    /// <summary>How reads detect END (VI_ATTR_ASRL_END_IN). Default: termination character.</summary>
    public SerialTerminationMethod EndInput { get; set; } = SerialTerminationMethod.TerminationCharacter;

    /// <summary>
    /// How writes mark END (VI_ATTR_ASRL_END_OUT). Default: none. With <see cref="SerialTerminationMethod.LastBit"/>
    /// the highest data bit is cleared in every byte written and set in the last byte of a write with END.
    /// </summary>
    public SerialTerminationMethod EndOutput { get; set; } = SerialTerminationMethod.None;

    /// <summary>
    /// Break duration in milliseconds, at least 1, for END_OUT = Break, <see cref="SendBreak()"/> and
    /// <see cref="MessageBasedSession.Clear"/> (VI_ATTR_ASRL_BREAK_LEN). Default 250.
    /// </summary>
    public int BreakLength
    {
        get;
        set
        {
            if (value < 1) throw new VisaException(VisaStatus.ErrorAttributeStateNotSupported, "The break length must be at least 1 ms.");
            field = value;
        }
    } = 250;

    /// <summary>
    /// Bytes waiting to be read (VI_ATTR_ASRL_AVAIL_NUM): in the driver, plus bytes the session already took from
    /// the port but has not returned (after END_IN = last bit).
    /// </summary>
    public int BytesAvailable => Port.BytesAvailable + _pendingCount;

    /// <summary>
    /// State of the RTS output (VI_ATTR_ASRL_RTS_STATE). Asserted on open. Cannot be set while RTS/CTS flow control
    /// drives it (Windows throws; Linux and macOS ignore it).
    /// </summary>
    public LineState RtsState
    {
        get => ToState(Port.Rts);
        set => Port.Rts = value == LineState.Asserted;
    }

    /// <summary>
    /// State of the DTR output (VI_ATTR_ASRL_DTR_STATE). Asserted on open. Cannot be set while DTR/DSR flow control
    /// drives it.
    /// </summary>
    public LineState DtrState
    {
        get => ToState(Port.Dtr);
        set => Port.Dtr = value == LineState.Asserted;
    }

    /// <summary>State of the CTS input (VI_ATTR_ASRL_CTS_STATE).</summary>
    public LineState CtsState => ToState(Port.Cts);

    /// <summary>State of the DSR input (VI_ATTR_ASRL_DSR_STATE).</summary>
    public LineState DsrState => ToState(Port.Dsr);

    /// <summary>State of the DCD (carrier detect) input (VI_ATTR_ASRL_DCD_STATE).</summary>
    public LineState DcdState => ToState(Port.Dcd);

    /// <summary>State of the RI (ring indicator) input (VI_ATTR_ASRL_RI_STATE).</summary>
    public LineState RiState => ToState(Port.Ri);

    /// <summary>Holds the transmit line in the break state while asserted (VI_ATTR_ASRL_BREAK_STATE).</summary>
    public LineState BreakState
    {
        get => ToState(Port.Break);
        set => Port.Break = value == LineState.Asserted;
    }

    /// <summary>When false, transmission is suspended as if XOFF had been received (VI_ATTR_ASRL_ALLOW_TRANSMIT). Default true.</summary>
    public bool AllowTransmit
    {
        get => _allowTransmit;
        set
        {
            Port.SetTransmitAllowed(value);
            _allowTransmit = value;
        }
    }

    private static LineState ToState(bool asserted) => asserted ? LineState.Asserted : LineState.Unasserted;

    /// <summary>Applies one changed setting to the port; the property keeps its old value if the port rejects it.</summary>
    private void Reconfigure<T>(Func<SerialSettings, T, SerialSettings> change, T value)
    {
        using var gate = IOGate.Enter();
        ThrowIfDisposed();
        var settings = change(_settings, value);
        if (settings == _settings) return;
        try
        {
            _port?.Configure(settings);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new VisaException(VisaStatus.ErrorAttributeStateNotSupported, ex.Message, ex);
        }
        _settings = settings;
    }

    private protected override bool HasEndIndicator => EndInput != SerialTerminationMethod.None;

    private protected override bool TermCharActive =>
        TerminationCharacterEnabled || EndInput == SerialTerminationMethod.TerminationCharacter;

    private protected override bool TermCharIsEnd => EndInput == SerialTerminationMethod.TerminationCharacter;

    private protected override ValueTask OpenCoreAsync(int connectTimeoutMs, CancellationToken cancellationToken, bool async)
    {
        _port = NativeSerialPort.Open(PortName, _settings);
        return default;
    }

    private protected override ValueTask CloseCoreAsync(bool async)
    {
        Interlocked.Exchange(ref _port, null)?.Dispose();
        return default;
    }

    private ValueTask PortWriteAsync(ReadOnlyMemory<byte> data, Deadline deadline, bool async)
    {
        if (async) return Port.WriteAsync(data, deadline.RemainingMilliseconds, deadline.CancellationToken);
        Port.Write(data.Span, deadline.RemainingMilliseconds);
        return default;
    }

    private ValueTask DrainAsync(Deadline deadline, bool async)
    {
        if (async) return Port.DrainAsync(deadline.RemainingMilliseconds, deadline.CancellationToken);
        Port.Drain(deadline.RemainingMilliseconds);
        return default;
    }

    private protected override async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> data, bool end, Deadline deadline, bool async)
    {
        switch (EndOutput)
        {
            case SerialTerminationMethod.LastBit:
            {
                // The highest data bit marks END: clear in every byte, set in the last one of a message with END.
                byte mask = (byte)(1 << (_settings.DataBits - 1));
                var copy = data.ToArray();
                for (int i = 0; i < copy.Length; i++) copy[i] &= (byte)~mask;
                if (end && copy.Length > 0) copy[^1] |= mask;
                await PortWriteAsync(copy, deadline, async).ConfigureAwait(false);
                break;
            }
            case SerialTerminationMethod.TerminationCharacter when end:
            {
                var copy = new byte[data.Length + 1];
                data.Span.CopyTo(copy);
                copy[^1] = TerminationCharacter;
                await PortWriteAsync(copy, deadline, async).ConfigureAwait(false);
                break;
            }
            case SerialTerminationMethod.Break when end:
                await PortWriteAsync(data, deadline, async).ConfigureAwait(false);
                await DrainAsync(deadline, async).ConfigureAwait(false);
                await SendBreakCoreAsync(BreakLength, deadline.CancellationToken, async).ConfigureAwait(false);
                break;
            default:
                await PortWriteAsync(data, deadline, async).ConfigureAwait(false);
                break;
        }
    }

    private protected override async ValueTask<FillResult> FillCoreAsync(Memory<byte> target, Deadline deadline, bool async)
    {
        bool end = false;
        bool fromPending = _pendingCount > 0;
        int n;
        if (fromPending)
        {
            n = Math.Min(target.Length, _pendingCount);
            _pending.AsSpan(_pendingOffset, n).CopyTo(target.Span);
        }
        else if (async)
        {
            n = await Port.ReadAsync(target, deadline.RemainingMilliseconds, deadline.CancellationToken).ConfigureAwait(false);
        }
        else
        {
            n = Port.Read(target.Span, deadline.RemainingMilliseconds);
        }

        int consumed = n;
        if (EndInput == SerialTerminationMethod.LastBit)
        {
            byte mask = (byte)(1 << (_settings.DataBits - 1));
            var span = target.Span;
            for (int i = 0; i < n; i++)
            {
                if ((span[i] & mask) != 0)
                {
                    end = true;
                    consumed = i + 1;
                    break;
                }
            }
        }

        if (fromPending)
        {
            _pendingOffset += consumed;
            _pendingCount -= consumed;
        }
        else if (consumed < n)
        {
            _pending = target.Span[consumed..n].ToArray();
            _pendingOffset = 0;
            _pendingCount = n - consumed;
        }
        return new FillResult(consumed, end);
    }

    private void DiscardPending()
    {
        _pendingCount = 0;
        _pendingOffset = 0;
    }

    private protected override ValueTask<byte> ReadStatusByteCoreAsync(Deadline deadline, bool async) => QueryStatusByteWithStringsAsync(deadline, async);

    /// <summary>
    /// viClear on a serial INSTR: discards the output, sends a break of <see cref="BreakLength"/> (or "*CLS\n" with
    /// IEEE 488.2 strings), then discards the input.
    /// </summary>
    private protected override async ValueTask ClearCoreAsync(Deadline deadline, bool async)
    {
        DiscardPending();
        Port.DiscardOutput();
        if (IOProtocol == IOProtocol.Ieee4882Strings)
            await PortWriteAsync("*CLS\n"u8.ToArray(), deadline, async).ConfigureAwait(false);
        else
            await SendBreakCoreAsync(BreakLength, deadline.CancellationToken, async).ConfigureAwait(false);
        Port.DiscardInput();
    }

    // Written as is: the command already ends with its terminator.
    private protected override ValueTask TriggerCoreAsync(Deadline deadline, bool async) => PortWriteAsync("*TRG\n"u8.ToArray(), deadline, async);

    private protected override void FlushTransportInput()
    {
        DiscardPending();
        Port.DiscardInput();
    }

    private protected override ValueTask FlushTransportOutputAsync(Deadline deadline, bool async) => DrainAsync(deadline, async);

    private protected override ValueTask DiscardTransportOutputAsync(bool async)
    {
        Port.DiscardOutput();
        return default;
    }

    private protected override bool SupportsIOProtocol(IOProtocol protocol) => protocol is IOProtocol.Normal or IOProtocol.Ieee4882Strings;

    /// <summary>Sends a break of <see cref="BreakLength"/> milliseconds.</summary>
    public void SendBreak() => SendBreak(BreakLength);

    /// <summary>Sends a break of <paramref name="milliseconds"/> (at least 1).</summary>
    public void SendBreak(int milliseconds) => AsyncIO.Complete(SendBreakAsync(milliseconds, CancellationToken.None, async: false));

    /// <summary>Sends a break of <see cref="BreakLength"/> milliseconds without blocking.</summary>
    public Task SendBreakAsync(CancellationToken cancellationToken = default) => SendBreakAsync(BreakLength, cancellationToken);

    /// <summary>
    /// Sends a break of <paramref name="milliseconds"/> (at least 1) without blocking. Cancelling the token ends the
    /// break early and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public Task SendBreakAsync(int milliseconds, CancellationToken cancellationToken = default) =>
        SendBreakAsync(milliseconds, cancellationToken, async: true).AsTask();

    private async ValueTask SendBreakAsync(int milliseconds, CancellationToken cancellationToken, bool async) =>
        await ExecuteAsync(milliseconds, static async (s, milliseconds, ct, async) =>
        {
            await ((SerialSession)s).SendBreakCoreAsync(milliseconds, ct, async).ConfigureAwait(false);
            return true;
        }, cancellationToken, async).ConfigureAwait(false);

    private async ValueTask SendBreakCoreAsync(int milliseconds, CancellationToken cancellationToken, bool async)
    {
        var port = Port;
        port.Break = true;
        try
        {
            if (async) await Task.Delay(Math.Max(1, milliseconds), cancellationToken).ConfigureAwait(false);
            else Thread.Sleep(Math.Max(1, milliseconds));
        }
        finally
        {
            try
            {
                port.Break = false;
            }
            catch (ObjectDisposedException)
            {
                // Closed meanwhile: closing ended the break.
            }
        }
    }

    private protected override void RegisterAttributes(AttributeTable table)
    {
        base.RegisterAttributes(table);
        table.Add(VisaAttribute.SerialBaudRate, () => BaudRate, v => BaudRate = v);
        table.Add(VisaAttribute.SerialDataBits, () => DataBits, v => DataBits = v);
        table.Add(VisaAttribute.SerialParity, () => Parity, v => Parity = v);
        table.Add(VisaAttribute.SerialStopBits, () => StopBits, v => StopBits = v);
        table.Add(VisaAttribute.SerialFlowControl, () => FlowControl, v => FlowControl = v);
        table.Add(VisaAttribute.SerialEndIn, () => EndInput, v => EndInput = v);
        table.Add(VisaAttribute.SerialEndOut, () => EndOutput, v => EndOutput = v);
        table.Add(VisaAttribute.SerialXOnCharacter, () => XOnCharacter, v => XOnCharacter = v);
        table.Add(VisaAttribute.SerialXOffCharacter, () => XOffCharacter, v => XOffCharacter = v);
        table.Add(VisaAttribute.SerialReplaceCharacter, () => ReplacementCharacter, v => ReplacementCharacter = v);
        table.Add(VisaAttribute.SerialDiscardNull, () => DiscardNull, v => DiscardNull = v);
        table.Add(VisaAttribute.SerialAvailableNumber, () => BytesAvailable);
        table.Add(VisaAttribute.SerialCtsState, () => CtsState);
        table.Add(VisaAttribute.SerialDsrState, () => DsrState);
        table.Add(VisaAttribute.SerialDcdState, () => DcdState);
        table.Add(VisaAttribute.SerialRiState, () => RiState);
        table.Add(VisaAttribute.SerialDtrState, () => DtrState, v => DtrState = v);
        table.Add(VisaAttribute.SerialRtsState, () => RtsState, v => RtsState = v);
        table.Add(VisaAttribute.SerialBreakState, () => BreakState, v => BreakState = v);
        table.Add(VisaAttribute.SerialBreakLength, () => BreakLength, v => BreakLength = v);
        table.Add(VisaAttribute.SerialAllowTransmit, () => AllowTransmit, v => AllowTransmit = v);
    }
}

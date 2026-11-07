namespace VisaCs.Internal;

/// <summary>A chunk of transport input: <paramref name="Count"/> bytes, with END on the last one when <paramref name="End"/>.</summary>
internal readonly record struct FillResult(int Count, bool End);

/// <summary>
/// Fetches the next chunk of input from a transport into <paramref name="target"/>. The result's End is true when
/// the END indicator is attached to the last byte returned (or to an empty chunk). With <paramref name="async"/>
/// false the call must complete synchronously.
/// </summary>
internal delegate ValueTask<FillResult> FillCallback(Memory<byte> target, Deadline deadline, bool async);

/// <summary>
/// Buffers transport input and implements the VISA read termination rules: a read completes on END,
/// on the termination character (when enabled), or when the caller's buffer is full. Data that arrives
/// after the termination point is kept for the next read.
/// </summary>
internal sealed class InputBuffer
{
    private readonly byte[] _data;
    private int _start;
    private int _count;
    private bool _end;

    public InputBuffer(int capacity = 64 * 1024) => _data = new byte[capacity];

    public void Clear()
    {
        _start = 0;
        _count = 0;
        _end = false;
    }

    /// <summary>
    /// Reads into <paramref name="destination"/>, calling <paramref name="fill"/> for more input as needed.
    /// <paramref name="termChar"/> is -1 when termination is disabled; <paramref name="termCharIsEnd"/> reports
    /// a termination character match as END (serial END_IN = TERMCHAR).
    /// </summary>
    public async ValueTask<ReadResult> ReadAsync(Memory<byte> destination, int termChar, bool termCharIsEnd,
        FillCallback fill, Deadline deadline, bool async)
    {
        int read = 0;
        if (destination.IsEmpty) return new ReadResult(0, ReadStatus.MaximumCountReached);

        while (true)
        {
            if (_count == 0 && !_end && destination.Length - read >= _data.Length)
            {
                // Large read with nothing buffered: let the transport write straight into the caller's buffer.
                var target = destination.Slice(read, _data.Length);
                var (n, end) = await FillAsync(fill, target, deadline, async, read).ConfigureAwait(false);
                var got = target.Span[..n];
                if (termChar >= 0)
                {
                    int index = got.IndexOf((byte)termChar);
                    if (index >= 0)
                    {
                        // Keep anything after the termination character for the next read.
                        var tail = got[(index + 1)..];
                        tail.CopyTo(_data);
                        _start = 0;
                        _count = tail.Length;
                        _end = end;
                        read += index + 1;
                        if (tail.IsEmpty && end)
                        {
                            _end = false;
                            return new ReadResult(read, ReadStatus.EndReceived);
                        }
                        return new ReadResult(read, termCharIsEnd ? ReadStatus.EndReceived : ReadStatus.TerminationCharacterEncountered);
                    }
                }
                read += n;
                if (end) return new ReadResult(read, ReadStatus.EndReceived);
                if (read == destination.Length) return new ReadResult(read, ReadStatus.MaximumCountReached);
                continue;
            }

            if (_count == 0)
            {
                if (_end)
                {
                    // An END with no data (e.g. an empty HiSLIP DataEND).
                    _end = false;
                    return new ReadResult(read, ReadStatus.EndReceived);
                }
                _start = 0;
                (_count, _end) = await FillAsync(fill, _data, deadline, async, read).ConfigureAwait(false);
                continue;
            }

            int take = Math.Min(destination.Length - read, _count);
            var source = _data.AsSpan(_start, take);
            bool termFound = false;
            if (termChar >= 0)
            {
                int index = source.IndexOf((byte)termChar);
                if (index >= 0)
                {
                    take = index + 1;
                    source = source[..take];
                    termFound = true;
                }
            }

            source.CopyTo(destination.Span[read..]);
            read += take;
            _start += take;
            _count -= take;

            if (_count == 0 && _end)
            {
                _end = false;
                return new ReadResult(read, ReadStatus.EndReceived);
            }
            if (termFound)
                return new ReadResult(read, termCharIsEnd ? ReadStatus.EndReceived : ReadStatus.TerminationCharacterEncountered);
            if (read == destination.Length)
                return new ReadResult(read, ReadStatus.MaximumCountReached);
        }
    }

    private static async ValueTask<FillResult> FillAsync(FillCallback fill, Memory<byte> target, Deadline deadline, bool async, int alreadyRead)
    {
        try
        {
            return await fill(target, deadline, async).ConfigureAwait(false);
        }
        catch (VisaTimeoutException ex) when (alreadyRead > 0)
        {
            throw new VisaTimeoutException(ex.Message, ex) { BytesTransferred = alreadyRead };
        }
    }
}

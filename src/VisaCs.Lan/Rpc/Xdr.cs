using System.Buffers.Binary;
using System.Text;
using VisaCs.Internal;

namespace VisaCs.Lan.Rpc;

/// <summary>XDR (RFC 4506) encoder writing big-endian data into a growable buffer.</summary>
public sealed class XdrWriter
{
    private byte[] _buffer;
    private int _length;

    /// <summary>Creates a writer with an initial buffer of <paramref name="capacity"/> bytes.</summary>
    public XdrWriter(int capacity = 256) => _buffer = new byte[Math.Max(capacity, 16)];

    /// <summary>Number of bytes written.</summary>
    public int Length => _length;

    /// <summary>The bytes written so far. Valid until the next write or <see cref="Reset"/>.</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    /// <summary>The bytes written so far. Valid until the next write or <see cref="Reset"/>.</summary>
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _length);

    internal byte[] Buffer => _buffer;

    /// <summary>Discards everything written; the buffer is reused.</summary>
    public void Reset() => _length = 0;

    private void Ensure(int additional)
    {
        if (_length + additional <= _buffer.Length) return;
        int size = Math.Max(_buffer.Length * 2, _length + additional);
        Array.Resize(ref _buffer, size);
    }

    /// <summary>Writes an unsigned 32-bit integer.</summary>
    public void WriteUInt32(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    /// <summary>Writes a signed 32-bit integer.</summary>
    public void WriteInt32(int value) => WriteUInt32(unchecked((uint)value));

    /// <summary>Writes a boolean (1 or 0).</summary>
    public void WriteBool(bool value) => WriteUInt32(value ? 1u : 0u);

    /// <summary>Writes an unsigned 64-bit integer (hyper).</summary>
    public void WriteUInt64(ulong value)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64BigEndian(_buffer.AsSpan(_length), value);
        _length += 8;
    }

    /// <summary>Variable-length opaque data: length prefix, data, zero padding to a multiple of 4.</summary>
    public void WriteOpaque(ReadOnlySpan<byte> data)
    {
        WriteUInt32((uint)data.Length);
        WriteFixedOpaque(data);
    }

    /// <summary>Fixed-length opaque data (no length prefix), zero padded to a multiple of 4.</summary>
    public void WriteFixedOpaque(ReadOnlySpan<byte> data)
    {
        int padded = (data.Length + 3) & ~3;
        Ensure(padded);
        data.CopyTo(_buffer.AsSpan(_length));
        _buffer.AsSpan(_length + data.Length, padded - data.Length).Clear();
        _length += padded;
    }

    /// <summary>Writes an ASCII string as variable-length opaque data.</summary>
    public void WriteString(string value) => WriteOpaque(Encoding.ASCII.GetBytes(value));

    /// <summary>Overwrites a 32-bit value previously written at <paramref name="offset"/>.</summary>
    internal void PatchUInt32(int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(offset), value);
}

/// <summary>
/// XDR (RFC 4506) decoder over a byte array segment. Reading past the end throws <see cref="VisaException"/>
/// (VI_ERROR_IO).
/// </summary>
public sealed class XdrReader
{
    private readonly byte[] _buffer;
    private readonly int _end;
    private int _position;

    /// <summary>Creates a reader over <paramref name="count"/> bytes of <paramref name="buffer"/> from <paramref name="offset"/>.</summary>
    public XdrReader(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, buffer.Length - offset);
        _buffer = buffer;
        _position = offset;
        _end = offset + count;
    }

    /// <summary>Number of bytes not read yet.</summary>
    public int Remaining => _end - _position;

    private void Require(long count)
    {
        if (count < 0 || count > _end - _position)
            throw LanErrors.IO("Malformed XDR data: unexpected end of message.");
    }

    /// <summary>Reads an unsigned 32-bit integer.</summary>
    public uint ReadUInt32()
    {
        Require(4);
        uint v = BinaryPrimitives.ReadUInt32BigEndian(_buffer.AsSpan(_position));
        _position += 4;
        return v;
    }

    /// <summary>Reads a signed 32-bit integer.</summary>
    public int ReadInt32() => unchecked((int)ReadUInt32());

    /// <summary>Reads a boolean (any non-zero value is true).</summary>
    public bool ReadBool() => ReadUInt32() != 0;

    /// <summary>Reads an unsigned 64-bit integer (hyper).</summary>
    public ulong ReadUInt64()
    {
        Require(8);
        ulong v = BinaryPrimitives.ReadUInt64BigEndian(_buffer.AsSpan(_position));
        _position += 8;
        return v;
    }

    /// <summary>Reads variable-length opaque data as a slice of the reader's buffer (no copy).</summary>
    public ReadOnlySpan<byte> ReadOpaqueSpan() => ReadOpaqueMemory().Span;

    /// <summary>Reads fixed-length opaque data (and its padding) as a slice of the reader's buffer.</summary>
    public ReadOnlySpan<byte> ReadFixedOpaqueSpan(int length) => ReadFixedOpaqueMemory(length).Span;

    private ReadOnlyMemory<byte> ReadFixedOpaqueMemory(int length)
    {
        long padded = ((long)length + 3) & ~3L;
        Require(padded);
        var memory = _buffer.AsMemory(_position, length);
        _position += (int)padded;
        return memory;
    }

    /// <summary>Reads variable-length opaque data into a new array.</summary>
    public byte[] ReadOpaque() => ReadOpaqueSpan().ToArray();

    /// <summary>Reads variable-length opaque data as a slice of the reader's buffer (no copy).</summary>
    public ReadOnlyMemory<byte> ReadOpaqueMemory()
    {
        uint length = ReadUInt32();
        if (length > int.MaxValue) throw LanErrors.IO("Malformed XDR data: invalid length.");
        return ReadFixedOpaqueMemory((int)length);
    }

    /// <summary>Reads an ASCII string.</summary>
    public string ReadString() => Encoding.ASCII.GetString(ReadOpaqueSpan());

    /// <summary>Skips <paramref name="bytes"/> bytes.</summary>
    public void Skip(int bytes)
    {
        Require(bytes);
        _position += bytes;
    }
}

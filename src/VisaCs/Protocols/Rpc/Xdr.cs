using System.Buffers.Binary;
using System.Text;

namespace VisaCs.Protocols.Rpc;

/// <summary>XDR (RFC 4506) encoder writing into a growable buffer.</summary>
internal sealed class XdrWriter
{
    private byte[] _buffer;
    private int _length;

    public XdrWriter(int capacity = 256) => _buffer = new byte[capacity];

    public int Length => _length;

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    public byte[] Buffer => _buffer;

    public void Reset() => _length = 0;

    private void Ensure(int additional)
    {
        if (_length + additional <= _buffer.Length) return;
        int size = Math.Max(_buffer.Length * 2, _length + additional);
        Array.Resize(ref _buffer, size);
    }

    public void WriteUInt32(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    public void WriteInt32(int value) => WriteUInt32(unchecked((uint)value));

    public void WriteBool(bool value) => WriteUInt32(value ? 1u : 0u);

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

    public void WriteString(string value) => WriteOpaque(Encoding.ASCII.GetBytes(value));

    /// <summary>Overwrites a 32-bit value previously written at <paramref name="offset"/>.</summary>
    public void PatchUInt32(int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(offset), value);
}

/// <summary>XDR decoder over a byte array segment.</summary>
internal sealed class XdrReader
{
    private readonly byte[] _buffer;
    private readonly int _end;
    private int _position;

    public XdrReader(byte[] buffer, int offset, int count)
    {
        _buffer = buffer;
        _position = offset;
        _end = offset + count;
    }

    public int Remaining => _end - _position;

    private void Require(long count)
    {
        if (count < 0 || count > _end - _position)
            throw new VisaException(VisaStatus.ErrorIO, "Malformed XDR data: unexpected end of message.");
    }

    public uint ReadUInt32()
    {
        Require(4);
        uint v = BinaryPrimitives.ReadUInt32BigEndian(_buffer.AsSpan(_position));
        _position += 4;
        return v;
    }

    public int ReadInt32() => unchecked((int)ReadUInt32());

    public bool ReadBool() => ReadUInt32() != 0;

    public ulong ReadUInt64()
    {
        Require(8);
        ulong v = BinaryPrimitives.ReadUInt64BigEndian(_buffer.AsSpan(_position));
        _position += 8;
        return v;
    }

    public ReadOnlySpan<byte> ReadOpaqueSpan()
    {
        uint length = ReadUInt32();
        if (length > int.MaxValue) throw new VisaException(VisaStatus.ErrorIO, "Malformed XDR data: invalid length.");
        return ReadFixedOpaqueSpan((int)length);
    }

    public ReadOnlySpan<byte> ReadFixedOpaqueSpan(int length) => ReadFixedOpaqueMemory(length).Span;

    private ReadOnlyMemory<byte> ReadFixedOpaqueMemory(int length)
    {
        long padded = ((long)length + 3) & ~3L;
        Require(padded);
        var memory = _buffer.AsMemory(_position, length);
        _position += (int)padded;
        return memory;
    }

    public byte[] ReadOpaque() => ReadOpaqueSpan().ToArray();

    /// <summary>Variable-length opaque data as a slice of the reader's buffer (no copy).</summary>
    public ReadOnlyMemory<byte> ReadOpaqueMemory()
    {
        uint length = ReadUInt32();
        if (length > int.MaxValue) throw new VisaException(VisaStatus.ErrorIO, "Malformed XDR data: invalid length.");
        return ReadFixedOpaqueMemory((int)length);
    }

    public string ReadString() => Encoding.ASCII.GetString(ReadOpaqueSpan());

    public void Skip(int bytes)
    {
        Require(bytes);
        _position += bytes;
    }
}

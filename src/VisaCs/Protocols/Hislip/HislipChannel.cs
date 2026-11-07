using System.Buffers;
using System.Net.Sockets;
using VisaCs.Internal;

namespace VisaCs.Protocols.Hislip;

/// <summary>
/// One HiSLIP TCP connection (synchronous or asynchronous channel), either plain or TLS-encrypted.
/// HiSLIP 2.0 switches a channel between the two at runtime (StartTLS / EndTLS). Every operation takes a
/// <c>bool async</c>: false blocks the calling thread, true holds no thread while waiting.
/// </summary>
/// <remarks>
/// One sender and one receiver may use a channel at the same time. A send that fails or times out after part of a
/// message went out leaves the peer inside that message; the owner must then close the connection.
/// </remarks>
internal abstract class HislipChannel : IDisposable
{
    protected HislipChannel(Socket socket) => Socket = socket;

    public Socket Socket { get; }

    public abstract bool IsEncrypted { get; }

    /// <summary>
    /// Sends all of <paramref name="data"/>. Use <see cref="NetUtil.MayHaveSentPart"/> on a failure to tell whether the
    /// peer can be left inside a message (a TLS channel also reports <see cref="TlsHislipChannel.IsBroken"/>).
    /// </summary>
    public abstract ValueTask SendAsync(ReadOnlyMemory<byte> data, Deadline deadline, bool async);

    /// <summary>Receives at least one byte, or throws on timeout or when the connection ends.</summary>
    public abstract ValueTask<int> ReceiveSomeAsync(Memory<byte> buffer, Deadline deadline, bool async);

    public async ValueTask ReceiveExactAsync(Memory<byte> buffer, Deadline deadline, bool async)
    {
        while (!buffer.IsEmpty)
        {
            int n = await ReceiveSomeAsync(buffer, deadline, async).ConfigureAwait(false);
            buffer = buffer[n..];
        }
    }

    public async ValueTask SkipAsync(long count, Deadline deadline, bool async)
    {
        if (count <= 0) return;
        byte[] scratch = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (count > 0)
            {
                int n = await ReceiveSomeAsync(scratch.AsMemory(0, (int)Math.Min(scratch.Length, count)), deadline, async).ConfigureAwait(false);
                count -= n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>Closes the underlying socket (and, for TLS, stops the reader and releases the TLS session).</summary>
    public abstract void Dispose();
}

/// <summary>Unencrypted channel on a non-blocking socket.</summary>
internal sealed class PlainHislipChannel : HislipChannel
{
    public PlainHislipChannel(Socket socket) : base(socket)
    {
        socket.Blocking = false;
    }

    public override bool IsEncrypted => false;

    public override ValueTask SendAsync(ReadOnlyMemory<byte> data, Deadline deadline, bool async) =>
        NetUtil.SendAllAsync(Socket, data, deadline, async);

    public override ValueTask<int> ReceiveSomeAsync(Memory<byte> buffer, Deadline deadline, bool async) =>
        NetUtil.ReceiveSomeAsync(Socket, buffer, deadline, async);

    public override void Dispose() => NetUtil.SafeClose(Socket);
}

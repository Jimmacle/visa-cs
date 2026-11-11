using System.Net;
using System.Net.Sockets;

namespace VisaCs.Internal;

/// <summary>
/// Socket helpers. Sockets are non-blocking: the synchronous paths wait with <see cref="Socket.Poll(int, SelectMode)"/>
/// and the asynchronous ones with <see cref="Socket.ReceiveAsync(Memory{byte}, SocketFlags, CancellationToken)"/>
/// and friends, so a timeout never leaves the socket in an undefined state (which blocking sockets with
/// ReceiveTimeout do on Windows) and a canceled receive never consumes data.
/// </summary>
/// <remarks>
/// The exception is an encrypted HiSLIP channel: <see cref="NetworkStream"/> (under <c>SslStream</c>) needs a blocking
/// socket, so the channel switches the socket to blocking mode for the TLS session and back when TLS ends. Its I/O
/// still goes through asynchronous calls bounded by <see cref="AwaitBoundedAsync"/>, never through socket timeouts.
/// </remarks>
internal static class NetUtil
{
    public static async ValueTask<IPAddress[]> ResolveAsync(string host, Deadline deadline, bool async)
    {
        var trimmed = host.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) trimmed = trimmed[1..^1];
        if (IPAddress.TryParse(trimmed, out var ip)) return [ip];
        try
        {
            IPAddress[] addresses;
            if (!async && deadline.IsInfinite && !deadline.CanBeCanceled)
            {
                addresses = Dns.GetHostAddresses(trimmed);
            }
            else
            {
                using var cts = deadline.CreateTokenSource();
                var lookup = Dns.GetHostAddressesAsync(trimmed, cts?.Token ?? default);
                await AwaitBoundedAsync(lookup, deadline, cts, async).ConfigureAwait(false);
                addresses = lookup.Result;
            }
            // Prefer IPv4: many instruments only listen on IPv4, and VXI-11 interrupt channels need IPv4.
            return addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToArray();
        }
        catch (OperationCanceledException ex)
        {
            throw Timeout($"Resolving '{host}' timed out.", ex);
        }
        catch (SocketException ex)
        {
            throw NotFound($"Cannot resolve host '{host}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Connects a TCP socket and returns it in non-blocking mode. Tries the resolved addresses in order (IPv4 first);
    /// the deadline covers name resolution and all attempts, so an address that times out ends the attempt.
    /// </summary>
    public static async ValueTask<Socket> ConnectAsync(string host, int port, Deadline deadline, bool async, bool noDelay = true)
    {
        Exception? last = null;
        foreach (var address in await ResolveAsync(host, deadline, async).ConfigureAwait(false))
        {
            try
            {
                return await ConnectAsync(new IPEndPoint(address, port), deadline, async, noDelay, host).ConfigureAwait(false);
            }
            catch (VisaException ex) when (ex is not VisaTimeoutException && ex.InnerException is SocketException se)
            {
                last = se;
            }
        }
        throw NotFound($"Unable to connect to {host}:{port}: {last?.Message}", last);
    }

    /// <summary>Connects a TCP socket to one address and returns it in non-blocking mode.</summary>
    public static async ValueTask<Socket> ConnectAsync(IPEndPoint endPoint, Deadline deadline, bool async, bool noDelay = true,
        string? hostForMessages = null)
    {
        string target = hostForMessages is null ? endPoint.ToString() : $"{hostForMessages}:{endPoint.Port}";
        var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            using var cts = deadline.CreateTokenSource();
            await AwaitBoundedAsync(socket.ConnectAsync(endPoint, cts?.Token ?? default).AsTask(), deadline, cts, async).ConfigureAwait(false);
            socket.Blocking = false;
            socket.NoDelay = noDelay;
            return socket;
        }
        catch (OperationCanceledException ex)
        {
            socket.Dispose();
            throw Timeout($"Connection to {target} timed out.", ex);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw NotFound($"Unable to connect to {target}: {ex.Message}", ex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Waits for an operation that was started with a token from <paramref name="cts"/> (which cancels it when
    /// <paramref name="deadline"/> passes). Asynchronous callers await it. Synchronous callers block until it completes
    /// or the deadline passes; they have no other way to bound connects, TLS handshakes and shutdowns. Throws
    /// <see cref="OperationCanceledException"/> when the deadline passed first, otherwise the operation's own exception.
    /// </summary>
    public static async ValueTask AwaitBoundedAsync(Task task, Deadline deadline, CancellationTokenSource? cts, bool async)
    {
        if (async)
        {
            await task.ConfigureAwait(false);
            return;
        }
        bool completed;
        try
        {
            completed = task.Wait(deadline.RemainingMilliseconds);
        }
        catch (AggregateException)
        {
            completed = true; // rethrown unwrapped below
        }
        if (!completed)
        {
            cts?.Cancel();
            throw new OperationCanceledException(deadline.CancellationToken);
        }
        task.GetAwaiter().GetResult();
    }

    private static bool IsTransient(SocketError error) =>
        error is SocketError.WouldBlock or SocketError.IOPending or SocketError.Interrupted;

    /// <summary>Waits until the socket is readable/writable. Returns false when the deadline passes first.</summary>
    private static bool WaitFor(Socket socket, SelectMode mode, Deadline deadline)
    {
        while (true)
        {
            int remaining = deadline.NextWaitMilliseconds;
            if (remaining == 0) return false;
            // Poll takes microseconds as an int; slice long waits.
            int slice = remaining < 0 || remaining > 1_000_000 ? 1_000_000 : remaining;
            if (socket.Poll(slice * 1000, mode)) return true;
            if (deadline.HasExpired) return false;
        }
    }

    private static int ReceiveSome(Socket socket, Span<byte> buffer, Deadline deadline)
    {
        while (true)
        {
            int n = socket.Receive(buffer, SocketFlags.None, out var error);
            if (error == SocketError.Success)
            {
                if (n == 0) throw ConnectionLost("The remote host closed the connection.");
                return n;
            }
            if (!IsTransient(error))
                throw ConnectionLost($"Socket receive failed: {error}.");
            if (!WaitFor(socket, SelectMode.SelectRead, deadline)) throw Timeout();
        }
    }

    /// <summary>Receives at least one byte. Throws on timeout or when the peer closed the connection.</summary>
    public static async ValueTask<int> ReceiveSomeAsync(Socket socket, Memory<byte> buffer, Deadline deadline, bool async)
    {
        if (buffer.IsEmpty) return 0;
        if (!async) return ReceiveSome(socket, buffer.Span, deadline);

        // Take what is already there without arming a timer; this also matches the synchronous path when the
        // deadline has passed but data is waiting.
        int n = socket.Receive(buffer.Span, SocketFlags.None, out var error);
        if (IsTransient(error))
        {
            using var cts = deadline.CreateTokenSource();
            try
            {
                n = await socket.ReceiveAsync(buffer, SocketFlags.None, cts?.Token ?? default).ConfigureAwait(false);
                error = SocketError.Success;
            }
            catch (OperationCanceledException ex)
            {
                throw Timeout(null, ex);
            }
            catch (SocketException ex)
            {
                throw ConnectionLost($"Socket receive failed: {ex.SocketErrorCode}.", ex);
            }
        }
        if (error != SocketError.Success)
            throw ConnectionLost($"Socket receive failed: {error}.");
        if (n == 0) throw ConnectionLost("The remote host closed the connection.");
        return n;
    }

    public static async ValueTask ReceiveExactAsync(Socket socket, Memory<byte> buffer, Deadline deadline, bool async)
    {
        while (!buffer.IsEmpty)
        {
            int n = await ReceiveSomeAsync(socket, buffer, deadline, async).ConfigureAwait(false);
            buffer = buffer[n..];
        }
    }

    /// <summary>Reads and discards <paramref name="count"/> bytes.</summary>
    public static async ValueTask SkipAsync(Socket socket, long count, Deadline deadline, bool async)
    {
        if (count <= 0) return;
        var scratch = new byte[(int)Math.Min(count, 4096)];
        while (count > 0)
        {
            int n = await ReceiveSomeAsync(socket, scratch.AsMemory(0, (int)Math.Min(scratch.Length, count)), deadline, async).ConfigureAwait(false);
            count -= n;
        }
    }

    /// <summary>
    /// Sends all of <paramref name="data"/>. A timeout throws <see cref="SendTimeoutException"/>: framed protocols must
    /// close the connection unless <see cref="SendTimeoutException.NothingSent"/>, because the peer is left inside a
    /// message.
    /// </summary>
    public static async ValueTask SendAllAsync(Socket socket, ReadOnlyMemory<byte> data, Deadline deadline, bool async)
    {
        int sent = 0;
        while (sent < data.Length)
        {
            int n = socket.Send(data.Span[sent..], SocketFlags.None, out var error);
            if (error == SocketError.Success)
            {
                sent += n;
                continue;
            }
            if (!IsTransient(error))
                throw ConnectionLost($"Socket send failed: {error}.");
            if (!async)
            {
                if (!WaitFor(socket, SelectMode.SelectWrite, deadline))
                    throw new SendTimeoutException(SendTimeoutMessage(sent), null, nothingSent: sent == 0) { BytesTransferred = sent };
                continue;
            }
            using var cts = deadline.CreateTokenSource();
            try
            {
                sent += await socket.SendAsync(data[sent..], SocketFlags.None, cts?.Token ?? default).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                // A cancelled send may have handed part of the data to the network already; how much is not reported.
                throw new SendTimeoutException(SendTimeoutMessage(sent), ex, nothingSent: false) { BytesTransferred = sent };
            }
            catch (SocketException ex)
            {
                throw ConnectionLost($"Socket send failed: {ex.SocketErrorCode}.", ex);
            }
        }
    }

    private static string? SendTimeoutMessage(int sent) => sent == 0 ? null : $"The send timed out after {sent} bytes.";

    /// <summary>
    /// True when a failed send may have left the peer inside a message: anything but a timeout that sent nothing
    /// (a <see cref="VisaTimeoutException"/> other than <see cref="SendTimeoutException"/> comes from waiting for a gate
    /// or a deadline that had passed before anything was sent).
    /// </summary>
    public static bool MayHaveSentPart(Exception ex) => ex is not VisaTimeoutException || ex is SendTimeoutException { NothingSent: false };

    /// <summary>Discards everything currently readable without blocking.</summary>
    public static void Drain(Socket socket)
    {
        Span<byte> scratch = stackalloc byte[4096];
        while (socket.Available > 0)
        {
            int n = socket.Receive(scratch, SocketFlags.None, out var error);
            if (error != SocketError.Success || n <= 0) break;
        }
    }

    public static void SafeClose(Socket? socket)
    {
        if (socket is null) return;
        try { socket.Shutdown(SocketShutdown.Both); } catch { /* ignore */ }
        try { socket.Dispose(); } catch { /* ignore */ }
    }

    /// <summary>The peer's IP address as text (IPv4-mapped addresses as IPv4), for VI_ATTR_TCPIP_ADDR.</summary>
    public static string RemoteAddressText(Socket socket) => Normalize(((IPEndPoint)socket.RemoteEndPoint!).Address).ToString();

    public static bool GetKeepAlive(Socket socket) =>
        (int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)! != 0;

    public static void SetKeepAlive(Socket socket, bool value) =>
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, value);

    /// <summary>Formats an address for use in a resource string (IPv6 is bracketed).</summary>
    public static string FormatHost(string host) =>
        host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;

    public static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    // The exceptions this class creates (besides SendTimeoutException), in one place.
    private static VisaTimeoutException Timeout(string? message = null, Exception? innerException = null) => new(message, innerException);

    private static VisaException ConnectionLost(string message, Exception? innerException = null) =>
        new(VisaStatus.ErrorConnectionLost, message, innerException);

    private static VisaException NotFound(string message, Exception? innerException = null) =>
        new(VisaStatus.ErrorResourceNotFound, message, innerException);
}

/// <summary>A send that timed out (VI_ERROR_TMO). <see cref="NothingSent"/> tells whether the peer can have received part of the data.</summary>
internal sealed class SendTimeoutException(string? message, Exception? innerException, bool nothingSent)
    : VisaTimeoutException(message, innerException)
{
    /// <summary>True when none of the data reached the network, so the peer is not inside a message.</summary>
    public bool NothingSent { get; } = nothingSent;
}

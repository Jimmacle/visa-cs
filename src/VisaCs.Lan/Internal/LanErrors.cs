using System.Net.Sockets;
using VisaCs.Lan;
using VisaCs.Lan.Rpc;

namespace VisaCs.Internal;

/// <summary>
/// Every exception the LAN protocol code creates, in one place (socket-level errors from <see cref="NetUtil"/> come
/// through <see cref="SharedErrors"/>, which builds them here too). The simulator compiles this file as well, together
/// with the HiSLIP wire code.
/// </summary>
internal static class LanErrors
{
    /// <summary>The operation did not finish in time, or the caller cancelled it.</summary>
    public static LanTimeoutException Timeout(string? message = null, Exception? innerException = null, int bytesTransferred = 0) =>
        new(message, innerException) { BytesTransferred = bytesTransferred };

    /// <summary>The peer broke the protocol: malformed or unexpected data.</summary>
    public static LanException Protocol(string message, Exception? innerException = null) =>
        new(LanError.ProtocolError, message, innerException);

    /// <summary>The server reported an error.</summary>
    public static LanException ServerError(string message) => new(LanError.ServerError, message);

    /// <summary>The connection is closed or broken.</summary>
    public static LanException ConnectionLost(string message, Exception? innerException = null) =>
        new(LanError.ConnectionLost, message, innerException) { NativeError = NativeErrorOf(innerException) };

    /// <summary>
    /// The host cannot be resolved or reached: <see cref="LanError.ConnectionRefused"/> when the socket error says
    /// so, else <see cref="LanError.HostNotFound"/>.
    /// </summary>
    public static LanException HostNotFound(string message, Exception? innerException = null) =>
        new(innerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused } ? LanError.ConnectionRefused : LanError.HostNotFound,
            message, innerException) { NativeError = NativeErrorOf(innerException) };

    /// <summary>The host has no such device, sub-address, service or RPC program.</summary>
    public static LanException DeviceNotFound(string message) => new(LanError.DeviceNotFound, message);

    /// <summary>The peer or protocol version does not support the operation.</summary>
    public static LanException NotSupported(string message) => new(LanError.NotSupported, message);

    /// <summary>The resource is locked by another client.</summary>
    public static LanException Locked(string message) => new(LanError.Locked, message);

    /// <summary>The client holds no lock to release.</summary>
    public static LanException NotLocked(string message) => new(LanError.NotLocked, message);

    /// <summary>The server is busy and refused the request.</summary>
    public static LanException Busy(string message) => new(LanError.Busy, message);

    /// <summary>An OS call failed unexpectedly with <paramref name="errno"/>.</summary>
    public static LanException SystemError(string message, int errno) => new(LanError.SystemError, message) { NativeError = errno };

    /// <summary>
    /// The exception for a VXI-11 error code: <see cref="LanTimeoutException"/> for <see cref="Vxi11Error.IOTimeout"/>
    /// (with the <see cref="Vxi11Exception"/> as its inner exception), otherwise <see cref="Vxi11Exception"/>.
    /// </summary>
    public static Exception Vxi11(Vxi11Error error, string message)
    {
        var vxi11 = new Vxi11Exception(error, message);
        return error == Vxi11Error.IOTimeout ? Timeout(message, vxi11) : vxi11;
    }

    /// <summary>An RPC call was not accepted.</summary>
    public static RpcException Rpc(RpcAcceptStatus status, string message) => new(status, message);

    /// <summary>A secure connection (TLS or authentication) failed or the policy forbids it.</summary>
    public static LanSecurityException Security(LanSecurityFailure failure, string message, Exception? innerException = null) =>
        new(failure, message, innerException);

    /// <summary>True for the exceptions of this library (and the simulator's copy of the LAN code).</summary>
    public static bool IsLanError(Exception ex) => ex is LanException or LanTimeoutException;

    private static int? NativeErrorOf(Exception? ex) => ex is SocketException se ? se.NativeErrorCode : null;
}

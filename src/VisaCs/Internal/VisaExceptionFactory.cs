using System.Net.Sockets;

namespace VisaCs;

/// <summary>
/// Factory helpers for <see cref="VisaException"/> used inside this library. They are static extension members, so
/// call sites read <c>VisaException.From(ex)</c> although the exception type lives in VisaCs.Primitives.
/// </summary>
internal static class VisaExceptionFactory
{
    extension(VisaException)
    {
        /// <summary>The error for an operation the resource does not implement.</summary>
        public static VisaException NotSupported(string operation) =>
            new(VisaStatus.ErrorOperationNotSupported, $"{operation} is not supported by this resource.");

        /// <summary>Converts transport-level exceptions into VISA exceptions.</summary>
        public static VisaException From(Exception ex) => ex switch
        {
            VisaException v => v,
            TimeoutException t => new VisaTimeoutException(t.Message, t),
            SocketException s when s.SocketErrorCode == SocketError.TimedOut => new VisaTimeoutException(s.Message, s),
            SocketException s => new VisaException(VisaStatus.ErrorConnectionLost, s.Message, s),
            ObjectDisposedException o => new VisaException(VisaStatus.ErrorConnectionLost, "The connection was closed.", o),
            OperationCanceledException c => new VisaException(VisaStatus.ErrorAbort, c.Message, c),
            IOException io => new VisaException(VisaStatus.ErrorIO, io.Message, io),
            UnauthorizedAccessException u => new VisaException(VisaStatus.ErrorNoPermission, u.Message, u),
            DllNotFoundException d => new VisaException(VisaStatus.ErrorLibraryNotFound, d.Message, d),
            EntryPointNotFoundException e => new VisaException(VisaStatus.ErrorLibraryNotFound, e.Message, e),
            _ => new VisaException(VisaStatus.ErrorSystemError, ex.Message, ex),
        };
    }
}

# VisaCs.Lan

LAN instrument protocols without a VISA library: VXI-11, HiSLIP 2.0 (incl. TLS and SASL authentication), Teledyne
LeCroy VICP, the ONC RPC (XDR, portmapper) they build on, and discovery through mDNS / DNS-SD and VXI-11
broadcast.

| Type | Purpose |
|---|---|
| `Vxi11Client` | A VXI-11 device link: write/read with END, status byte, clear, trigger, remote/local, locks, `device_docmd`, interrupt channel, abort |
| `HislipClient` | A HiSLIP connection: data with END, status byte, device clear, locks, remote/local, trigger, descriptors, service requests, TLS and SASL |
| `VicpClient` | A VICP connection: blocks with EOI, serial poll, device clear, remote/lockout, in-band service requests |
| `RpcTcpClient`, `RpcTcpServer`, `XdrWriter`, `XdrReader`, `Portmapper` | ONC RPC over TCP (namespace `VisaCs.Lan.Rpc`) |
| `MdnsDiscovery`, `Vxi11Discovery` | Find instruments on the LAN |
| `LanException`, `LanTimeoutException`, `LanSecurityException`, `Vxi11Exception`, `RpcException` | Errors |

## Install

```
dotnet nuget add source https://nuget.pkg.github.com/Jimmacle/index.json --name visa-cs --username <github-user> --password <token with read:packages>
dotnet add package VisaCs.Lan --version 0.1.0
```

## Example

```csharp
using System.Text;
using VisaCs.Lan;

using var hislip = await HislipClient.ConnectAsync("192.168.1.20", HislipClient.DefaultPort, "hislip0", timeoutMilliseconds: 5000);
await hislip.WriteAsync("*IDN?\n"u8.ToArray(), end: true, timeoutMilliseconds: 2000);
var buffer = new byte[4096];
int total = 0;
while (true)
{
    var (count, end) = await hislip.ReadAsync(buffer.AsMemory(total), timeoutMilliseconds: 2000);
    total += count;
    if (end) break;
}
hislip.MarkResponseDelivered();
Console.WriteLine(Encoding.ASCII.GetString(buffer, 0, total));

using var vxi11 = Vxi11Client.Connect("192.168.1.21", "inst0", timeoutMilliseconds: 5000);
var (error, size) = vxi11.Write("*IDN?\n"u8.ToArray(), end: true, timeoutMilliseconds: 2000);
var reply = vxi11.Read(4096, terminationCharacter: (byte)'\n', timeoutMilliseconds: 2000);
if (reply.Error != Vxi11Error.None) throw Vxi11Errors.ToException(reply.Error, "device_read");
```

- Every operation has a synchronous form and an asynchronous one that holds no thread while it waits.
- The package depends on no other VisaCs package; every type is in `VisaCs.Lan` (ONC RPC in `VisaCs.Lan.Rpc`).
- `timeoutMilliseconds` bounds the call: negative (`Timeout.Infinite`) waits without limit, 0 does not wait. A
  timeout throws `LanTimeoutException` (a `TimeoutException`, with `BytesTransferred`), and cancelling the token
  `OperationCanceledException`. Cancelling a VXI-11 call also sends `device_abort`.
- Other failures throw `LanException` (an `IOException`) whose `Error` (`LanError`) names the cause:
  `ConnectionLost`, `HostNotFound`, `ConnectionRefused`, `DeviceNotFound`, `ProtocolError`, `ServerError`,
  `NotSupported`, `InvalidRequest`, `Locked`, `NotLocked`, `Busy`, `Aborted`, `OutOfResources`, `Security`,
  `SystemError`. `NativeError` holds the socket error code when known. Subclasses: `LanSecurityException`
  (HiSLIP TLS and SASL, with `Failure`), `Vxi11Exception` (with the VXI-11 `Code`) and `RpcException` (with the
  RPC `AcceptStatus`).
- VXI-11 calls return the device's error code (`Vxi11Error`) instead of throwing it; `Vxi11Errors.ToException`
  makes it an exception.
- Use one caller at a time for a connection's data I/O.

More in the [TCP/IP guide](https://github.com/Jimmacle/visa-cs/blob/main/docs/tcpip.md#without-visa).

## License

MIT

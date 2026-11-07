# TCP/IP instruments

VisaCs implements four LAN protocols in managed code, so they behave the same on every operating system:

| Resource | Protocol | Session |
|---|---|---|
| `TCPIP::host[::inst0]::INSTR` | VXI-11 | `TcpipVxi11Session` |
| `TCPIP::host::hislip0[,port]::INSTR` | HiSLIP 1.0 and 2.0 | `TcpipHislipSession` |
| `TCPIP::host::port::SOCKET` | raw TCP (SCPI-RAW) | `TcpipSocketSession` |
| `VICP::host::INSTR` | Teledyne LeCroy VICP | `VicpSession` |

All of them expose `Address` (`VI_ATTR_TCPIP_ADDR`), `HostName` (`VI_ATTR_TCPIP_HOSTNAME`) and the port.
Host names are resolved with DNS (including `.local` names if the operating system resolves them); IPv4
and IPv6 both work. When a name has several addresses, IPv4 addresses are tried first, and every connection of a
session (VXI-11 core and abort channels, both HiSLIP channels) goes to the address the first one reached.

`ResourceManagerOptions.ConnectTimeoutMilliseconds` (default 5000) bounds the whole connection setup when a
session opens: for VXI-11 the portmapper query, the core and abort connections and `create_link`; for HiSLIP both
channels, the message-size negotiation and, when the connection is encrypted, both TLS handshakes and SASL
authentication.

## VXI-11

```csharp
using var s = rm.Open<TcpipVxi11Session>("TCPIP::192.168.1.5::inst0::INSTR");
```

- The client asks the portmapper (TCP port 111, `ResourceManagerOptions.Vxi11PortmapperPort`) for the core
  channel, then creates a link to the device name (`inst0` by default).
- Reads honor END, the termination character and the requested count. `MaxReceiveSize` is the instrument's
  limit per `device_write`; larger writes are split.
- `ReadStatusByte`, `Clear`, `AssertTrigger` and `ControlRen` map to `device_readstb`, `device_clear`,
  `device_trigger`, `device_remote`/`device_local`.
- Exclusive locks also call `device_lock`, so other clients see them. VXI-11 has no shared locks: a shared lock
  only excludes other sessions in this process.
- The remaining session timeout is passed to the instrument with each call (`io_timeout`), so the instrument ends
  the call itself; the client waits 2 s longer for the reply. I/O does not wait for a lock that another client
  holds: it fails at once with `VI_ERROR_RSRC_LOCKED`.
- GPIB gateways use device names such as `gpib0,5` (`TCPIP::10.0.0.2::gpib0,5::INSTR`).
- `DoCommand` sends `device_docmd` for gateway-specific commands.

**Service requests** use the VXI-11 interrupt channel: when you enable `EventType.ServiceRequest`, the session
starts a small RPC server on an ephemeral TCP port and asks the instrument to connect back to it
(`create_intr_chan`). The instrument must be able to reach your computer, so allow inbound TCP for the process
in the host firewall. Enabling the event fails if the instrument rejects the request; instruments that connect
only when the first request occurs fail silently instead, so test SRQ once after setting up a new machine.
The interrupt channel needs an IPv4 connection to the instrument.

## HiSLIP

```csharp
using var s = rm.Open<TcpipHislipSession>("TCPIP::192.168.1.5::hislip0::INSTR");
Console.WriteLine($"HiSLIP {s.ProtocolVersion >> 8}.{s.ProtocolVersion & 0xFF}, session {s.SessionId}, vendor {s.ServerVendorId}");
```

- The port defaults to 4880 (`ResourceManagerOptions.HislipPort`); `hislip0,4881` selects another.
- The client offers version 2.0 and works with 1.0 servers.
- `SetOverlapEnabled` requests synchronized or overlapped mode through a device clear; the server decides, and
  `OverlapEnabled` tells the result.
- `SetMaximumMessageKB` announces the largest message the client wants to receive (`MaximumMessageKB`, default
  1024 KiB); `ServerMaximumMessageSize` is the server's limit, and writes are split to fit it.
- Data, END and triggers travel in order on the synchronous channel. Status byte, remote/local and locks use
  the asynchronous channel, and device clear uses both. `GetLockInfo()` returns whether an exclusive lock is
  granted and how many clients hold locks.
- Service requests arrive as `AsyncServiceRequest` messages with the status byte, so
  `ServiceRequestEventArgs.StatusByte` is filled.

### HiSLIP 2.0 secure connections

HiSLIP 2.0 (IVI-6.1) and IVI-6.5 add TLS encryption and SASL client authentication on the same port. VisaCs
implements both. Configure them on `ResourceManagerOptions.HislipSecurity`:

```csharp
var options = new ResourceManagerOptions();
options.HislipSecurity.Encryption = HislipEncryptionMode.Required;
options.HislipSecurity.Credentials = new NetworkCredential("operator", "secret");
options.HislipSecurity.TrustedCertificates.Add(X509CertificateLoader.LoadCertificateFromFile("instrument.cer"));
using var rm = new ResourceManager(options);

using var s = rm.Open<TcpipHislipSession>("TCPIP::scope.lab::hislip0::INSTR");
Console.WriteLine($"{s.TlsProtocol} {s.TlsCipherSuite}, authenticated with {s.SaslMechanism}");
```

`Encryption` decides when TLS is used:

| Mode | Behavior |
|---|---|
| `Auto` (default) | Encrypt only when the server requires it (mandatory or initial encryption). |
| `Preferred` | Encrypt whenever the server supports secure connections. |
| `Required` | Always encrypt; opening fails if the server cannot. |
| `Disabled` | Never encrypt; opening fails if the server requires encryption. |

Authentication happens right after TLS starts, so with `Auto` and a server that does not require encryption no
credentials are sent. Use `Preferred` or `Required` when the instrument restricts access by user.

**SASL mechanisms.** Unless you list mechanisms in `SaslMechanisms`, the client uses the strongest one the server
offers, whatever order the server lists them in:

| Mechanism | Used when |
|---|---|
| `SCRAM-SHA-256-PLUS` | credentials are available; binds to the TLS channel (`tls-server-end-point`) |
| `SCRAM-SHA-256` | credentials are available |
| `EXTERNAL` | `ClientCertificates` is not empty (the TLS client certificate identifies you) |
| `ANONYMOUS` | no credentials, or listed explicitly in `SaslMechanisms` (sends `AnonymousTrace`) |
| `PLAIN` | only when listed in `SaslMechanisms` (it sends the password itself, protected only by TLS) |

`SaslMechanisms` sets both the allowed mechanisms and their order. A mechanism the server rejects outright is
skipped, but when the server rejects the credentials, authentication fails without trying another mechanism:
an impostor that makes channel binding fail on purpose cannot get the password through a weaker mechanism.

Credentials come from `HostCredentials[host]` (the host as written in the resource name), then
`CredentialProvider(host)`, then `Credentials`. There is no persistent IVI-6.5 credential store.

**Server certificates.** By default the certificate must chain to a root trusted by the operating system and
match the host name. For instruments with self-signed or private-CA certificates, add the certificate or the
CA to `TrustedCertificates` (both pinning and custom roots work; the host name is still checked).
`TargetHostName` overrides the name that is checked (and sent as SNI), `CheckCertificateName = false` skips the
name check, and `ServerCertificateValidation` replaces the checks entirely. `AcceptAnyServerCertificate` exists
for tests only. Revocation is not checked by default, because instruments often cannot reach the issuers'
revocation lists; set `RevocationMode` to `Online` or `Offline` to check it (pinned certificates are exempt).

**At run time** `TcpipHislipSession` reports `SecureConnectionSupported`, `EncryptionMandatory`,
`EncryptionEnabled`, `SaslMechanism`, `TlsProtocol`, `TlsCipherSuite` and `ServerCertificate` (all null when the
connection is not encrypted), plus the matching `VI_ATTR_TCPIP_*` attributes. `SetEncryptionEnabled(true)` starts
TLS and authentication on an open session with the current `HislipSecurity` options; `SetEncryptionEnabled(false)`
ends TLS when the server does not make it mandatory. `GetDescriptors()` returns the server's HiSLIP 2.0
descriptors (`HislipDescriptorType`).

Security failures throw `VisaSecurityException` (`VI_ERROR_NPERMISSION`) with `Failure` set to `TlsNegotiation`,
`ServerCertificate`, `Authentication` or `Policy`. A handshake that does not finish in time throws
`VisaTimeoutException`. When encryption is switched on an open session, a server that still has unread data
answers `VI_ERROR_RSRC_BUSY` and nothing changes; any other failure, including a timeout or cancellation, closes
the connection, and later calls throw `VI_ERROR_CONN_LOST` until the session is reopened.

TLS uses the operating system's stack through `SslStream` (SChannel on Windows, OpenSSL on Linux), TLS 1.2 and
1.3 by default (`TlsProtocols`).

## Raw sockets

```csharp
using var s = rm.Open<TcpipSocketSession>("TCPIP::192.168.1.5::5025::SOCKET");
```

- A TCP stream has no END indicator, so `TerminationCharacterEnabled` starts as true (with `\n`) for socket
  sessions. Reads stop at the termination character, the count, or the timeout.
- `ReadStatusByte` sends `*STB?` and parses the reply; `AssertTrigger` sends `*TRG`; `Clear` discards
  buffered input (and sends `*CLS` when `IOProtocol` is `Ieee4882Strings`). Remote/local control and service
  requests are not available.
- `NoDelay` (`VI_ATTR_TCPIP_NODELAY`, default true) and `KeepAlive` (`VI_ATTR_TCPIP_KEEPALIVE`) are
  settable.
- After a timeout the late reply is still on its way. Wait for it, then `Clear()`.

## VICP

```csharp
using var s = rm.Open<VicpSession>("VICP::192.168.1.9::INSTR");
```

VICP (port 1861, `ResourceManagerOptions.VicpPort`) frames each block with an EOI flag, so reads end on END.
Status byte, device clear and remote/local use VICP's own operations; a device clear reconnects the socket, as
VICP requires. `AssertTrigger` sends `*TRG`. Service requests arrive in-band and are only noticed while the
session is reading.

## Timeouts, cancellation and threads

The session timeout bounds each call; cancelling the token of an `*Async` call ends it like a timeout, and the
call throws `OperationCanceledException`. What remains afterwards depends on the protocol:

| Protocol | After a timeout or cancellation |
|---|---|
| VXI-11 | Cancelling sends `device_abort` on the abort channel, so the instrument ends the pending call. The session stays usable; the late reply is discarded. Lock requests are aborted too, and a lock the instrument grants afterwards is released again. |
| HiSLIP | A read keeps its place: the next read continues the same response (in synchronized mode the next write discards it). A late answer to a status, lock or clear request is discarded, and a lock granted after its request was cancelled is released again. |
| Raw socket, VICP | The late response is still on its way: wait for it, then `Clear()`. |

A write that times out after part of a message went out leaves VXI-11 and HiSLIP instruments inside that message
(they would read the next request as its rest), so the session closes the connection: the write throws
`VisaTimeoutException`, and later calls throw `VI_ERROR_CONN_LOST` until the session is reopened. A raw socket
simply has sent part of the data.

Operations on one session run one at a time. Locking, and on HiSLIP `GetLockInfo` and `GetDescriptors`, do not
wait for a read or write in progress. `Dispose` may be called from another thread to end a blocked call, which
then fails with `VI_ERROR_CONN_LOST` (or `VI_ERROR_INV_OBJECT` once the session is closed).

Asynchronous calls hold no thread while they wait. Synchronous calls block their thread, but a few of them also
wait for work that completes on the .NET thread pool: HiSLIP requests on the asynchronous channel (status byte,
locks, device clear, and the message-size negotiation when opening) are answered through a background reader,
reads and writes on an encrypted HiSLIP connection go through `SslStream`'s asynchronous I/O, and connection
setup (TCP connect, DNS lookup with a timeout, TLS handshake) waits for asynchronous operations. A program that
blocks every thread-pool thread can delay these calls until the pool adds threads.

## Discovery

`rm.Find("TCPIP?*")` combines:

- a VXI-11 portmapper `GETPORT` broadcast on every local IPv4 network (`EnableLanDiscovery`,
  `LanDiscoveryUsesBroadcast`, plus unicast `LanDiscoveryTargets`);
- mDNS/DNS-SD queries for `_hislip._tcp`, `_vxi-11._tcp` and `_scpi-raw._tcp` (`EnableMdnsDiscovery`;
  `MdnsEndPoint` sends them to a specific responder instead of the multicast group);
- `StaticResources`.

Both LAN mechanisms listen for `LanDiscoveryTimeoutMilliseconds` (1000 ms). `FindResources` returns the mDNS
instance name as the description.

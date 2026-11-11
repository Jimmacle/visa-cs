using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VisaCs.Lan.Rpc;
using VisaCs.Simulation;

namespace VisaCs.Tests;

/// <summary>Timeouts, cancellation and failures in the middle of LAN protocol exchanges.</summary>
public sealed class ProtocolRobustnessTests(HislipSecurityFixture certificates)
    : IClassFixture<HislipSecurityFixture>
{
    private static ResourceManager CreateResourceManager(Action<ResourceManagerOptions>? configure = null)
    {
        var options = new ResourceManagerOptions { EnableLanDiscovery = false, EnableMdnsDiscovery = false };
        configure?.Invoke(options);
        return new ResourceManager(options);
    }

    // ------------------------------------------------------------------ partial sends

    [Fact]
    public void HislipClosesTheConnectionAfterAPartlySentMessage()
    {
        using var server = new StalledHislipServer();
        using var rm = CreateResourceManager();
        using var session = rm.Open<TcpipHislipSession>($"TCPIP::127.0.0.1::hislip0,{server.Port}::INSTR");
        session.TimeoutMilliseconds = 500;

        // The server never reads the synchronous channel: once the socket buffers are full, a write stops part-way
        // through its Data message (inside the payload, or right after the header).
        // A write that cannot send anything at all just times out and leaves the connection usable.
        var data = new byte[8 * 1024 * 1024];
        VisaTimeoutException? ex = null;
        for (int i = 0; i < 64 && ex?.Message.Contains("connection was closed") != true; i++)
            ex = Record.Exception(() => session.Write(data)) as VisaTimeoutException;
        Assert.Contains("connection was closed", ex?.Message);
        // The server would read anything that follows as the rest of that message, so the connection is gone.
        Assert.Equal(VisaStatus.ErrorConnectionLost, Assert.Throws<VisaException>(() => session.Write("*IDN?\n")).Status);
        Assert.Equal(VisaStatus.ErrorConnectionLost, Assert.Throws<VisaException>(() => session.ReadStatusByte()).Status);
        Assert.True(server.SyncChannelClosed.Wait(5000), "The client did not close the connection.");
    }

    [Fact]
    public async Task RpcClosesTheConnectionAfterAPartlySentRequest()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptSocketAsync();
        // Generous: on a busy CI machine the connect's completion can wait for a thread-pool thread.
        using var client = RpcTcpClient.Connect((IPEndPoint)listener.LocalEndpoint, 0x0607AF, 1, 10_000);
        using var peer = await accept; // never reads

        // Fill the socket buffers. Calls that were sent completely time out waiting for a reply (the connection stays
        // usable); the one cut off while sending (an asynchronous send that is cancelled cannot tell how much it sent)
        // closes the connection.
        var data = new byte[8 * 1024 * 1024];
        Exception? ex = null;
        for (int i = 0; i < 64 && ex?.Message.Contains("partly sent") != true; i++)
            ex = await Record.ExceptionAsync(async () => await client.CallAsync(11, w => w.WriteOpaque(data), 300));
        Assert.IsType<VisaTimeoutException>(ex);
        Assert.Contains("partly sent", ex.Message);
        var next = await Assert.ThrowsAsync<VisaException>(async () => await client.CallAsync(13, null, 500));
        Assert.Equal(VisaStatus.ErrorConnectionLost, next.Status);
    }

    // ------------------------------------------------------------------ HiSLIP TLS switch and locks

    [Fact]
    public void HislipClosesTheConnectionWhenStartTlsIsAnsweredLate()
    {
        var instrument = new SimulatedInstrument();
        using var server = new HislipInstrumentServer(instrument)
        {
            Certificate = certificates.ServerCertificate,
            StartTlsResponseDelayMilliseconds = 1500,
        };
        server.Users["alice"] = "pw";
        server.Start();
        using var rm = CreateResourceManager(o =>
        {
            o.HislipSecurity.TrustedCertificates.Add(certificates.PublicServerCertificate);
            o.HislipSecurity.Credentials = new NetworkCredential("alice", "pw");
        });
        using var session = rm.Open<TcpipHislipSession>($"TCPIP::127.0.0.1::hislip0,{server.Port}::INSTR");
        session.TimeoutMilliseconds = 300;

        Assert.Throws<VisaTimeoutException>(() => session.SetEncryptionEnabled(true));
        // The server may still agree and expect a handshake: the connection is closed instead of left half-switched.
        var watch = Stopwatch.StartNew();
        Assert.Equal(VisaStatus.ErrorConnectionLost, Assert.Throws<VisaException>(() => session.ReadStatusByte()).Status);
        Assert.True(watch.ElapsedMilliseconds < 250, "The failed switch left the session waiting.");
    }

    [Fact]
    public async Task HislipRunsOtherRequestsAroundTheTlsSwitch()
    {
        var instrument = new SimulatedInstrument();
        using var server = new HislipInstrumentServer(instrument) { Certificate = certificates.ServerCertificate };
        server.Users["alice"] = "pw";
        server.Start();
        using var rm = CreateResourceManager(o =>
        {
            o.HislipSecurity.TrustedCertificates.Add(certificates.PublicServerCertificate);
            o.HislipSecurity.Credentials = new NetworkCredential("alice", "pw");
        });
        using var session = rm.Open<TcpipHislipSession>($"TCPIP::127.0.0.1::hislip0,{server.Port}::INSTR");
        session.TimeoutMilliseconds = 10_000;

        // Lock information travels on the asynchronous channel without the session's I/O gate, so it can be asked
        // while that channel switches to and from TLS; it must never write into the handshake.
        using var stop = new CancellationTokenSource();
        int answers = 0;
        var poller = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                session.GetLockInfo();
                Interlocked.Increment(ref answers);
            }
        });
        for (int i = 0; i < 4; i++)
        {
            session.SetEncryptionEnabled(true);
            Assert.Equal("on", session.Query("ECHO? on"));
            session.SetEncryptionEnabled(false);
            Assert.Equal("off", session.Query("ECHO? off"));
        }
        stop.Cancel();
        await poller;
        Assert.True(answers > 0);
    }

    [Fact]
    public async Task HislipReleasesALockGrantedAfterTheRequestWasCancelled()
    {
        using var server = new HislipInstrumentServer(new SimulatedInstrument());
        server.Start();
        await using var rm = CreateResourceManager();
        // Two names for one server, so the process-wide lock table does not keep the second session waiting.
        await using var a = await rm.OpenAsync<TcpipHislipSession>($"TCPIP::127.0.0.1::hislip0,{server.Port}::INSTR");
        await using var b = await rm.OpenAsync<TcpipHislipSession>($"TCPIP::localhost::hislip0,{server.Port}::INSTR");
        a.LockExclusive(1000);

        using (var cts = new CancellationTokenSource(300))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.LockExclusiveAsync(10_000, cts.Token));
        // The server still holds b's request and grants it once a unlocks; b's client must give it back.
        a.Unlock();
        a.LockExclusive(5000);
        Assert.True(a.GetLockInfo().ExclusiveLockGranted);
        a.Unlock();
        Assert.Equal("x", b.Query("ECHO? x"));
    }

    [Fact]
    public async Task HislipClosesPromptlyWhileALockRequestWaits()
    {
        using var server = new HislipInstrumentServer(new SimulatedInstrument());
        server.Start();
        await using var rm = CreateResourceManager();
        await using var a = await rm.OpenAsync<TcpipHislipSession>($"TCPIP::127.0.0.1::hislip0,{server.Port}::INSTR");
        var b = await rm.OpenAsync<TcpipHislipSession>($"TCPIP::localhost::hislip0,{server.Port}::INSTR");
        a.LockExclusive(1000);
        var waiting = b.LockExclusiveAsync(-1);
        await Task.Delay(200);

        var watch = Stopwatch.StartNew();
        b.Dispose();
        Assert.True(watch.ElapsedMilliseconds < 2000, $"Closing took {watch.ElapsedMilliseconds} ms.");
        await Assert.ThrowsAnyAsync<VisaException>(() => waiting);
        a.Unlock();
        // The closed session's request must not end up owning the lock.
        a.LockExclusive(2000);
        a.Unlock();
    }

    // ------------------------------------------------------------------ SASL

    [Fact]
    public void UsesTheStrongestMechanismWhateverTheServerPrefers()
    {
        var instrument = new SimulatedInstrument();
        using var server = new HislipInstrumentServer(instrument) { Certificate = certificates.ServerCertificate };
        server.SaslMechanisms.Clear();
        server.SaslMechanisms.Add("PLAIN");
        server.SaslMechanisms.Add("SCRAM-SHA-256-PLUS");
        server.Users["alice"] = "pw";
        server.Start();
        using var rm = CreateResourceManager(o =>
        {
            o.HislipSecurity.Encryption = HislipEncryptionMode.Preferred;
            o.HislipSecurity.TrustedCertificates.Add(certificates.PublicServerCertificate);
            o.HislipSecurity.Credentials = new NetworkCredential("alice", "pw");
        });
        using var session = rm.Open<TcpipHislipSession>($"TCPIP::127.0.0.1::hislip0,{server.Port}::INSTR");
        Assert.Equal("SCRAM-SHA-256-PLUS", session.SaslMechanism);
    }

    [Fact]
    public void DoesNotRetryRejectedCredentialsWithAnotherMechanism()
    {
        var instrument = new SimulatedInstrument();
        using var server = new HislipInstrumentServer(instrument) { Certificate = certificates.ServerCertificate };
        server.Users["alice"] = "pw";
        server.Start();
        using var rm = CreateResourceManager(o =>
        {
            o.HislipSecurity.Encryption = HislipEncryptionMode.Preferred;
            o.HislipSecurity.TrustedCertificates.Add(certificates.PublicServerCertificate);
            o.HislipSecurity.Credentials = new NetworkCredential("alice", "wrong");
            o.HislipSecurity.SaslMechanisms.Add("SCRAM-SHA-256-PLUS");
            o.HislipSecurity.SaslMechanisms.Add("PLAIN");
        });
        var ex = Assert.Throws<VisaSecurityException>(() => rm.Open($"TCPIP::127.0.0.1::hislip0,{server.Port}::INSTR"));
        Assert.Equal(SecurityFailure.Authentication, ex.Failure);
        Assert.Equal(1, server.FailedAuthenticationCount); // PLAIN was never tried with the rejected password
    }

    // ------------------------------------------------------------------ XDR

    [Fact]
    public void RejectsOverlongXdrLengths()
    {
        var data = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(data, 0x7FFFFFFE);
        var reader = new XdrReader(data, 0, data.Length);
        Assert.Equal(VisaStatus.ErrorIO, Assert.Throws<VisaException>(() => reader.ReadOpaque()).Status);
    }

    /// <summary>
    /// A HiSLIP 1.0 server that completes initialization, answers the asynchronous channel, and never reads the
    /// synchronous channel again.
    /// </summary>
    private sealed class StalledHislipServer : IDisposable
    {
        private enum HislipMessageType : byte
        {
            InitializeResponse = 1,
            AsyncMaximumMessageSize = 15,
            AsyncMaximumMessageSizeResponse = 16,
            AsyncInitializeResponse = 18,
            AsyncStatusResponse = 22,
        }

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<Socket> _sockets = new();

        public StalledHislipServer()
        {
            _listener.Start();
            _ = Task.Run(RunAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public ManualResetEventSlim SyncChannelClosed { get; } = new();

        private async Task RunAsync()
        {
            try
            {
                var sync = await _listener.AcceptSocketAsync();
                lock (_sockets) _sockets.Add(sync);
                await ReceiveMessageAsync(sync);
                await SendAsync(sync, HislipMessageType.InitializeResponse, 0, 0x0100_0001u, []);
                var async = await _listener.AcceptSocketAsync();
                lock (_sockets) _sockets.Add(async);
                await ReceiveMessageAsync(async);
                await SendAsync(async, HislipMessageType.AsyncInitializeResponse, 0, 0x5349, []);
                while (true)
                {
                    var (type, _) = await ReceiveMessageAsync(async);
                    if (type == HislipMessageType.AsyncMaximumMessageSize)
                    {
                        var size = new byte[8];
                        BinaryPrimitives.WriteUInt64BigEndian(size, 1UL << 30);
                        await SendAsync(async, HislipMessageType.AsyncMaximumMessageSizeResponse, 0, 0, size);
                    }
                    else
                    {
                        await SendAsync(async, HislipMessageType.AsyncStatusResponse, 0, 0, []);
                    }
                }
            }
            catch
            {
                // The client closed the connection (it closes both channels together), or the test ended.
                SyncChannelClosed.Set();
            }
        }

        private const int HeaderSize = 16;

        private static async Task<(HislipMessageType Type, byte[] Payload)> ReceiveMessageAsync(Socket socket)
        {
            var header = new byte[HeaderSize];
            await ReceiveExactAsync(socket, header);
            if (header[0] != 'H' || header[1] != 'S') throw new IOException("Bad HiSLIP prologue.");
            var payload = new byte[(int)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8))];
            await ReceiveExactAsync(socket, payload);
            return ((HislipMessageType)header[2], payload);
        }

        private static async Task ReceiveExactAsync(Socket socket, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int n = await socket.ReceiveAsync(buffer.AsMemory(offset));
                if (n == 0) throw new IOException("closed");
                offset += n;
            }
        }

        private static async Task SendAsync(Socket socket, HislipMessageType type, byte control, uint parameter, byte[] payload)
        {
            var message = new byte[HeaderSize + payload.Length];
            message[0] = (byte)'H';
            message[1] = (byte)'S';
            message[2] = (byte)type;
            message[3] = control;
            BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), parameter);
            BinaryPrimitives.WriteUInt64BigEndian(message.AsSpan(8), (ulong)payload.Length);
            payload.CopyTo(message, HeaderSize);
            await socket.SendAsync(message);
        }

        public void Dispose()
        {
            _listener.Stop();
            lock (_sockets)
            {
                foreach (var s in _sockets) s.Dispose();
            }
        }
    }
}

/// <summary>VXI-11 robustness tests; they use the shared simulator's port-less resource names.</summary>
[Collection(PortlessLanResourceCollection.Name)]
public sealed class Vxi11RobustnessTests(SimulatorFixture fixture) : IClassFixture<SimulatorFixture>
{
    [Fact]
    public async Task ClosesPromptlyWhileACallIsInProgress()
    {
        using var rm = fixture.CreateResourceManager();
        var session = rm.Open<TcpipVxi11Session>(fixture.Simulator.Vxi11Resource);
        session.TimeoutMilliseconds = -1;
        var query = Task.Run(() => session.Query("DELAY? 4000"));
        await Task.Delay(300);

        var watch = Stopwatch.StartNew();
        session.Dispose();
        Assert.True(watch.ElapsedMilliseconds < 2000, $"Closing waited {watch.ElapsedMilliseconds} ms for the read in progress.");
        await Assert.ThrowsAnyAsync<VisaException>(() => query);
    }

    [Fact]
    public async Task ReleasesALockGrantedAfterTheRequestWasCancelled()
    {
        using var rm = fixture.CreateResourceManager();
        // Two names for one instrument, so the process-wide lock table does not keep the second session waiting.
        using var a = rm.Open<TcpipVxi11Session>("TCPIP::127.0.0.1::inst0::INSTR");
        using var b = rm.Open<TcpipVxi11Session>("TCPIP::localhost::inst0::INSTR");
        a.LockExclusive(1000);
        try
        {
            using (var cts = new CancellationTokenSource(300))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.LockExclusiveAsync(10_000, cts.Token));
            // The instrument grants b's waiting device_lock once a unlocks; b's client must give it back.
            a.Unlock();
            a.LockExclusive(5000);
        }
        finally
        {
            if (a.ResourceLockState != ResourceLockState.NoLock) a.Unlock();
        }
        Assert.Equal(fixture.Instrument.Identity, b.Query("*IDN?"));
    }
}

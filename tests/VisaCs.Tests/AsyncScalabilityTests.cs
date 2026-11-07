using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace VisaCs.Tests;

/// <summary>Tests that measure threads; they run after the parallel tests, on their own.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NonParallelCollection
{
    public const string Name = "Not run in parallel";
}

/// <summary>
/// Pending asynchronous operations must not hold threads. Many reads wait on a server that never answers; the
/// thread pool must stay free, and cancelling must end every read.
/// </summary>
[Collection(NonParallelCollection.Name)]
public sealed class AsyncScalabilityTests
{
    [Fact]
    public Task PendingReadsHoldNoThreads() =>
        // Fail instead of hanging the whole run if reads do hold threads after all.
        PendingReadsHoldNoThreadsCore().WaitAsync(TimeSpan.FromSeconds(60));

    private static async Task PendingReadsHoldNoThreadsCore()
    {
        // A server that accepts connections and never sends anything.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1024);
        var accepted = new List<Socket>();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var socket = await listener.AcceptSocketAsync();
                    lock (accepted) accepted.Add(socket);
                }
            }
            catch (Exception)
            {
                // Listener stopped.
            }
        });
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Cap the worker pool and leave four times as many reads pending. Reads that block a thread each would occupy
        // every worker, and the work item below could not run until they timed out. The cap is at least 8: on small
        // machines (CI runners have 2-4 cores) the test framework itself needs a few pool threads.
        ThreadPool.GetMaxThreads(out int maxWorkers, out int maxIo);
        ThreadPool.GetMinThreads(out int minWorkers, out _);
        int workers = Math.Max(Math.Max(minWorkers, Environment.ProcessorCount), 8);
        int count = workers * 4;
        Assert.True(ThreadPool.SetMaxThreads(workers, maxIo));
        try
        {
            await using var rm = new ResourceManager(new ResourceManagerOptions { EnableLanDiscovery = false, EnableMdnsDiscovery = false });
            var sessions = new List<MessageBasedSession>();
            for (int i = 0; i < count; i++)
            {
                var session = await rm.OpenAsync($"TCPIP0::127.0.0.1::{port}::SOCKET");
                session.TimeoutMilliseconds = 10_000;
                sessions.Add(session);
            }

            int processThreadsBefore = Process.GetCurrentProcess().Threads.Count;
            using var cts = new CancellationTokenSource();
            var reads = sessions.Select(s => s.ReadLineAsync(cts.Token)).ToArray();
            await Task.Delay(500);
            Assert.All(reads, r => Assert.False(r.IsCompleted));

            var latency = Stopwatch.StartNew();
            await Task.Run(() => { });
            latency.Stop();
            Assert.True(latency.ElapsedMilliseconds < 1000,
                $"With {count} reads pending, the thread pool ({workers} workers) took {latency.ElapsedMilliseconds} ms to run a work item.");
            Assert.True(Process.GetCurrentProcess().Threads.Count - processThreadsBefore < count / 4,
                $"The process grew from {processThreadsBefore} to {Process.GetCurrentProcess().Threads.Count} threads for {count} pending reads.");

            var cancelling = Stopwatch.StartNew();
            cts.Cancel();
            foreach (var read in reads)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            Assert.True(cancelling.ElapsedMilliseconds < 2000, $"Cancelling {count} reads took {cancelling.ElapsedMilliseconds} ms.");
        }
        finally
        {
            ThreadPool.SetMaxThreads(maxWorkers, maxIo);
        }

        listener.Stop();
        lock (accepted)
        {
            foreach (var socket in accepted) socket.Dispose();
        }
    }
}

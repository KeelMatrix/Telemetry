// Copyright (c) KeelMatrix

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using KeelMatrix.Telemetry.Infrastructure;
using KeelMatrix.Telemetry.Storage;

namespace KeelMatrix.Telemetry.IntegrationTests;

// TelemetryConfig.Runtime, DurableTelemetryQueue.Instance and TelemetryDeliveryWorker are global/static.
// Keep these tests non-parallel.
[CollectionDefinition(Name, DisableParallelization = true)]
public static class TelemetryDeliveryWorkerIntegrationTestsCollectionDefinition {
    public const string Name = $"{nameof(TelemetryDeliveryWorkerIntegrationTests)}.NonParallel";
}

[Collection(TelemetryDeliveryWorkerIntegrationTestsCollectionDefinition.Name)]
public sealed class TelemetryDeliveryWorkerIntegrationTests : IDisposable {
    private readonly string toolNameUpper;
    private readonly string rootDir;
    private readonly string queueRootDir;
    private readonly string pendingDir;
    private readonly string processingDir;
#pragma warning disable S4487 // Unread "private" fields should be removed
    private readonly string deadDir;
#pragma warning restore S4487
    private readonly string markersDir;

    private readonly LocalTelemetryServer server;
    private readonly EnvVarScope env;

    public TelemetryDeliveryWorkerIntegrationTests() {
        toolNameUpper = "INTEGRATIONTEST_WORKER_" + Guid.NewGuid().ToString("N");

        // Isolate opt-out vars per test.
        env = new EnvVarScope(
            "KEELMATRIX_NO_TELEMETRY",
            "DOTNET_CLI_TELEMETRY_OPTOUT",
            "DO_NOT_TRACK");

        // Configure runtime first (must be done before resetting queue instance).
        TelemetryConfig.Runtime.Set(toolNameUpper, typeof(TelemetryDeliveryWorkerIntegrationTests));

        // Best-effort compute paths for cleanup/assertions.
        rootDir = ResolveRootDir(toolNameUpper);
        queueRootDir = Path.Combine(rootDir, "telemetry.queue");
        pendingDir = Path.Combine(queueRootDir, "pending");
        processingDir = Path.Combine(queueRootDir, "processing");
        deadDir = Path.Combine(queueRootDir, "dead");
        markersDir = Path.Combine(rootDir, "markers");

        // Fresh start for this test.
        TryDeleteDirectory(rootDir);

        // Force DurableTelemetryQueue.Instance to be re-created under this test's root.
        ResetDurableQueueSingletonForTests();

        // Reset worker state that caches marker directory/project hash.
        ResetWorkerSingletonForTests();

        server = new LocalTelemetryServer();
        TelemetryConfig.SetUrlOverrideForTests(server.BaseUri);

        // Start worker (non-blocking); worker wakes immediately on construction and will deliver backlog.
        TelemetryDeliveryWorker.EnsureStarted();
    }

    [Fact]
    public async Task RequestActivation_SetsFlagAndDoesNotBlock() {
        var sw = Stopwatch.StartNew();

        var t = Task.Run(() => TelemetryDeliveryWorker.RequestActivation());
        var completed = await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(1)));

        completed.Should().Be(t, "RequestActivation must not block");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task DisabledPolicy_DoesNotSendOrTouchQueueIncludingBacklog() {
        // Arrange: disable telemetry and create a backlog file in pending.
        Environment.SetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY", "1");

        Directory.CreateDirectory(pendingDir);
        var backlogPath = Path.Combine(pendingDir, "backlog.json");
        await File.WriteAllTextAsync(backlogPath, new TelemetryEnvelope("{}").Serialize(), Encoding.UTF8);

        // Act: wake worker; it must skip delivery and avoid queue I/O when disabled.
        TelemetryDeliveryWorker.RequestActivation();
        await Task.Delay(250);

        // Assert: no HTTP attempts and backlog still present.
        server.Received.Count.Should().Be(0);
        File.Exists(backlogPath).Should().BeTrue();

        // If the worker touched the backlog it would likely move items into processing.
        if (Directory.Exists(processingDir)) {
            Directory.EnumerateFiles(processingDir, "*.json").Should().BeEmpty();
        }
    }

    [Fact]
    public async Task ActivationPlanning_EnqueuesEventAndCommitsActivationMarker() {
        // Act
        TelemetryDeliveryWorker.RequestActivation();

        // Assert: observe at least one HTTP request.
        await WaitUntilAsync(() => !server.Received.IsEmpty, TimeSpan.FromSeconds(5));

        // Planning commits activation marker and a heartbeat marker for the current week.
        await WaitUntilAsync(() => Directory.Exists(markersDir), TimeSpan.FromSeconds(2));

        var week = TelemetryClock.GetCurrentIsoWeek();

        Directory.EnumerateFiles(markersDir, "activation.*.json").Should().NotBeEmpty();
        Directory.EnumerateFiles(markersDir, $"heartbeat.*.{week}.json").Should().NotBeEmpty();
    }

    [Fact]
    public async Task ActivationPlanning_SuppressesHeartbeatForSameWeek() {
        // Arrange: request both before the worker wakes so they are processed together.
        TelemetryDeliveryWorker.RequestActivation();
        TelemetryDeliveryWorker.RequestHeartbeat();

        // Assert: only activation should be sent.
        await WaitUntilAsync(() => !server.Received.IsEmpty, TimeSpan.FromSeconds(5));
        await Task.Delay(250);

        var events = server.Received.Select(r => r.Event).ToList();
        events.Should().Contain("activation");
        events.Should().NotContain("heartbeat", "activation should suppress heartbeat for the same week");

        // Subsequent heartbeat request in the same week should also be suppressed by marker.
        TelemetryDeliveryWorker.RequestHeartbeat();
        await Task.Delay(250);

        server.Received.Select(r => r.Event).Should().NotContain("heartbeat");
    }

    [Fact]
    public async Task HeartbeatPlanning_EnqueuesAndCommitsHeartbeatMarker_WhenNotSuppressed() {
        TelemetryDeliveryWorker.RequestHeartbeat();

        await WaitUntilAsync(() => server.Received.Any(r => r.Event == "heartbeat"), TimeSpan.FromSeconds(5));

        var week = TelemetryClock.GetCurrentIsoWeek();
        Directory.EnumerateFiles(markersDir, $"heartbeat.*.{week}.json").Should().NotBeEmpty();
    }

    [Fact]
    public async Task DeliveryLoop_ClaimsSendsCompletesOrAbandons() {
        // Arrange: create backlog items without touching DurableTelemetryQueue.Instance by writing into pending.
        Directory.CreateDirectory(pendingDir);
        for (int i = 0; i < 6; i++) {
            var envl = new TelemetryEnvelope($"{{\"i\":{i}}}");
            await File.WriteAllTextAsync(Path.Combine(pendingDir, $"{envl.Id}.json"), envl.Serialize(), Encoding.UTF8);
        }

        // Act: wake worker to process backlog.
        TelemetryDeliveryWorker.RequestActivation();

        // Assert: all pending should be delivered and removed.
        await WaitUntilAsync(() => server.Received.Count >= 6, TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => !Directory.Exists(pendingDir) || !Directory.EnumerateFiles(pendingDir, "*.json").Any(), TimeSpan.FromSeconds(5));

        // No processing leftovers on all-success path.
        if (Directory.Exists(processingDir))
            Directory.EnumerateFiles(processingDir, "*.json").Should().BeEmpty();
    }

    public void Dispose() {
        try { TelemetryConfig.SetUrlOverrideForTests(null); } catch { /* swallow */ }

        server.Dispose();
        env.Dispose();

        // Best-effort cleanup of the per-test root.
        TryDeleteDirectory(rootDir);

        // Reset global hooks we changed.
        try { ResetWorkerSingletonForTests(); } catch { /* swallow */ }
        try { ResetDurableQueueSingletonForTests(); } catch { /* swallow */ }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout) {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout) {
            if (condition())
                return;
            await Task.Delay(25);
        }

        condition().Should().BeTrue($"condition should become true within {timeout}");
    }

    private static void ResetWorkerSingletonForTests() {
        // Clear cached dispatcher so TelemetryState rebinds to the current Runtime root.
        var t = typeof(TelemetryDeliveryWorker);
        var instField = t.GetField("Instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        instField.Should().NotBeNull();
        var inst = instField!.GetValue(null);
        inst.Should().NotBeNull();

        var dispatcherField = t.GetField("dispatcher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        dispatcherField.Should().NotBeNull();
        dispatcherField!.SetValue(inst, null);

        var hasPendingWorkField = t.GetField("hasPendingWork", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        hasPendingWorkField?.SetValue(inst, 0);

        // Also clear cached project hash in ProjectIdentityProvider to avoid cross-test coupling.
        var pit = typeof(KeelMatrix.Telemetry.ProjectIdentity.ProjectIdentityProvider);
        var sharedProp = pit.GetProperty("Shared", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (sharedProp is not null) {
            var shared = sharedProp.GetValue(null);
            if (shared is not null) {
                var cached = pit.GetField("cachedProjectHash", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                cached?.SetValue(shared, null);
            }
        }
    }

    private static void ResetDurableQueueSingletonForTests() {
        // Recreate under current TelemetryConfig.Runtime root.
        var newQueue = Activator.CreateInstance(typeof(DurableTelemetryQueue), nonPublic: true);
        newQueue.Should().NotBeNull();

        var backing = typeof(DurableTelemetryQueue).GetField("instance",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        backing.Should().NotBeNull();
        backing!.SetValue(null, (ITelemetryQueue)newQueue!);
    }

    private static string ResolveRootDir(string toolNameUpper) {
        // Mirrors TelemetryConfig.Runtime.ResolveRootDirectory() ordering.
        string? local = null;
        try { local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } catch { /* swallow */ }
        if (!string.IsNullOrWhiteSpace(local) && Path.IsPathRooted(local))
            return Path.Combine(local, "KeelMatrix", toolNameUpper);

        string? roaming = null;
        try { roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } catch { /* swallow */ }
        if (!string.IsNullOrWhiteSpace(roaming) && Path.IsPathRooted(roaming))
            return Path.Combine(roaming, "KeelMatrix", toolNameUpper);

        string? profile = null;
        try { profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch { /* swallow */ }
        if (!string.IsNullOrWhiteSpace(profile) && Path.IsPathRooted(profile))
            return Path.Combine(profile, "KeelMatrix", toolNameUpper);

        // Last resort: temp.
        return Path.Combine(Path.GetTempPath(), "KeelMatrix", toolNameUpper);
    }

    private static void TryDeleteDirectory(string dir) {
        try {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch {
            // swallow
        }
    }

    private sealed class EnvVarScope : IDisposable {
#pragma warning disable IDE0028 // Simplify collection initialization
        private readonly Dictionary<string, string?> snapshot = new(StringComparer.Ordinal);
#pragma warning restore IDE0028

        public EnvVarScope(params string[] names) {
            foreach (var n in names) {
                try { snapshot[n] = Environment.GetEnvironmentVariable(n); }
                catch { snapshot[n] = null; }
            }
        }

        public void Dispose() {
            foreach (var kv in snapshot) {
                try { Environment.SetEnvironmentVariable(kv.Key, kv.Value); }
                catch { /* swallow */ }
            }
        }
    }

    private sealed class LocalTelemetryServer : IDisposable {
        private readonly HttpListener listener;
        private readonly CancellationTokenSource cts = new();
        private readonly Task loop;

        public Uri BaseUri { get; }
        public ConcurrentQueue<ReceivedRequest> Received { get; } = new();

        public LocalTelemetryServer() {
            int port = GetFreeTcpPort();
            var prefix = $"http://127.0.0.1:{port}/";
            BaseUri = new Uri(prefix, UriKind.Absolute);

            listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();

            loop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync() {
            while (!cts.IsCancellationRequested) {
                HttpListenerContext? ctx;
                try {
                    ctx = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch {
                    if (cts.IsCancellationRequested)
                        return;
                    continue;
                }

                try {
                    string body;
                    using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false)) {
                        body = await sr.ReadToEndAsync().ConfigureAwait(false);
                    }

                    var ev = TryExtractEventField(body) ?? "";
                    Received.Enqueue(new ReceivedRequest(ev, body));

                    ctx.Response.StatusCode = 200;
                    var bytes = Encoding.UTF8.GetBytes("ok");
                    ctx.Response.ContentType = "text/plain";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                }
                catch {
                    // ignore
                }
                finally {
                    try { ctx?.Response.OutputStream.Close(); } catch { /* swallow */ }
                    try { ctx?.Response.Close(); } catch { /* swallow */ }
                }
            }
        }

        private static string? TryExtractEventField(string json) {
            // Minimal extraction to avoid taking a dependency on a JSON parser in tests.
            // Payloads are small and stable; look for: "event":"...".
            const string key = "\"event\"";
            int k = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (k < 0)
                return null;

            int colon = json.IndexOf(':', k);
            if (colon < 0)
                return null;

            int firstQuote = json.IndexOf('"', colon + 1);
            if (firstQuote < 0)
                return null;

            int secondQuote = json.IndexOf('"', firstQuote + 1);
            if (secondQuote < 0)
                return null;

            return json.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
        }

        private static int GetFreeTcpPort() {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose() {
            try { cts.Cancel(); cts.Dispose(); } catch { /* swallow */ }
            try { listener.Stop(); } catch { /* swallow */ }
            try { listener.Close(); } catch { /* swallow */ }
            try { loop.Wait(TimeSpan.FromSeconds(1)); } catch { /* swallow */ }
        }

        public readonly record struct ReceivedRequest(string Event, string Body);
    }
}

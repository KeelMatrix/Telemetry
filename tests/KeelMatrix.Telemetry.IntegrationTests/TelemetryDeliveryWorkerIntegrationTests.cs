// Copyright (c) KeelMatrix

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using KeelMatrix.Telemetry.Infrastructure;

namespace KeelMatrix.Telemetry.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public static class TelemetryDeliveryWorkerIntegrationTestsCollectionDefinition {
    public const string Name = $"{nameof(TelemetryDeliveryWorkerIntegrationTests)}.NonParallel";
}

[Collection(TelemetryDeliveryWorkerIntegrationTestsCollectionDefinition.Name)]
public sealed class TelemetryDeliveryWorkerIntegrationTests {
    [Fact]
    public async Task RequestActivation_SetsFlagAndDoesNotBlock() {
        using var harness = new WorkerHarness();
        using var worker = harness.CreateWorker();

        var sw = Stopwatch.StartNew();

        var task = Task.Run(worker.RequestActivation);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(1)));

        completed.Should().Be(task, "RequestActivation must not block");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task DisabledPolicy_DoesNotSendOrTouchQueueIncludingBacklog() {
        using var harness = new WorkerHarness();

        var queue = harness.CreateQueue();
        queue.Enqueue("{\"event\":\"backlog\"}");
        var backlogPath = Directory.EnumerateFiles(harness.PendingDir, "*.json").Single();

        Environment.SetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY", "1");

        using var worker = harness.CreateWorker();
        worker.RequestActivation();
        await Task.Delay(250);

        harness.Server.Received.Count.Should().Be(0);
        File.Exists(backlogPath).Should().BeTrue();

        if (Directory.Exists(harness.ProcessingDir))
            Directory.EnumerateFiles(harness.ProcessingDir, "*.json").Should().BeEmpty();
    }

    [Fact]
    public async Task ActivationPlanning_EnqueuesEventAndCommitsActivationMarker() {
        using var harness = new WorkerHarness();
        using var worker = harness.CreateWorker();

        worker.RequestActivation();

        await WaitUntilAsync(() => harness.Server.Received.Any(r => r.Event == "activation"), TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => Directory.Exists(harness.MarkersDir), TimeSpan.FromSeconds(2));

        Directory.EnumerateFiles(harness.MarkersDir, "activation.*.json").Should().NotBeEmpty();
        Directory.EnumerateFiles(harness.MarkersDir, $"heartbeat.*.{harness.CurrentWeek}.json").Should().NotBeEmpty();
    }

    [Fact]
    public async Task ActivationPlanning_SuppressesHeartbeatForSameWeek() {
        using var harness = new WorkerHarness();
        using var worker = harness.CreateWorker();

        worker.RequestActivation();
        worker.RequestHeartbeat();

        await WaitUntilAsync(() => harness.Server.Received.Any(r => r.Event == "activation"), TimeSpan.FromSeconds(5));
        await Task.Delay(250);

        var events = harness.Server.Received.Select(r => r.Event).ToList();
        events.Should().Contain("activation");
        events.Should().NotContain("heartbeat", "activation should suppress heartbeat for the same week");

        worker.RequestHeartbeat();
        await Task.Delay(250);

        harness.Server.Received.Select(r => r.Event).Should().NotContain("heartbeat");
    }

    [Fact]
    public async Task HeartbeatPlanning_EnqueuesAndCommitsHeartbeatMarker_WhenNotSuppressed() {
        using var harness = new WorkerHarness();
        using var worker = harness.CreateWorker();

        worker.RequestHeartbeat();

        await WaitUntilAsync(() => harness.Server.Received.Any(r => r.Event == "heartbeat"), TimeSpan.FromSeconds(5));

        Directory.EnumerateFiles(harness.MarkersDir, $"heartbeat.*.{harness.CurrentWeek}.json").Should().NotBeEmpty();
    }

    [Fact]
    public async Task DeliveryLoop_ClaimsSendsCompletesOrAbandons() {
        using var harness = new WorkerHarness();
        var queue = harness.CreateQueue();

        for (int i = 0; i < 6; i++)
            queue.Enqueue($"{{\"i\":{i}}}");

        using var worker = harness.CreateWorker();

        await WaitUntilAsync(() => harness.Server.Received.Count >= 6, TimeSpan.FromSeconds(10));
        await WaitUntilAsync(
            () => !Directory.Exists(harness.PendingDir) || !Directory.EnumerateFiles(harness.PendingDir, "*.json").Any(),
            TimeSpan.FromSeconds(5));

        if (Directory.Exists(harness.ProcessingDir))
            Directory.EnumerateFiles(harness.ProcessingDir, "*.json").Should().BeEmpty();
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

    private sealed class WorkerHarness : IDisposable {
        private readonly EnvVarScope env;
        private readonly string rootDir;

        public WorkerHarness() {
            env = new EnvVarScope(
                "KEELMATRIX_NO_TELEMETRY",
                "DOTNET_CLI_TELEMETRY_OPTOUT",
                "DO_NOT_TRACK");

            env.Clear();
            ResetProcessDisabledForTests();

            Server = new LocalTelemetryServer();
            TelemetryConfig.SetUrlOverrideForTests(Server.BaseUri);

            var toolNameUpper = "INTEGRATIONTEST_WORKER_" + Guid.NewGuid().ToString("N");
            RuntimeContext = new TelemetryRuntimeContext(toolNameUpper, typeof(TelemetryDeliveryWorkerIntegrationTests));
            RuntimeInfo = new RuntimeInfo();

            RuntimeContext.EnsureRootDirectoryResolvedOnWorkerThread();
            rootDir = RuntimeContext.GetRootDirectory();
            TryDeleteDirectory(rootDir);
        }

        public LocalTelemetryServer Server { get; }
        public TelemetryRuntimeContext RuntimeContext { get; }
        public RuntimeInfo RuntimeInfo { get; }
        public string PendingDir => Path.Combine(rootDir, "telemetry.queue", "pending");
        public string ProcessingDir => Path.Combine(rootDir, "telemetry.queue", "processing");
        public string MarkersDir => Path.Combine(rootDir, "markers");
        public string CurrentWeek => TelemetryClock.GetCurrentIsoWeek();

        public ITelemetryQueue CreateQueue() {
            return DurableTelemetryQueue.CreateSafe(RuntimeContext);
        }

        public TelemetryDeliveryWorker CreateWorker() {
            return new TelemetryDeliveryWorker(RuntimeContext, RuntimeInfo);
        }

        public void Dispose() {
            try { TelemetryConfig.SetUrlOverrideForTests(null); } catch { /* swallow */ }

            Server.Dispose();
            env.Dispose();
            TryDeleteDirectory(rootDir);
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

        private static void ResetProcessDisabledForTests() {
            var field = typeof(TelemetryConfig).GetField("processDisabled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (field is not null)
                field.SetValue(null, 0);
        }
    }

    private sealed class EnvVarScope : IDisposable {
#pragma warning disable IDE0028 // Simplify collection initialization
        private readonly Dictionary<string, string?> snapshot = new(StringComparer.Ordinal);
#pragma warning restore IDE0028

        public EnvVarScope(params string[] names) {
            foreach (var name in names) {
                try { snapshot[name] = Environment.GetEnvironmentVariable(name); }
                catch { snapshot[name] = null; }
            }
        }

        public void Clear() {
            foreach (var name in snapshot.Keys)
                Environment.SetEnvironmentVariable(name, null);
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

        public LocalTelemetryServer() {
            int port = GetFreeTcpPort();
            var prefix = $"http://127.0.0.1:{port}/";

            BaseUri = new Uri(prefix, UriKind.Absolute);

            listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();

            loop = Task.Run(AcceptLoopAsync);
        }

        public Uri BaseUri { get; }
        public ConcurrentQueue<ReceivedRequest> Received { get; } = new();

        private async Task AcceptLoopAsync() {
            while (!cts.IsCancellationRequested) {
                HttpListenerContext? context;
                try {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch {
                    if (cts.IsCancellationRequested)
                        return;

                    continue;
                }

                try {
                    string body;
                    using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false)) {
                        body = await reader.ReadToEndAsync().ConfigureAwait(false);
                    }

                    var telemetryEvent = TryExtractEventField(body) ?? string.Empty;
                    Received.Enqueue(new ReceivedRequest(telemetryEvent, body));

                    context.Response.StatusCode = (int)HttpStatusCode.OK;
                    context.Response.ContentType = "text/plain";

                    var bytes = Encoding.UTF8.GetBytes("ok");
                    await context.Response.OutputStream.WriteAsync(bytes);
                }
                catch {
                    // swallow
                }
                finally {
                    try { context?.Response.OutputStream.Close(); } catch { /* swallow */ }
                    try { context?.Response.Close(); } catch { /* swallow */ }
                }
            }
        }

        private static string? TryExtractEventField(string json) {
            const string key = "\"event\"";
            int keyIndex = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (keyIndex < 0)
                return null;

            int colonIndex = json.IndexOf(':', keyIndex);
            if (colonIndex < 0)
                return null;

            int firstQuote = json.IndexOf('"', colonIndex + 1);
            if (firstQuote < 0)
                return null;

            int secondQuote = json.IndexOf('"', firstQuote + 1);
            if (secondQuote < 0)
                return null;

            return json.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
        }

        private static int GetFreeTcpPort() {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
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

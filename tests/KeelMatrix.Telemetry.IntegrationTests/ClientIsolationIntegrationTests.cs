// Copyright (c) KeelMatrix

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using KeelMatrix.Telemetry.Infrastructure;

namespace KeelMatrix.Telemetry.IntegrationTests;

public sealed class ClientIsolationIntegrationTests {
    [Fact]
    public async Task Clients_WithSameToolName_ReuseTheSameWorker_AndEmitSingleActivation() {
        using var harness = new ClientHarness();
        var toolNameUpper = harness.CreateToolName("SAME");

        var firstClient = harness.CreateClient(toolNameUpper);
        var secondClient = harness.CreateClient(toolNameUpper);

        var firstWorker = harness.GetWorker(firstClient);
        var secondWorker = harness.GetWorker(secondClient);

        firstWorker.Should().BeSameAs(secondWorker);

        var rootDir = harness.GetRootDirectory(firstWorker);

        firstClient.TrackActivation();
        secondClient.TrackActivation();

        await WaitUntilAsync(() => harness.Server.CountEvents("activation") >= 1, TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => CountFiles(Path.Combine(rootDir, "markers"), "activation.*.json") == 1, TimeSpan.FromSeconds(5));
        await Task.Delay(250);

        harness.Server.CountEvents("activation").Should().Be(1);
        CountFiles(Path.Combine(rootDir, "markers"), "activation.*.json").Should().Be(1);
        File.Exists(Path.Combine(rootDir, "telemetry.salt")).Should().BeTrue();
    }

    [Fact]
    public async Task Clients_WithDifferentToolNames_UseDifferentWorkers_AndSeparateRoots() {
        using var harness = new ClientHarness();
        var firstToolNameUpper = harness.CreateToolName("ALPHA");
        var secondToolNameUpper = harness.CreateToolName("BETA");

        var firstClient = harness.CreateClient(firstToolNameUpper);
        var secondClient = harness.CreateClient(secondToolNameUpper);

        var firstWorker = harness.GetWorker(firstClient);
        var secondWorker = harness.GetWorker(secondClient);

        firstWorker.Should().NotBeSameAs(secondWorker);

        var firstRootDir = harness.GetRootDirectory(firstWorker);
        var secondRootDir = harness.GetRootDirectory(secondWorker);

        firstRootDir.Should().NotBe(secondRootDir);

        firstClient.TrackActivation();
        secondClient.TrackActivation();

        await WaitUntilAsync(() => harness.Server.CountEvents("activation") >= 2, TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => CountFiles(Path.Combine(firstRootDir, "markers"), "activation.*.json") == 1, TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => CountFiles(Path.Combine(secondRootDir, "markers"), "activation.*.json") == 1, TimeSpan.FromSeconds(5));

        var emittedTools = harness.Server.Received
            .Where(request => request.Event == "activation")
            .Select(request => request.Tool)
            .ToHashSet(StringComparer.Ordinal);

        emittedTools.Should().Contain(firstToolNameUpper.ToLowerInvariant());
        emittedTools.Should().Contain(secondToolNameUpper.ToLowerInvariant());

        File.Exists(Path.Combine(firstRootDir, "telemetry.salt")).Should().BeTrue();
        File.Exists(Path.Combine(secondRootDir, "telemetry.salt")).Should().BeTrue();
    }

    [Fact]
    public async Task ConcurrentTrackCalls_DoNotDeadlock_AndOnlyEmitSingleActivationPerProject() {
        using var harness = new ClientHarness();
        var toolNameUpper = harness.CreateToolName("RACE");

        var client = harness.CreateClient(toolNameUpper);
        var worker = harness.GetWorker(client);
        var rootDir = harness.GetRootDirectory(worker);

        var calls = Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => {
                for (int i = 0; i < 20; i++) {
                    client.TrackActivation();
                    client.TrackHeartbeat();
                }
            }))
            .ToArray();

        var allCalls = Task.WhenAll(calls);
        var completed = await Task.WhenAny(allCalls, Task.Delay(TimeSpan.FromSeconds(3)));
        completed.Should().Be(allCalls, "client tracking calls must not deadlock");
        await allCalls;

        var markersDir = Path.Combine(rootDir, "markers");
        var queueDir = Path.Combine(rootDir, "telemetry.queue");

        await WaitUntilAsync(() => CountFiles(markersDir, "activation.*.json") == 1, TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => CountFiles(markersDir, $"heartbeat.*.{TelemetryClock.GetCurrentIsoWeek()}.json") == 1, TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => harness.Server.CountEvents("activation") >= 1, TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => CountQueueFiles(queueDir) == 0, TimeSpan.FromSeconds(5));
        await Task.Delay(250);

        CountFiles(markersDir, "activation.*.json").Should().Be(1);
        CountFiles(markersDir, $"heartbeat.*.{TelemetryClock.GetCurrentIsoWeek()}.json").Should().Be(1);
        harness.Server.CountEvents("activation").Should().Be(1);
        harness.Server.CountEvents("heartbeat").Should().Be(0);
        CountQueueFiles(queueDir).Should().Be(0);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout) {
        var startedAt = DateTime.UtcNow;

        while (DateTime.UtcNow - startedAt < timeout) {
            if (condition())
                return;

            await Task.Delay(25);
        }

        condition().Should().BeTrue($"condition should become true within {timeout}");
    }

    private static int CountFiles(string dir, string pattern) {
        if (!Directory.Exists(dir))
            return 0;

        return Directory.EnumerateFiles(dir, pattern).Count();
    }

    private static int CountQueueFiles(string queueDir) {
        if (!Directory.Exists(queueDir))
            return 0;

        return Directory.EnumerateFiles(queueDir, "*.json", SearchOption.AllDirectories).Count();
    }

    private sealed class ClientHarness : IDisposable {
        private readonly EnvironmentVariableSnapshot env;
        private readonly List<string> toolNames = [];
        private readonly List<Client> clients = [];

        public ClientHarness() {
            env = new EnvironmentVariableSnapshot("KEELMATRIX_NO_TELEMETRY", "DOTNET_CLI_TELEMETRY_OPTOUT", "DO_NOT_TRACK");
            ClearOptOutVars();
            ResetProcessDisabledForTests();

            Server = new LocalTelemetryServer();
            TelemetryConfig.SetUrlOverrideForTests(Server.BaseUri);
        }

        public LocalTelemetryServer Server { get; }

        public string CreateToolName(string prefix) {
            return $"CLIENT_{prefix}_{Guid.NewGuid():N}";
        }

        public Client CreateClient(string toolNameUpper) {
            toolNames.Add(toolNameUpper);

            var client = new Client(toolNameUpper, typeof(ClientIsolationIntegrationTests));
            clients.Add(client);
            return client;
        }

        public TelemetryDeliveryWorker GetWorker(Client client) {
            var inner = GetInnerTelemetryClient(client);
            inner.Should().BeOfType<TelemetryClient>();

            var workerField = typeof(TelemetryClient).GetField("worker", BindingFlags.Instance | BindingFlags.NonPublic);
            workerField.Should().NotBeNull();
            return (TelemetryDeliveryWorker)workerField!.GetValue(inner)!;
        }

        public string GetRootDirectory(TelemetryDeliveryWorker worker) {
            var runtimeContextField = typeof(TelemetryDeliveryWorker).GetField("runtimeContext", BindingFlags.Instance | BindingFlags.NonPublic);
            runtimeContextField.Should().NotBeNull();

            var runtimeContext = (TelemetryRuntimeContext)runtimeContextField!.GetValue(worker)!;
            runtimeContext.EnsureRootDirectoryResolvedOnWorkerThread();
            return runtimeContext.GetRootDirectory();
        }

        public void Dispose() {
            var workers = new HashSet<TelemetryDeliveryWorker>();

            foreach (var client in clients) {
                var inner = GetInnerTelemetryClient(client);
                if (inner is not TelemetryClient telemetryClient)
                    continue;

                var workerField = typeof(TelemetryClient).GetField("worker", BindingFlags.Instance | BindingFlags.NonPublic);
                var worker = (TelemetryDeliveryWorker?)workerField?.GetValue(telemetryClient);
                if (worker is not null)
                    workers.Add(worker);
            }

            foreach (var worker in workers) {
                try { worker.Dispose(); } catch { /* swallow */ }
            }

            try { TelemetryConfig.SetUrlOverrideForTests(null); } catch { /* swallow */ }
            Server.Dispose();
            env.Dispose();

            foreach (var toolNameUpper in toolNames.Distinct(StringComparer.Ordinal)) {
                try {
                    var runtimeContext = new TelemetryRuntimeContext(toolNameUpper, typeof(ClientIsolationIntegrationTests));
                    runtimeContext.EnsureRootDirectoryResolvedOnWorkerThread();
                    var rootDir = runtimeContext.GetRootDirectory();

                    if (Directory.Exists(rootDir))
                        Directory.Delete(rootDir, recursive: true);
                }
                catch {
                    // swallow
                }
            }
        }

        private static object? GetInnerTelemetryClient(Client client) {
            var field = typeof(Client).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic);
            field.Should().NotBeNull();
            return field!.GetValue(client);
        }

        private static void ClearOptOutVars() {
            Environment.SetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY", null);
            Environment.SetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", null);
            Environment.SetEnvironmentVariable("DO_NOT_TRACK", null);
        }

        private static void ResetProcessDisabledForTests() {
            var field = typeof(TelemetryConfig).GetField("processDisabled", BindingFlags.NonPublic | BindingFlags.Static);
            field.Should().NotBeNull();
            field!.SetValue(null, 0);
        }
    }

    private sealed class EnvironmentVariableSnapshot : IDisposable {
        private readonly (string Name, string? Value)[] snapshot;

        public EnvironmentVariableSnapshot(params string[] names) {
            snapshot = new (string, string? Value)[names.Length];
            for (int i = 0; i < names.Length; i++)
                snapshot[i] = (names[i], Environment.GetEnvironmentVariable(names[i]));
        }

        public void Dispose() {
            foreach (var (name, value) in snapshot)
                Environment.SetEnvironmentVariable(name, value);
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
        public ConcurrentQueue<RequestRecord> Received { get; } = new();

        public int CountEvents(string eventName) {
            return Received.Count(record => string.Equals(record.Event, eventName, StringComparison.Ordinal));
        }

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

                    Received.Enqueue(ParseRequest(body));

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

        private static RequestRecord ParseRequest(string body) {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            return new RequestRecord(
                Event: root.GetProperty("event").GetString() ?? string.Empty,
                Tool: root.GetProperty("tool").GetString() ?? string.Empty,
                Body: body);
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

        public readonly record struct RequestRecord(string Event, string Tool, string Body);
    }
}

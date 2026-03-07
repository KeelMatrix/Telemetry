// Copyright (c) KeelMatrix

using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using KeelMatrix.Telemetry.Infrastructure;
using KeelMatrix.Telemetry.ProjectIdentity;

namespace KeelMatrix.Telemetry.IntegrationTests;

[Collection(TelemetryDeliveryWorkerIntegrationTestsCollectionDefinition.Name)]
public sealed class ProjectIdentityFailureIntegrationTests {
    [Fact]
    public async Task TrackActivation_WithBrokenProjectIdentity_EmitsNothing_AndDisablesTelemetry() {
        using var harness = new BrokenIdentityHarness();
        using var clientScope = harness.CreateClientScope();

        clientScope.Client.TrackActivation();

        await Task.Delay(300);

        harness.Server.Received.Should().BeEmpty();
        harness.CountQueueFiles().Should().Be(0);
        harness.CountMarkerFiles("activation.*.json").Should().Be(0);
        harness.CountMarkerFiles($"heartbeat.*.{harness.CurrentWeek}.json").Should().Be(0);
        TelemetryConfig.IsTelemetryDisabled().Should().BeTrue();
    }

    [Fact]
    public async Task TrackHeartbeat_WithBrokenProjectIdentity_EmitsNothing_AndDisablesTelemetry() {
        using var harness = new BrokenIdentityHarness();
        using var clientScope = harness.CreateClientScope();

        clientScope.Client.TrackHeartbeat();

        await Task.Delay(300);

        harness.Server.Received.Should().BeEmpty();
        harness.CountQueueFiles().Should().Be(0);
        harness.CountMarkerFiles("activation.*.json").Should().Be(0);
        harness.CountMarkerFiles($"heartbeat.*.{harness.CurrentWeek}.json").Should().Be(0);
        TelemetryConfig.IsTelemetryDisabled().Should().BeTrue();
    }

    [Fact]
    public async Task BrokenProjectIdentity_DisablesTelemetryBeforeBacklogCanSend() {
        using var harness = new BrokenIdentityHarness();
        var queue = harness.CreateQueue();

        queue.Enqueue("{\"event\":\"backlog\",\"tool\":\"test\"}");
        harness.CountQueueFiles().Should().Be(1);

        using var clientScope = harness.CreateClientScope();
        clientScope.Client.TrackActivation();

        await Task.Delay(300);

        harness.Server.Received.Should().BeEmpty();
        harness.CountQueueFiles().Should().Be(1);
        TelemetryConfig.IsTelemetryDisabled().Should().BeTrue();
    }

    private sealed class BrokenIdentityHarness : IDisposable {
        private readonly EnvVarScope env;
        private readonly string rootDir;
        private readonly string toolNameUpper;

        public BrokenIdentityHarness() {
            env = new EnvVarScope(
                "KEELMATRIX_NO_TELEMETRY",
                "DOTNET_CLI_TELEMETRY_OPTOUT",
                "DO_NOT_TRACK");

            env.Clear();
            ResetProcessDisabledForTests();

            Server = new LocalTelemetryServer();
            TelemetryConfig.SetUrlOverrideForTests(Server.BaseUri);

            toolNameUpper = "BROKENIDENTITY_" + Guid.NewGuid().ToString("N");
            RuntimeContext = new TelemetryRuntimeContext(toolNameUpper, typeof(ProjectIdentityFailureIntegrationTests));
            RuntimeInfo = new RuntimeInfo();
            RuntimeContext.EnsureRootDirectoryResolvedOnWorkerThread();
            rootDir = RuntimeContext.GetRootDirectory();
            TryDeleteDirectory(rootDir);
        }

        public LocalTelemetryServer Server { get; }
        public TelemetryRuntimeContext RuntimeContext { get; }
        public RuntimeInfo RuntimeInfo { get; }
        public string CurrentWeek => TelemetryClock.GetCurrentIsoWeek();
        public string MarkersDir => Path.Combine(rootDir, "markers");
        public string PendingDir => Path.Combine(rootDir, "telemetry.queue", "pending");
        public string ProcessingDir => Path.Combine(rootDir, "telemetry.queue", "processing");

        public ITelemetryQueue CreateQueue() {
            return DurableTelemetryQueue.CreateSafe(RuntimeContext);
        }

        public ClientScope CreateClientScope() {
            var client = new Client(
                toolNameUpper,
                typeof(ProjectIdentityFailureIntegrationTests),
                (_, _) => new ThrowingProjectIdentityProvider());

            return new ClientScope(client);
        }

        public int CountQueueFiles() {
            return CountFiles(PendingDir, "*.json") + CountFiles(ProcessingDir, "*.json");
        }

        public int CountMarkerFiles(string pattern) {
            return CountFiles(MarkersDir, pattern);
        }

        public void Dispose() {
            try { TelemetryConfig.SetUrlOverrideForTests(null); } catch { /* swallow */ }
            Server.Dispose();
            env.Dispose();
            TryDeleteDirectory(rootDir);
        }

        private static int CountFiles(string dir, string pattern) {
            if (!Directory.Exists(dir))
                return 0;

            return Directory.EnumerateFiles(dir, pattern).Count();
        }

        private static void ResetProcessDisabledForTests() {
            var field = typeof(TelemetryConfig).GetField("processDisabled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (field is not null)
                field.SetValue(null, 0);
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
    }

    private sealed class ClientScope : IDisposable {
        public ClientScope(Client client) {
            Client = client;
        }

        public Client Client { get; }

        public void Dispose() {
            var innerField = typeof(Client).GetField("client", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var inner = innerField?.GetValue(Client);
            if (inner is not TelemetryClient telemetryClient)
                return;

            var workerField = typeof(TelemetryClient).GetField("worker", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var worker = workerField?.GetValue(telemetryClient) as TelemetryDeliveryWorker;
            if (worker is not null) {
                try { worker.Dispose(); } catch { /* swallow */ }
            }
        }
    }

    private sealed class ThrowingProjectIdentityProvider : IProjectIdentityProvider {
        public string EnsureComputedOnWorkerThread() {
            throw new IOException("Simulated project identity failure.");
        }
    }

    private sealed class EnvVarScope : IDisposable {
        private readonly Dictionary<string, string?> snapshot = new(StringComparer.Ordinal);

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
        public List<string> Received { get; } = [];

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
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
                    var body = await reader.ReadToEndAsync().ConfigureAwait(false);
                    Received.Add(body);

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

        private static int GetFreeTcpPort() {
            var tcpListener = new TcpListener(IPAddress.Loopback, 0);
            tcpListener.Start();
            int port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
            tcpListener.Stop();
            return port;
        }

        public void Dispose() {
            try { cts.Cancel(); cts.Dispose(); } catch { /* swallow */ }
            try { listener.Stop(); } catch { /* swallow */ }
            try { listener.Close(); } catch { /* swallow */ }
            try { loop.Wait(TimeSpan.FromSeconds(1)); } catch { /* swallow */ }
        }
    }
}

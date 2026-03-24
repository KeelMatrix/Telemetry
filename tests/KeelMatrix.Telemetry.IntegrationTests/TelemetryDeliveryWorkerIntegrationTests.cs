// Copyright (c) KeelMatrix

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using KeelMatrix.Telemetry.Infrastructure;
using KeelMatrix.Telemetry.ProjectIdentity;

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

        harness.Sender.Received.Count.Should().Be(0);
        File.Exists(backlogPath).Should().BeTrue();

        if (Directory.Exists(harness.ProcessingDir))
            Directory.EnumerateFiles(harness.ProcessingDir, "*.json").Should().BeEmpty();
    }

    [Fact]
    public async Task ActivationPlanning_EnqueuesEventAndCommitsActivationMarker() {
        using var harness = new WorkerHarness();
        using var worker = harness.CreateWorker();

        worker.RequestActivation();

        await WaitUntilAsync(() => harness.Sender.Received.Any(r => r.Event == "activation"), TimeSpan.FromSeconds(5));
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

        await WaitUntilAsync(() => harness.Sender.Received.Any(r => r.Event == "activation"), TimeSpan.FromSeconds(5));
        await Task.Delay(250);

        var events = harness.Sender.Received.Select(r => r.Event).ToList();
        events.Should().Contain("activation");
        events.Should().NotContain("heartbeat", "activation should suppress heartbeat for the same week");

        worker.RequestHeartbeat();
        await Task.Delay(250);

        harness.Sender.Received.Select(r => r.Event).Should().NotContain("heartbeat");
    }

    [Fact]
    public async Task HeartbeatPlanning_EnqueuesAndCommitsHeartbeatMarker_WhenNotSuppressed() {
        using var harness = new WorkerHarness();
        using var worker = harness.CreateWorker();

        worker.RequestHeartbeat();

        await WaitUntilAsync(() => harness.Sender.Received.Any(r => r.Event == "heartbeat"), TimeSpan.FromSeconds(5));

        Directory.EnumerateFiles(harness.MarkersDir, $"heartbeat.*.{harness.CurrentWeek}.json").Should().NotBeEmpty();
    }

    [Fact]
    public async Task ActivationPayload_ContainsProjectHashAndInstallationHash() {
        var projectDir = CreateGitRepoRoot("worker-activation-payload", "https://github.com/KeelMatrix/Telemetry.git");

        using var overrideScope = new StartingPointsOverrideScope(projectDir);
        using var harness = new WorkerHarness();
        using var worker = harness.CreateWorker();

        worker.RequestActivation();

        await WaitUntilAsync(() => harness.Sender.Received.Any(r => r.Event == "activation"), TimeSpan.FromSeconds(5));

        using var doc = JsonDocument.Parse(harness.Sender.Received.Single(r => r.Event == "activation").Body);
        doc.RootElement.GetProperty("project_hash").GetString().Should().NotBeNullOrWhiteSpace();
        doc.RootElement.GetProperty("installation_hash").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task HeartbeatPayload_ContainsProjectHashAndInstallationHash() {
        var projectDir = CreateGitRepoRoot("worker-heartbeat-payload", "https://github.com/KeelMatrix/Telemetry.git");

        using var overrideScope = new StartingPointsOverrideScope(projectDir);
        using var harness = new WorkerHarness();
        using var worker = harness.CreateWorker();

        worker.RequestHeartbeat();

        await WaitUntilAsync(() => harness.Sender.Received.Any(r => r.Event == "heartbeat"), TimeSpan.FromSeconds(5));

        using var doc = JsonDocument.Parse(harness.Sender.Received.Single(r => r.Event == "heartbeat").Body);
        doc.RootElement.GetProperty("project_hash").GetString().Should().NotBeNullOrWhiteSpace();
        doc.RootElement.GetProperty("installation_hash").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ActivationAndHeartbeat_UseSameProjectHashResolution_ForSameRepo() {
        var projectDir = CreateGitRepoRoot("worker-shared-project-hash", "https://github.com/KeelMatrix/Telemetry.git");

        using var overrideScope = new StartingPointsOverrideScope(projectDir);
        using var activationHarness = new WorkerHarness();
        using var activationWorker = activationHarness.CreateWorker();

        activationWorker.RequestActivation();
        await WaitUntilAsync(() => activationHarness.Sender.Received.Any(r => r.Event == "activation"), TimeSpan.FromSeconds(5));

        using var heartbeatHarness = new WorkerHarness();
        using var heartbeatWorker = heartbeatHarness.CreateWorker();

        heartbeatWorker.RequestHeartbeat();
        await WaitUntilAsync(() => heartbeatHarness.Sender.Received.Any(r => r.Event == "heartbeat"), TimeSpan.FromSeconds(5));

        var activationProjectHash = ReadHashField(
            activationHarness.Sender.Received.Single(r => r.Event == "activation").Body,
            "project_hash");
        var heartbeatProjectHash = ReadHashField(
            heartbeatHarness.Sender.Received.Single(r => r.Event == "heartbeat").Body,
            "project_hash");

        activationProjectHash.Should().Be(heartbeatProjectHash);
    }

    [Fact]
    public async Task RequestsAreSuppressed_WhenStableProjectIdentityIsUnavailable() {
        var emptyDir = CreateEmptyIdentityRoot("worker-no-project-identity");

        using var overrideScope = new StartingPointsOverrideScope(emptyDir);
        using var harness = new WorkerHarness();
        harness.RuntimeInfo.SetCiOverrideForTests(false);
        using var worker = harness.CreateWorker();

        worker.RequestActivation();
        worker.RequestHeartbeat();

        await Task.Delay(300);

        harness.Sender.Received.Should().BeEmpty();
        harness.CountMarkerFiles("activation.*.json").Should().Be(0);
        harness.CountMarkerFiles($"heartbeat.*.{harness.CurrentWeek}.json").Should().Be(0);
        TelemetryConfig.IsTelemetryDisabled().Should().BeFalse();
    }

    [Fact]
    public async Task DeliveryLoop_ClaimsSendsCompletesOrAbandons() {
        using var harness = new WorkerHarness();
        var queue = harness.CreateQueue();

        for (int i = 0; i < 6; i++)
            queue.Enqueue($"{{\"i\":{i}}}");

        using var worker = harness.CreateWorker();

        await WaitUntilAsync(() => harness.Sender.Received.Count >= 6, TimeSpan.FromSeconds(10));
        await WaitUntilAsync(
            () => !Directory.Exists(harness.PendingDir) || !Directory.EnumerateFiles(harness.PendingDir, "*.json").Any(),
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => !Directory.Exists(harness.ProcessingDir) || !Directory.EnumerateFiles(harness.ProcessingDir, "*.json").Any(),
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

    private static string ReadHashField(string json, string fieldName) {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty(fieldName).GetString() ?? string.Empty;
    }

    private static string CreateGitRepoRoot(string name, string originRemoteUrl) {
        var repoDir = Path.Combine(Path.GetTempPath(), "KeelMatrix.Telemetry.WorkerIdentityTests", name + "." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        File.WriteAllText(
            Path.Combine(repoDir, ".git", "config"),
            """
            [remote "origin"]
                url = PLACEHOLDER_REMOTE
            """
                .Replace("PLACEHOLDER_REMOTE", originRemoteUrl, StringComparison.Ordinal));
        return repoDir;
    }

    private static string CreateEmptyIdentityRoot(string name) {
        var root = Path.Combine(Path.GetTempPath(), "KeelMatrix.Telemetry.WorkerIdentityTests", name + "." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class StartingPointsOverrideScope : IDisposable {
        public StartingPointsOverrideScope(params string[] startingPoints) {
            GitDiscovery.SetStartingPointsOverrideForTests(startingPoints);
        }

        public void Dispose() {
            GitDiscovery.SetStartingPointsOverrideForTests(null);
        }
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

            Sender = new RecordingTelemetrySender();

            var toolNameUpper = "INTEGRATIONTEST_WORKER_" + Guid.NewGuid().ToString("N");
            RuntimeContext = new TelemetryRuntimeContext(toolNameUpper, typeof(TelemetryDeliveryWorkerIntegrationTests));
            RuntimeInfo = new RuntimeInfo();

            RuntimeContext.EnsureRootDirectoryResolvedOnWorkerThread();
            rootDir = RuntimeContext.GetRootDirectory();
            TryDeleteDirectory(rootDir);
        }

        public RecordingTelemetrySender Sender { get; }
        public TelemetryRuntimeContext RuntimeContext { get; }
        public RuntimeInfo RuntimeInfo { get; }
        public string PendingDir => Path.Combine(rootDir, "telemetry.queue", "pending");
        public string ProcessingDir => Path.Combine(rootDir, "telemetry.queue", "processing");
        public string MarkersDir => Path.Combine(rootDir, "markers");
#pragma warning disable S2325 // Methods and properties that don't access instance data should be static
#pragma warning disable CA1822 // Mark members as static
        public string CurrentWeek => TelemetryClock.GetCurrentIsoWeek();
#pragma warning restore CA1822, S2325

        public ITelemetryQueue CreateQueue() {
            return DurableTelemetryQueue.CreateSafe(RuntimeContext);
        }

        public int CountMarkerFiles(string pattern) {
            return CountFiles(MarkersDir, pattern);
        }

        public TelemetryDeliveryWorker CreateWorker() {
            return new TelemetryDeliveryWorker(RuntimeContext, RuntimeInfo, new ProjectIdentityProvider(RuntimeContext, RuntimeInfo), Sender);
        }

        public void Dispose() {
            Sender.Dispose();
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
            field?.SetValue(null, 0);
        }

        private static int CountFiles(string dir, string pattern) {
            if (!Directory.Exists(dir))
                return 0;

            return Directory.EnumerateFiles(dir, pattern).Count();
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

    private sealed class RecordingTelemetrySender : ITelemetrySender {
        public ConcurrentQueue<ReceivedRequest> Received { get; } = new();

        public Task<bool> TrySendAsync(string json, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            var telemetryEvent = TryExtractEventField(json) ?? string.Empty;
            Received.Enqueue(new ReceivedRequest(telemetryEvent, json));
            return Task.FromResult(true);
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

        public void Dispose() { }

        public readonly record struct ReceivedRequest(string Event, string Body);
    }
}

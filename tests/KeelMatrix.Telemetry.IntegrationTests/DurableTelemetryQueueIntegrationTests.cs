// Copyright (c) KeelMatrix

using FluentAssertions;
using KeelMatrix.Telemetry.Infrastructure;
using KeelMatrix.Telemetry.Storage;

namespace KeelMatrix.Telemetry.IntegrationTests;

// DurableTelemetryQueue and TelemetryConfig.Runtime are global/static in behavior.
// Keep these tests non-parallel.
[CollectionDefinition(Name, DisableParallelization = true)]
public static class DurableTelemetryQueueIntegrationTestsCollectionDefinition {
    public const string Name = $"{nameof(DurableTelemetryQueueIntegrationTests)}.NonParallel";
}

[Collection(DurableTelemetryQueueIntegrationTestsCollectionDefinition.Name)]
public sealed class DurableTelemetryQueueIntegrationTests {
    private const string QueueSubdir = "telemetry.queue";

    [Fact]
    public void Enqueue_CreatesPendingFile_UsingTmpThenMove() {
        using var runtime = IsolatedRuntime.Create();
        var queue = IsolatedRuntime.CreateQueue();

        queue.Enqueue("{}");

        var pendingJson = Directory.EnumerateFiles(runtime.PendingDir, "*.json").ToList();
        var pendingTmp = Directory.EnumerateFiles(runtime.PendingDir, "*.tmp").ToList();

        pendingTmp.Should().BeEmpty("tmp files must be cleaned up after atomic move");
        pendingJson.Should().HaveCount(1);

        // Ensure content is a valid envelope JSON.
        var json = File.ReadAllText(pendingJson[0]);
        var env = TelemetryEnvelope.Deserialize(json);
        env.PayloadJson.Should().Be("{}");
    }

    [Fact]
    public void TryClaim_MovesPendingToProcessing_AndReturnsEnvelope() {
        using var runtime = IsolatedRuntime.Create();
        var queue = IsolatedRuntime.CreateQueue();

        const string payload = "{\"event\":\"activation\"}";
        queue.Enqueue(payload);

        var claimed = queue.TryClaim(1).ToList();
        claimed.Should().HaveCount(1);

        var item = claimed[0];
        File.Exists(item.Path).Should().BeTrue("claimed item must exist in processing dir");

        Directory.EnumerateFiles(runtime.PendingDir, "*.json").Should().BeEmpty();
        Directory.EnumerateFiles(runtime.ProcessingDir, "*.json").Should().ContainSingle();

        item.Envelope.PayloadJson.Should().Be(payload);
        item.Envelope.Id.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TryClaim_DeletesCorruptOrTooLargeItems() {
        using var runtime = IsolatedRuntime.Create();
        _ = IsolatedRuntime.CreateQueue(); // ctor cleans tmp + sets up dirs

        // 1) Too-large envelope JSON (> 4096 chars)
        runtime.WritePendingRawText(new string('a', 4097));

        // 2) Invalid envelope JSON (valid JSON, but not a TelemetryEnvelope)
        runtime.WritePendingRawText("{}");

        // 3) Payload too large (> MaxPayloadBytes)
        var tooLargePayload = new string('x', TelemetryConfig.MaxPayloadBytes + 1);
        var env = new TelemetryEnvelope(tooLargePayload);
        runtime.WritePendingRawText(env.Serialize());

        var queue = IsolatedRuntime.CreateQueue();

        var claimed = queue.TryClaim(32).ToList();
        claimed.Should().BeEmpty();

        Directory.EnumerateFiles(runtime.PendingDir, "*.json").Should().BeEmpty();
        Directory.EnumerateFiles(runtime.ProcessingDir, "*.json").Should().BeEmpty();
    }

    [Fact]
    public void CrashRecovery_MovesStaleProcessingBackToPending() {
        using var runtime = IsolatedRuntime.Create();
        _ = IsolatedRuntime.CreateQueue(); // ensure dirs exist

        var env = new TelemetryEnvelope("{}");
        var processingPath = Path.Combine(runtime.ProcessingDir, $"{env.Id}.json");
        File.WriteAllText(processingPath, env.Serialize());

        // Mark as stale.
        var staleUtc = DateTime.UtcNow - TelemetryConfig.ProcessingStaleThreshold - TimeSpan.FromMinutes(1);
        File.SetLastWriteTimeUtc(processingPath, staleUtc);

        // New instance should run CrashRecovery in ctor.
        _ = IsolatedRuntime.CreateQueue();

        File.Exists(processingPath).Should().BeFalse();
        var pendingPath = Path.Combine(runtime.PendingDir, Path.GetFileName(processingPath));
        File.Exists(pendingPath).Should().BeTrue();
    }

    [Fact]
    public void Abandon_RequeuesWithAttemptsIncremented() {
        using var runtime = IsolatedRuntime.Create();
        var queue = IsolatedRuntime.CreateQueue();

        const string payload = "{}";
        queue.Enqueue(payload);

        var item = queue.TryClaim(1).Single();
        item.Envelope.Attempts.Should().Be(0);

        queue.Abandon(item);

        File.Exists(item.Path).Should().BeFalse("processing file should be deleted only after requeue succeeds");

        var pendingFiles = Directory.EnumerateFiles(runtime.PendingDir, "*.json").ToList();
        pendingFiles.Should().HaveCount(1);

        var reread = TelemetryEnvelope.Deserialize(File.ReadAllText(pendingFiles[0]));
        reread.PayloadJson.Should().Be(payload);
        reread.Attempts.Should().Be(1);
        reread.Id.Should().Be(item.Envelope.Id);
    }

    [Fact]
    public void Abandon_MovesToDeadLetter_WhenAttemptsWouldReachMax() {
        using var runtime = IsolatedRuntime.Create();
        _ = IsolatedRuntime.CreateQueue();

        // Write an envelope already at MaxSendAttempts - 1, so Abandon triggers dead-letter.
        var env = new TelemetryEnvelope("{}") { Attempts = TelemetryConfig.MaxSendAttempts - 1 };
        runtime.WritePendingRawText(env.Serialize());

        var queue = IsolatedRuntime.CreateQueue();
        var item = queue.TryClaim(1).Single();

        queue.Abandon(item);

        Directory.EnumerateFiles(runtime.PendingDir, "*.json").Should().BeEmpty();
        Directory.EnumerateFiles(runtime.ProcessingDir, "*.json").Should().BeEmpty();
        Directory.EnumerateFiles(runtime.DeadDir, "*.json").Should().ContainSingle();
    }

    [Fact]
    public void Complete_DeletesProcessingItem() {
        using var runtime = IsolatedRuntime.Create();
        var queue = IsolatedRuntime.CreateQueue();

        queue.Enqueue("{}");

        var item = queue.TryClaim(1).Single();
        File.Exists(item.Path).Should().BeTrue();

        queue.Complete(item);

        File.Exists(item.Path).Should().BeFalse();
    }

    [Fact]
    public void EnforceLimit_DeletesOldestBeyondPendingAndDeadLetterCaps() {
        using var runtime = IsolatedRuntime.Create();
        _ = IsolatedRuntime.CreateQueue();

        IsolatedRuntime.Prepopulate(runtime.PendingDir, TelemetryConfig.MaxPendingItems + 7, prefixOld: "oldp_", prefixNew: "newp_");
        IsolatedRuntime.Prepopulate(runtime.DeadDir, TelemetryConfig.MaxDeadLetterItems + 7, prefixOld: "oldd_", prefixNew: "newd_");

        var queue = IsolatedRuntime.CreateQueue();

        // Trigger EnforceLimit.
        queue.Enqueue("{}");

        Directory.EnumerateFiles(runtime.PendingDir, "*.json").Count().Should().BeLessOrEqualTo(TelemetryConfig.MaxPendingItems);
        Directory.EnumerateFiles(runtime.DeadDir, "*.json").Count().Should().BeLessOrEqualTo(TelemetryConfig.MaxDeadLetterItems);

        // Oldest prefixes should be removed.
        Directory.EnumerateFiles(runtime.PendingDir, "*.json").Any(p => Path.GetFileName(p).StartsWith("oldp_", StringComparison.Ordinal)).Should().BeFalse();
        Directory.EnumerateFiles(runtime.DeadDir, "*.json").Any(p => Path.GetFileName(p).StartsWith("oldd_", StringComparison.Ordinal)).Should().BeFalse();

        // Newer prefixes should remain.
        Directory.EnumerateFiles(runtime.PendingDir, "*.json").Any(p => Path.GetFileName(p).StartsWith("newp_", StringComparison.Ordinal)).Should().BeTrue();
        Directory.EnumerateFiles(runtime.DeadDir, "*.json").Any(p => Path.GetFileName(p).StartsWith("newd_", StringComparison.Ordinal)).Should().BeTrue();
    }

    private sealed class IsolatedRuntime : IDisposable {
        public string RootDir { get; }
        public string QueueRootDir { get; }
        public string PendingDir { get; }
        public string ProcessingDir { get; }
        public string DeadDir { get; }

        private IsolatedRuntime(string toolNameUpper) {
            TelemetryConfig.Runtime.Set(toolNameUpper, typeof(DurableTelemetryQueueIntegrationTests));
            TelemetryConfig.Runtime.EnsureRootDirectoryResolvedOnWorkerThread();

            RootDir = TelemetryConfig.Runtime.GetRootDirectory();

            // Ensure per-test root is clean.
            TryDeleteDirectory(RootDir);

            QueueRootDir = Path.Combine(RootDir, QueueSubdir);
            PendingDir = Path.Combine(QueueRootDir, "pending");
            ProcessingDir = Path.Combine(QueueRootDir, "processing");
            DeadDir = Path.Combine(QueueRootDir, "dead");

            Directory.CreateDirectory(PendingDir);
            Directory.CreateDirectory(ProcessingDir);
            Directory.CreateDirectory(DeadDir);
        }

        public static IsolatedRuntime Create() {
            var toolNameUpper = "INTEGRATIONTEST_" + Guid.NewGuid().ToString("N");
            return new IsolatedRuntime(toolNameUpper);
        }

        public static ITelemetryQueue CreateQueue() {
            // DurableTelemetryQueue has a private ctor; create via reflection for full isolation.
            var t = typeof(DurableTelemetryQueue);
            var instance = Activator.CreateInstance(t, nonPublic: true);
            instance.Should().NotBeNull();
            return (ITelemetryQueue)instance!;
        }

        public void WritePendingRawText(string rawText) {
            Directory.CreateDirectory(PendingDir);
            var path = Path.Combine(PendingDir, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, rawText);
        }

        public static void Prepopulate(string dir, int count, string prefixOld, string prefixNew) {
            Directory.CreateDirectory(dir);

            var baseUtc = DateTime.UtcNow.AddHours(-2);

            for (int i = 0; i < count; i++) {
                // Ensure some files are clearly the oldest.
                var prefix = i < 7 ? prefixOld : prefixNew;
                var name = $"{prefix}{i:D5}.json";
                var path = Path.Combine(dir, name);

                var env = new TelemetryEnvelope("{}");
                File.WriteAllText(path, env.Serialize());

                // Creation time ordering is what queue uses today. Best-effort set.
                var t = baseUtc.AddSeconds(i);
                TrySetTimesUtc(path, t);
            }
        }

        private static void TrySetTimesUtc(string path, DateTime utc) {
            try { File.SetCreationTimeUtc(path, utc); } catch { /* swallow */ }
            try { File.SetLastWriteTimeUtc(path, utc); } catch { /* swallow */ }
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

        public void Dispose() {
            // Best-effort cleanup of the per-test root.
            TryDeleteDirectory(RootDir);
        }
    }
}

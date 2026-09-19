// Copyright (c) KeelMatrix

using System.Globalization;
using FluentAssertions;
using KeelMatrix.Telemetry.Infrastructure;
using KeelMatrix.Telemetry.Storage;

namespace KeelMatrix.Telemetry.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public static class DurableTelemetryQueueIntegrationTestsCollectionDefinition {
    public const string Name = $"{nameof(DurableTelemetryQueueIntegrationTests)}.NonParallel";
}

[Collection(TelemetryDeliveryWorkerIntegrationTestsCollectionDefinition.Name)]
public sealed class DurableTelemetryQueueIntegrationTests {
    private const string QueueFileTimestampFormat = "yyyyMMddHHmmssfffffff";

    [Fact]
    public void CreateSafe_ReturnsNoQueue_WhenPendingPathIsRegularFile() {
        using var runtime = TestRuntimeScope.Create(typeof(DurableTelemetryQueueIntegrationTests));
        Directory.CreateDirectory(runtime.QueueRootDir);
        File.WriteAllText(runtime.PendingDir, "blocked");

        DurableTelemetryQueue.CreateSafe(runtime.RuntimeContext).Should().BeNull();
    }

    [Fact]
    public void Enqueue_CreatesPendingFile_UsingTmpThenMove() {
        using var runtime = TestRuntimeScope.Create(typeof(DurableTelemetryQueueIntegrationTests));
        var queue = runtime.CreateQueue();

        queue.Enqueue("{}").Should().BeTrue();

        var pendingJson = Directory.EnumerateFiles(runtime.PendingDir, "*.json").ToList();
        var pendingTmp = Directory.EnumerateFiles(runtime.PendingDir, "*.tmp").ToList();

        pendingTmp.Should().BeEmpty("tmp files must be cleaned up after atomic move");
        pendingJson.Should().HaveCount(1);

        var json = File.ReadAllText(pendingJson[0]);
        var env = TelemetryEnvelope.Deserialize(json);
        env.PayloadJson.Should().Be("{}");
    }

    [Fact]
    public void TryClaim_MovesPendingToProcessing_AndReturnsEnvelope() {
        using var runtime = TestRuntimeScope.Create(typeof(DurableTelemetryQueueIntegrationTests));
        var queue = runtime.CreateQueue();

        const string payload = "{\"event\":\"activation\"}";
        queue.Enqueue(payload).Should().BeTrue();

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
        using var runtime = TestRuntimeScope.Create(typeof(DurableTelemetryQueueIntegrationTests));
        _ = runtime.CreateQueue();

        WritePendingRawText(runtime.PendingDir, new string('a', 4097), "too-large-envelope");
        WritePendingRawText(runtime.PendingDir, "{}", "invalid-envelope");

        var tooLargePayload = new string('x', TelemetryConfig.MaxPayloadBytes + 1);
        var envelope = new TelemetryEnvelope(tooLargePayload);
        WritePendingRawText(runtime.PendingDir, envelope.Serialize(), "too-large-payload");

        var queue = runtime.CreateQueue();

        var claimed = queue.TryClaim(32).ToList();
        claimed.Should().BeEmpty();

        Directory.EnumerateFiles(runtime.PendingDir, "*.json").Should().BeEmpty();
        Directory.EnumerateFiles(runtime.ProcessingDir, "*.json").Should().BeEmpty();
    }

    [Fact]
    public void CrashRecovery_MovesStaleProcessingBackToPending() {
        using var runtime = TestRuntimeScope.Create(typeof(DurableTelemetryQueueIntegrationTests));
        var queue = runtime.CreateQueue();
        queue.Enqueue("{\"event\":\"crash-recovery\"}");
        var claimed = queue.TryClaim(1).Single();

        var staleUtc = DateTime.UtcNow - TelemetryConfig.ProcessingStaleThreshold - TimeSpan.FromMinutes(1);
        File.SetLastWriteTimeUtc(claimed.Path, staleUtc);

        var recoveredQueue = runtime.CreateQueue();
        var pendingPath = Path.Combine(runtime.PendingDir, Path.GetFileName(claimed.Path));
        File.Exists(pendingPath).Should().BeTrue();
        var recovered = recoveredQueue.TryClaim(1).Single();

        recovered.Envelope.PayloadJson.Should().Be("{\"event\":\"crash-recovery\"}");
    }

    [Fact]
    public void TryClaim_DoesNotReclaimYoungClaim_ButLaterReclaimsAfterItBecomesStale() {
        using var runtime = TestRuntimeScope.Create(typeof(DurableTelemetryQueueIntegrationTests));
        var queue = runtime.CreateQueue();
        queue.Enqueue("{\"event\":\"delayed-recovery\"}").Should().BeTrue();

        var claimed = queue.TryClaim(1).Single();
        File.SetLastWriteTimeUtc(claimed.Path, DateTime.UtcNow);

        queue.TryClaim(1).Should().BeEmpty();

        File.SetLastWriteTimeUtc(
            claimed.Path,
            DateTime.UtcNow - TelemetryConfig.ProcessingStaleThreshold - TimeSpan.FromMinutes(1));

        var recovered = queue.TryClaim(1).Single();
        recovered.Envelope.PayloadJson.Should().Be("{\"event\":\"delayed-recovery\"}");
    }

    [Fact]
    public void TryClaim_SkipsCorruptPrefixAndClaimsLaterValidEnvelope() {
        using var runtime = TestRuntimeScope.Create(typeof(DurableTelemetryQueueIntegrationTests));
        _ = runtime.CreateQueue();

        var baseUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        for (int i = 0; i < 4; i++) {
            var corruptPath = CreateQueueFilePath(runtime.PendingDir, baseUtc.AddSeconds(i), $"corrupt_{i}");
            File.WriteAllText(corruptPath, "not-json");
        }

        var validPath = CreateQueueFilePath(runtime.PendingDir, baseUtc.AddSeconds(4), "valid");
        File.WriteAllText(validPath, new TelemetryEnvelope("{\"event\":\"valid\"}").Serialize());

        var queue = runtime.CreateQueue();
        var claimed = queue.TryClaim(1).Single();

        claimed.Envelope.PayloadJson.Should().Be("{\"event\":\"valid\"}");
        Directory.EnumerateFiles(runtime.PendingDir, "*.json").Should().BeEmpty();
    }

    [Fact]
    public void QueueInitialization_DoesNotDeleteAnotherProcessActiveTempFile() {
        using var runtime = TestRuntimeScope.Create(typeof(DurableTelemetryQueueIntegrationTests));
        _ = runtime.CreateQueue();

        var activeTempPath = Path.Combine(runtime.PendingDir, "202609190000000000000_active.tmp");
        using (var writer = new FileStream(activeTempPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
            writer.WriteByte((byte)'{');
            File.SetLastWriteTimeUtc(
                activeTempPath,
                DateTime.UtcNow - TelemetryConfig.ProcessingStaleThreshold - TimeSpan.FromMinutes(1));
            _ = runtime.CreateQueue();
            File.Exists(activeTempPath).Should().BeTrue();
        }
    }

    [Fact]
    public void Abandon_RequeuesWithAttemptsIncremented() {
        using var runtime = TestRuntimeScope.Create(typeof(DurableTelemetryQueueIntegrationTests));
        var queue = runtime.CreateQueue();

        const string payload = "{}";
        queue.Enqueue(payload).Should().BeTrue();

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
        using var runtime = TestRuntimeScope.Create(typeof(DurableTelemetryQueueIntegrationTests));
        _ = runtime.CreateQueue();

        var envelope = new TelemetryEnvelope("{}") { Attempts = TelemetryConfig.MaxSendAttempts - 1 };
        WritePendingRawText(runtime.PendingDir, envelope.Serialize(), "max-attempts");

        var queue = runtime.CreateQueue();
        var item = queue.TryClaim(1).Single();

        queue.Abandon(item);

        Directory.EnumerateFiles(runtime.PendingDir, "*.json").Should().BeEmpty();
        Directory.EnumerateFiles(runtime.ProcessingDir, "*.json").Should().BeEmpty();
        Directory.EnumerateFiles(runtime.DeadDir, "*.json").Should().ContainSingle();
    }

    [Fact]
    public void Abandon_PreservesProcessingItem_WhenRequeueCannotBeWritten() {
        using var runtime = TestRuntimeScope.Create(typeof(DurableTelemetryQueueIntegrationTests));
        var queue = runtime.CreateQueue();
        queue.Enqueue("{\"event\":\"preserve\"}");
        var item = queue.TryClaim(1).Single();

        Directory.Delete(runtime.PendingDir);
        File.WriteAllText(runtime.PendingDir, "blocked");

        queue.Abandon(item);

        File.Exists(item.Path).Should().BeTrue();
    }

    [Fact]
    public void Complete_DeletesProcessingItem() {
        using var runtime = TestRuntimeScope.Create(typeof(DurableTelemetryQueueIntegrationTests));
        var queue = runtime.CreateQueue();

        queue.Enqueue("{}").Should().BeTrue();

        var item = queue.TryClaim(1).Single();
        File.Exists(item.Path).Should().BeTrue();

        queue.Complete(item);

        File.Exists(item.Path).Should().BeFalse();
    }

    [Fact]
    public void EnforceLimit_DeletesOldestBeyondPendingAndDeadLetterCaps() {
        using var runtime = TestRuntimeScope.Create(typeof(DurableTelemetryQueueIntegrationTests));
        _ = runtime.CreateQueue();

        Prepopulate(runtime.PendingDir, TelemetryConfig.MaxPendingItems + 7, prefixOld: "oldp_", prefixNew: "newp_");
        Prepopulate(runtime.DeadDir, TelemetryConfig.MaxDeadLetterItems + 7, prefixOld: "oldd_", prefixNew: "newd_");

        var queue = runtime.CreateQueue();
        queue.Enqueue("{}").Should().BeTrue();

        Directory.EnumerateFiles(runtime.PendingDir, "*.json").Count().Should().BeLessOrEqualTo(TelemetryConfig.MaxPendingItems);
        Directory.EnumerateFiles(runtime.DeadDir, "*.json").Count().Should().BeLessOrEqualTo(TelemetryConfig.MaxDeadLetterItems);

        Directory.EnumerateFiles(runtime.PendingDir, "*.json")
            .Any(path => Path.GetFileName(path).Contains("_oldp_", StringComparison.Ordinal))
            .Should().BeFalse();
        Directory.EnumerateFiles(runtime.DeadDir, "*.json")
            .Any(path => Path.GetFileName(path).Contains("_oldd_", StringComparison.Ordinal))
            .Should().BeFalse();

        Directory.EnumerateFiles(runtime.PendingDir, "*.json")
            .Any(path => Path.GetFileName(path).Contains("_newp_", StringComparison.Ordinal))
            .Should().BeTrue();
        Directory.EnumerateFiles(runtime.DeadDir, "*.json")
            .Any(path => Path.GetFileName(path).Contains("_newd_", StringComparison.Ordinal))
            .Should().BeTrue();
    }

    private static void WritePendingRawText(string dir, string rawText, string suffix) {
        Directory.CreateDirectory(dir);
        var path = CreateQueueFilePath(dir, DateTimeOffset.UtcNow, suffix);
        File.WriteAllText(path, rawText);
    }

    private static void Prepopulate(string dir, int count, string prefixOld, string prefixNew) {
        Directory.CreateDirectory(dir);

        var baseUtc = DateTimeOffset.UtcNow.AddHours(-2);

        for (int i = 0; i < count; i++) {
            var prefix = i < 7 ? prefixOld : prefixNew;
            var path = CreateQueueFilePath(dir, baseUtc.AddSeconds(i), $"{prefix}{i:D5}");

            var envelope = new TelemetryEnvelope("{}");
            File.WriteAllText(path, envelope.Serialize());
        }
    }

    private static string CreateQueueFilePath(string dir, DateTimeOffset timestampUtc, string suffix) {
        var fileName = string.Concat(
            timestampUtc.UtcDateTime.ToString(QueueFileTimestampFormat, CultureInfo.InvariantCulture),
            "_",
            suffix,
            ".json");

        return Path.Combine(dir, fileName);
    }
}

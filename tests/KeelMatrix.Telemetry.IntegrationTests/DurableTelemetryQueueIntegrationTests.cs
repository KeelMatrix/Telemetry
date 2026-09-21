// Copyright (c) KeelMatrix

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
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
    private const string QueueProcessRoleVariable = "KEELMATRIX_QUEUE_PROCESS_ROLE";
    private const string QueueProcessToolVariable = "KEELMATRIX_QUEUE_PROCESS_TOOL";
    private const string QueueProcessSignalVariable = "KEELMATRIX_QUEUE_PROCESS_SIGNAL";
    private const string QueueProcessReleaseVariable = "KEELMATRIX_QUEUE_PROCESS_RELEASE";
    private const string QueueProcessOutputVariable = "KEELMATRIX_QUEUE_PROCESS_OUTPUT";
    private const string QueueProcessProducerIndexVariable = "KEELMATRIX_QUEUE_PROCESS_PRODUCER_INDEX";
    private const string QueueProcessProducerCountVariable = "KEELMATRIX_QUEUE_PROCESS_PRODUCER_COUNT";
    private const string QueueProcessItemsPerProducerVariable = "KEELMATRIX_QUEUE_PROCESS_ITEMS_PER_PRODUCER";
    private const string QueueProcessProducersDoneVariable = "KEELMATRIX_QUEUE_PROCESS_PRODUCERS_DONE";

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
        Directory.EnumerateFiles(runtime.PendingDir, "*.claiming.lock").Should().BeEmpty();
        Path.GetFileName(pendingJson[0]).Should().MatchRegex(
            @"^\d{21}_[0-9a-f]{32}\.json$",
            "queue files must carry a UTC timestamp and envelope id");

        var json = File.ReadAllText(pendingJson[0]);
        var env = TelemetryEnvelope.Deserialize(json);
        env.PayloadJson.Should().Be("{}");
    }

    [Fact]
    public void FailedAtomicRename_DoesNotLeavePartialTempFile() {
        using var runtime = TestRuntimeScope.Create(typeof(DurableTelemetryQueueIntegrationTests));
        _ = runtime.CreateQueue();

        var target = Path.Combine(runtime.PendingDir, "202609190000000000000_failed.json");
        Directory.CreateDirectory(target);
        var method = typeof(DurableTelemetryQueue).GetMethod(
            "TryWritePendingAtomically",
            BindingFlags.Static | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        var result = (bool)method!.Invoke(null, [target, "{}"] )!;

        result.Should().BeFalse();
        Directory.EnumerateFiles(runtime.PendingDir, "*.tmp").Should().BeEmpty();
        Directory.Exists(target).Should().BeTrue();
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
        File.Exists(item.Path + ".lock").Should().BeTrue("the claim generation must have an ownership marker");

        Directory.EnumerateFiles(runtime.PendingDir, "*.json").Should().BeEmpty();
        Directory.EnumerateFiles(runtime.ProcessingDir, "*.json").Should().ContainSingle();
        Path.GetFileName(item.Path).Should().MatchRegex(
            @"^\d{21}_[0-9a-f]{32}\.claim\.[0-9a-f]{32}\.json$",
            "claims must carry a distinct generation");

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
    public void OldOwner_CompleteAndAbandon_DoNotAffectNewOwnerAfterStaleReclaim() {
        using var runtime = TestRuntimeScope.Create(typeof(DurableTelemetryQueueIntegrationTests));
        var queue = runtime.CreateQueue();
        queue.Enqueue("{\"event\":\"ownership\"}").Should().BeTrue();

        var oldOwner = queue.TryClaim(1).Single();
        File.SetLastWriteTimeUtc(
            oldOwner.Path,
            DateTime.UtcNow - TelemetryConfig.ProcessingStaleThreshold - TimeSpan.FromMinutes(1));

        var newOwner = queue.TryClaim(1).Single();
        newOwner.Path.Should().NotBe(oldOwner.Path);

        queue.Complete(oldOwner);
        File.Exists(newOwner.Path).Should().BeTrue();
        File.Exists(newOwner.Path + ".lock").Should().BeTrue();
        File.Exists(oldOwner.Path + ".lock").Should().BeFalse();

        queue.Abandon(oldOwner);
        File.Exists(newOwner.Path).Should().BeTrue();
        Directory.EnumerateFiles(runtime.PendingDir, "*.json").Should().BeEmpty();
    }

    [Fact]
    public void TryClaim_ReclaimsAfterControlledLeaseAdvance_WithoutRestart() {
        using var runtime = TestRuntimeScope.Create(typeof(DurableTelemetryQueueIntegrationTests));
        var queue = runtime.CreateQueue();
        queue.Enqueue("{\"event\":\"controlled-clock\"}").Should().BeTrue();

        var claimTime = DateTime.UtcNow;
        try {
            TelemetryClock.SetUtcNowOverrideForTests(() => claimTime);
            var firstOwner = queue.TryClaim(1).Single();

            queue.TryClaim(1).Should().BeEmpty();

            TelemetryClock.SetUtcNowOverrideForTests(
                () => claimTime + TelemetryConfig.ProcessingStaleThreshold + TimeSpan.FromSeconds(1));

            var recovered = queue.TryClaim(1).Single();
            recovered.Envelope.PayloadJson.Should().Be("{\"event\":\"controlled-clock\"}");
            recovered.Path.Should().NotBe(firstOwner.Path);
        }
        finally {
            TelemetryClock.SetUtcNowOverrideForTests(null);
        }
    }

    [Fact]
    public async Task CrossProcess_KilledClaimOwner_IsRecoveredAfterLeaseWithoutSecondRestart() {
        const string childMarker = "KEELMATRIX_QUEUE_CLAIM_CHILD";

        if (Environment.GetEnvironmentVariable(childMarker) == "1") {
            var toolName = Environment.GetEnvironmentVariable("KEELMATRIX_QUEUE_TOOL")!;
            var childSignalPath = Environment.GetEnvironmentVariable("KEELMATRIX_QUEUE_SIGNAL")!;
            var runtimeContext = new TelemetryRuntimeContext(toolName, typeof(DurableTelemetryQueueIntegrationTests));
            runtimeContext.EnsureRootDirectoryResolvedOnWorkerThread();
            var childQueue = DurableTelemetryQueue.CreateSafe(runtimeContext)!;
            var childClaim = childQueue.TryClaim(1).Single();
            var childSignalTempPath = childSignalPath + ".tmp";
            File.WriteAllLines(
                childSignalTempPath,
                [
                    childClaim.Path,
                    Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
                ]);
            File.Move(childSignalTempPath, childSignalPath);
            Thread.Sleep(Timeout.Infinite);
            return;
        }

        using var runtime = TestRuntimeScope.Create(typeof(DurableTelemetryQueueIntegrationTests));
        var queue = runtime.CreateQueue();
        queue.Enqueue("{\"event\":\"cross-process\"}").Should().BeTrue();

        var signalPath = Path.Combine(runtime.RootDir, "claim.signal");
        var startInfo = new ProcessStartInfo {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("vstest");
        startInfo.ArgumentList.Add(typeof(DurableTelemetryQueueIntegrationTests).Assembly.Location);
        startInfo.ArgumentList.Add(
            $"--TestCaseFilter:FullyQualifiedName~{typeof(DurableTelemetryQueueIntegrationTests).FullName}.{nameof(CrossProcess_KilledClaimOwner_IsRecoveredAfterLeaseWithoutSecondRestart)}");
        startInfo.Environment[childMarker] = "1";
        startInfo.Environment["KEELMATRIX_QUEUE_TOOL"] = runtime.ToolNameUpper;
        startInfo.Environment["KEELMATRIX_QUEUE_SIGNAL"] = signalPath;

        Process? child = null;
        Process? claimOwner = null;
        Task<string>? childStandardOutputTask = null;
        Task<string>? childStandardErrorTask = null;
        try {
            child = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start queue claim child process.");
            childStandardOutputTask = child.StandardOutput.ReadToEndAsync();
            childStandardErrorTask = child.StandardError.ReadToEndAsync();

            SpinWait.SpinUntil(() => File.Exists(signalPath) || child!.HasExited, TimeSpan.FromSeconds(30))
                .Should().BeTrue("the child must claim the event before it is terminated");
            child!.HasExited.Should().BeFalse();

            var claimSignal = File.ReadAllLines(signalPath);
            claimSignal.Should().HaveCount(2, "the child must publish its claim path and owner process id");
            File.Exists(claimSignal[0]).Should().BeTrue("the published claim path must identify the active claim");
            var claimOwnerProcessId = int.Parse(claimSignal[1], CultureInfo.InvariantCulture);
            claimOwner = Process.GetProcessById(claimOwnerProcessId);
            claimOwner.Id.Should().NotBe(child.Id, "the signal must identify the real claim-owner test host");
            claimOwner.HasExited.Should().BeFalse("the claim owner must be alive before termination");

            KillIfRunning(child);
            child.HasExited.Should().BeTrue();
            claimOwner.WaitForExit(10_000).Should().BeTrue("the published claim-owner process must exit before recovery");
            claimOwner.HasExited.Should().BeTrue("the published claim-owner process must be gone before recovery");
            await Task.WhenAll(childStandardOutputTask, childStandardErrorTask);

            var restartedQueue = runtime.CreateQueue();
            Directory.EnumerateFiles(runtime.PendingDir, "*.json").Should().BeEmpty();
            Directory.EnumerateFiles(runtime.ProcessingDir, "*.json").Should().ContainSingle();

            var restartTime = DateTime.UtcNow;
            TelemetryClock.SetUtcNowOverrideForTests(
                () => restartTime + TelemetryConfig.ProcessingStaleThreshold + TimeSpan.FromSeconds(1));

            var recovered = restartedQueue.TryClaim(1).Single();
            recovered.Envelope.PayloadJson.Should().Be("{\"event\":\"cross-process\"}");
        }
        finally {
            TelemetryClock.SetUtcNowOverrideForTests(null);
            if (child is not null) {
                try {
                    KillIfRunning(child);
                }
                finally {
                    if (childStandardOutputTask is not null && childStandardErrorTask is not null)
                        await Task.WhenAll(childStandardOutputTask!, childStandardErrorTask!);
                }
            }

            if (child is not null)
                child.Dispose();
            claimOwner?.Dispose();
        }
    }

    [Fact]
    public async Task SimultaneousProducersAndConsumers_DeliverEachEnvelopeOnce() {
        using var runtime = TestRuntimeScope.Create(typeof(DurableTelemetryQueueIntegrationTests));
        _ = runtime.CreateQueue();

        const int producerCount = 4;
        const int itemsPerProducer = 24;
        var produced = new ConcurrentBag<string>();
        var consumed = new ConcurrentBag<string>();
        var producersCompleted = 0;

        var producers = Enumerable.Range(0, producerCount).Select(producer => Task.Run(() => {
            var queue = runtime.CreateQueue();
            for (var item = 0; item < itemsPerProducer; item++) {
                var payload = $"{{\"producer\":{producer},\"item\":{item}}}";
                queue.Enqueue(payload).Should().BeTrue();
                produced.Add(payload);
            }

            Interlocked.Increment(ref producersCompleted);
        })).ToArray();

        var consumers = Enumerable.Range(0, 4).Select(_ => Task.Run(() => {
            var queue = runtime.CreateQueue();
            while (true) {
                var claims = queue.TryClaim(4).ToList();
                foreach (var claim in claims) {
                    consumed.Add(claim.Envelope.PayloadJson);
                    queue.Complete(claim);
                }

                if (claims.Count == 0) {
                    var producersDone = Volatile.Read(ref producersCompleted) == producerCount;
                    var pending = Directory.EnumerateFiles(runtime.PendingDir, "*.json").Any();
                    var processing = Directory.EnumerateFiles(runtime.ProcessingDir, "*.json").Any();
                    if (producersDone && !pending && !processing)
                        return;

                    Thread.Yield();
                }
            }
        })).ToArray();

        await Task.WhenAll(producers.Concat(consumers));

        produced.Should().HaveCount(producerCount * itemsPerProducer);
        consumed.Should().HaveCount(
            producerCount * itemsPerProducer,
            "missing payloads: {0}; pending={1}; processing={2}; dead={3}",
            string.Join(", ", produced.Except(consumed)),
            Directory.EnumerateFiles(runtime.PendingDir, "*.json").Count(),
            Directory.EnumerateFiles(runtime.ProcessingDir, "*.json").Count(),
            Directory.EnumerateFiles(runtime.DeadDir, "*.json").Count());
        consumed.Should().OnlyHaveUniqueItems();
        consumed.Should().BeEquivalentTo(produced);
        Directory.EnumerateFiles(runtime.PendingDir, "*.json").Should().BeEmpty();
        Directory.EnumerateFiles(runtime.ProcessingDir, "*.json").Should().BeEmpty();
    }

    [Fact]
    public async Task CrossProcess_SimultaneousProducersAndConsumers_DeliverEachEnvelopeOnce() {
        var role = Environment.GetEnvironmentVariable(QueueProcessRoleVariable);
        if (role == "producer") {
            var toolName = Environment.GetEnvironmentVariable(QueueProcessToolVariable)!;
            var producerIndex = int.Parse(Environment.GetEnvironmentVariable(QueueProcessProducerIndexVariable)!);
            var childProducerCount = int.Parse(Environment.GetEnvironmentVariable(QueueProcessProducerCountVariable)!);
            var childItemsPerProducer = int.Parse(Environment.GetEnvironmentVariable(QueueProcessItemsPerProducerVariable)!);
            var queue = DurableTelemetryQueue.CreateSafe(CreateChildRuntimeContext(toolName))!;

            for (var item = 0; item < childItemsPerProducer; item++) {
                var payload = $"{{\"producer\":{producerIndex},\"item\":{item}}}";
                queue.Enqueue(payload).Should().BeTrue();
            }

            producerIndex.Should().BeLessThan(childProducerCount);
            return;
        }

        if (role == "consumer") {
            var toolName = Environment.GetEnvironmentVariable(QueueProcessToolVariable)!;
            var outputPath = Environment.GetEnvironmentVariable(QueueProcessOutputVariable)!;
            var childProducersDonePath = Environment.GetEnvironmentVariable(QueueProcessProducersDoneVariable)!;
            var runtimeContext = CreateChildRuntimeContext(toolName);
            var queue = DurableTelemetryQueue.CreateSafe(runtimeContext)!;
            var consumed = new List<string>();

            while (true) {
                var claims = queue.TryClaim(4).ToList();
                foreach (var claim in claims) {
                    consumed.Add(claim.Envelope.PayloadJson);
                    queue.Complete(claim);
                }

                if (claims.Count == 0 &&
                    File.Exists(childProducersDonePath) &&
                    !Directory.EnumerateFiles(Path.Combine(runtimeContext.GetRootDirectory(), "telemetry.queue", "pending"), "*.json").Any() &&
                    !Directory.EnumerateFiles(Path.Combine(runtimeContext.GetRootDirectory(), "telemetry.queue", "processing"), "*.json").Any())
                    break;

                await Task.Delay(5);
            }

            File.WriteAllLines(outputPath, consumed);
            return;
        }

        using var runtime = TestRuntimeScope.Create(typeof(DurableTelemetryQueueIntegrationTests));
        _ = runtime.CreateQueue();

        const int producerCount = 3;
        const int itemsPerProducer = 24;
        const int consumerCount = 4;
        var producersDonePath = Path.Combine(runtime.RootDir, "cross-process-producers.done");
        var children = new List<Process>();
        var producerWaits = new List<Task>();
        var consumerWaits = new List<Task>();

        try {
            for (var producer = 0; producer < producerCount; producer++) {
                var child = StartQueueTestProcess(
                    nameof(CrossProcess_SimultaneousProducersAndConsumers_DeliverEachEnvelopeOnce),
                    "producer",
                    runtime.ToolNameUpper,
                    producerIndex: producer,
                    producerCount: producerCount,
                    itemsPerProducer: itemsPerProducer);
                children.Add(child);
                producerWaits.Add(WaitForChildSuccessAsync(child));
            }

            for (var consumer = 0; consumer < consumerCount; consumer++) {
                var outputPath = Path.Combine(runtime.RootDir, $"cross-process-consumer-{consumer}.txt");
                var child = StartQueueTestProcess(
                    nameof(CrossProcess_SimultaneousProducersAndConsumers_DeliverEachEnvelopeOnce),
                    "consumer",
                    runtime.ToolNameUpper,
                    outputPath: outputPath,
                    producersDonePath: producersDonePath);
                children.Add(child);
                consumerWaits.Add(WaitForChildSuccessAsync(child));
            }

            await Task.WhenAll(producerWaits);
            File.WriteAllText(producersDonePath, "done");
            await Task.WhenAll(consumerWaits);

            var consumed = children
                .Where(child => child.StartInfo.Environment[QueueProcessRoleVariable] == "consumer")
                .SelectMany(child => {
                    var outputPath = child.StartInfo.Environment[QueueProcessOutputVariable]!;
                    return File.Exists(outputPath) ? File.ReadAllLines(outputPath) : [];
                })
                .ToList();
            var expected = Enumerable.Range(0, producerCount)
                .SelectMany(producer => Enumerable.Range(0, itemsPerProducer)
                    .Select(item => $"{{\"producer\":{producer},\"item\":{item}}}"))
                .ToList();

            consumed.Should().HaveCount(expected.Count);
            consumed.Should().OnlyHaveUniqueItems();
            consumed.Should().BeEquivalentTo(expected);
            Directory.EnumerateFiles(runtime.PendingDir, "*.json").Should().BeEmpty();
            Directory.EnumerateFiles(runtime.ProcessingDir, "*.json").Should().BeEmpty();
        }
        finally {
            File.WriteAllText(producersDonePath, "done");
            foreach (var child in children) {
                KillIfRunning(child);
                child.Dispose();
            }
        }
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
    public async Task CrossProcess_QueueInitialization_DoesNotDeleteActiveProducerTempFile() {
        var role = Environment.GetEnvironmentVariable(QueueProcessRoleVariable);
        if (role == "active-temp-producer") {
            var toolName = Environment.GetEnvironmentVariable(QueueProcessToolVariable)!;
            var signalPath = Environment.GetEnvironmentVariable(QueueProcessSignalVariable)!;
            var childReleasePath = Environment.GetEnvironmentVariable(QueueProcessReleaseVariable)!;
            var runtimeContext = CreateChildRuntimeContext(toolName);

            DurableTelemetryQueue.SetPendingWritePauseHookForTests((tmpPath, ownershipPath) => {
                File.WriteAllText(signalPath, tmpPath + Environment.NewLine + ownershipPath);
                SpinWait.SpinUntil(
                    () => File.Exists(childReleasePath),
                    TimeSpan.FromSeconds(30))
                    .Should().BeTrue("the parent must release the producer after checking the active temp");
            });

            DurableTelemetryQueue.CreateSafe(runtimeContext)!.Enqueue("{\"event\":\"active-temp\"}")
                .Should().BeTrue();
            return;
        }

        if (role == "active-temp-initializer") {
            var toolName = Environment.GetEnvironmentVariable(QueueProcessToolVariable)!;
            var signalPath = Environment.GetEnvironmentVariable(QueueProcessSignalVariable)!;
            var runtimeContext = CreateChildRuntimeContext(toolName);
            DurableTelemetryQueue.CreateSafe(runtimeContext).Should().NotBeNull();
            File.WriteAllText(signalPath, "initialized");
            return;
        }

        using var runtime = TestRuntimeScope.Create(typeof(DurableTelemetryQueueIntegrationTests));
        _ = runtime.CreateQueue();

        var producerSignalPath = Path.Combine(runtime.RootDir, "active-temp-producer.signal");
        var initializerSignalPath = Path.Combine(runtime.RootDir, "active-temp-initializer.signal");
        var releasePath = Path.Combine(runtime.RootDir, "active-temp.release");
        Process? producer = null;
        try {
            producer = StartQueueTestProcess(
                nameof(CrossProcess_QueueInitialization_DoesNotDeleteActiveProducerTempFile),
                "active-temp-producer",
                runtime.ToolNameUpper,
                producerSignalPath,
                releasePath);

            SpinWait.SpinUntil(
                () => File.Exists(producerSignalPath) || producer!.HasExited,
                TimeSpan.FromSeconds(30))
                .Should().BeTrue("the producer must pause after writing its temp file");
            producer!.HasExited.Should().BeFalse();

            var producerPaths = File.ReadAllLines(producerSignalPath);
            producerPaths.Should().HaveCount(2);
            var activeTempPath = producerPaths[0];
            var ownershipPath = producerPaths[1];
            File.Exists(activeTempPath).Should().BeTrue();
            File.Exists(ownershipPath).Should().BeTrue();
            File.SetLastWriteTimeUtc(
                activeTempPath,
                DateTime.UtcNow - TelemetryConfig.ProcessingStaleThreshold - TimeSpan.FromMinutes(1));

            using var initializer = StartQueueTestProcess(
                nameof(CrossProcess_QueueInitialization_DoesNotDeleteActiveProducerTempFile),
                "active-temp-initializer",
                runtime.ToolNameUpper,
                initializerSignalPath,
                releasePath);
            await WaitForChildSuccessAsync(initializer);
            File.Exists(initializerSignalPath).Should().BeTrue();

            File.Exists(activeTempPath).Should().BeTrue(
                "queue initialization in another process must not delete a producer temp file whose ownership sidecar is held");
            File.Exists(ownershipPath).Should().BeTrue();

            File.WriteAllText(releasePath, "release");
            await WaitForChildSuccessAsync(producer);

            Directory.EnumerateFiles(runtime.PendingDir, "*.tmp").Should().BeEmpty();
            var pendingPaths = Directory.EnumerateFiles(runtime.PendingDir, "*.json").ToList();
            pendingPaths.Should().ContainSingle();
            TelemetryEnvelope.Deserialize(File.ReadAllText(pendingPaths.Single())).PayloadJson.Should().Be("{\"event\":\"active-temp\"}");
        }
        finally {
            File.WriteAllText(releasePath, "release");
            if (producer is not null) {
                KillIfRunning(producer);
                producer.Dispose();
            }
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
        File.Exists(item.Path + ".lock").Should().BeTrue();
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

    [Fact]
    public void Netstandard2Queue_ExecutesClaimRecoveryAndTerminalOperations() {
        var assemblyPath = FindNetstandardAssemblyPath();
        var loadContext = new NetstandardAssemblyLoadContext(Path.GetDirectoryName(assemblyPath)!);
        var rootDirectory = string.Empty;

        try {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var contextType = assembly.GetType("KeelMatrix.Telemetry.TelemetryRuntimeContext")!;
            var queueType = assembly.GetType("KeelMatrix.Telemetry.Infrastructure.DurableTelemetryQueue")!;
            var toolName = "NETSTANDARDQUEUE_" + Guid.NewGuid().ToString("N")[..16];
            var runtimeContext = Activator.CreateInstance(
                contextType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: [toolName, typeof(DurableTelemetryQueueIntegrationTests)],
                culture: CultureInfo.InvariantCulture)!;

            Invoke(runtimeContext, "EnsureRootDirectoryResolvedOnWorkerThread");
            rootDirectory = (string)Invoke(runtimeContext, "GetRootDirectory")!;
            var queue = Invoke(queueType, "CreateSafe", runtimeContext)!;
            queue.Should().NotBeNull();

            ((bool)Invoke(queue, "Enqueue", "{\"event\":\"netstandard-queue\"}")!).Should().BeTrue();
            var firstClaim = SingleClaim(queue);
            var firstClaimPath = ClaimPath(firstClaim);
            File.Exists(firstClaimPath).Should().BeTrue();

            Invoke(queue, "Release", firstClaim);
            var releasedClaim = SingleClaim(queue);
            Invoke(queue, "Abandon", releasedClaim);

            var pendingPath = Directory.EnumerateFiles(
                    Path.Combine(rootDirectory, "telemetry.queue", "pending"),
                    "*.json")
                .Single();
            File.SetLastWriteTimeUtc(
                pendingPath,
                DateTime.UtcNow - TimeSpan.FromMinutes(6));

            var recoveredQueue = Invoke(queueType, "CreateSafe", runtimeContext)!;
            var recoveredClaim = SingleClaim(recoveredQueue);
            var recoveredClaimPath = ClaimPath(recoveredClaim);
            Invoke(recoveredQueue, "Complete", recoveredClaim);

            File.Exists(recoveredClaimPath).Should().BeFalse();
            Directory.EnumerateFiles(
                    Path.Combine(rootDirectory, "telemetry.queue", "pending"),
                    "*.json")
                .Should().BeEmpty();
            Directory.EnumerateFiles(
                    Path.Combine(rootDirectory, "telemetry.queue", "processing"),
                    "*.json")
                .Should().BeEmpty();
        }
        finally {
            loadContext.Unload();
            TestCleanup.TryDeleteDirectory(rootDirectory);
        }
    }

    private static TelemetryRuntimeContext CreateChildRuntimeContext(string toolName) {
        var runtimeContext = new TelemetryRuntimeContext(toolName, typeof(DurableTelemetryQueueIntegrationTests));
        runtimeContext.EnsureRootDirectoryResolvedOnWorkerThread();
        return runtimeContext;
    }

    private static Process StartQueueTestProcess(
        string testName,
        string role,
        string toolName,
        string? signalPath = null,
        string? releasePath = null,
        string? outputPath = null,
        string? producersDonePath = null,
        int? producerIndex = null,
        int? producerCount = null,
        int? itemsPerProducer = null) {
        var startInfo = new ProcessStartInfo {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("vstest");
        startInfo.ArgumentList.Add(typeof(DurableTelemetryQueueIntegrationTests).Assembly.Location);
        startInfo.ArgumentList.Add(
            $"--TestCaseFilter:FullyQualifiedName~{typeof(DurableTelemetryQueueIntegrationTests).FullName}.{testName}");
        startInfo.Environment[QueueProcessRoleVariable] = role;
        startInfo.Environment[QueueProcessToolVariable] = toolName;

        SetEnvironmentValue(startInfo, QueueProcessSignalVariable, signalPath);
        SetEnvironmentValue(startInfo, QueueProcessReleaseVariable, releasePath);
        SetEnvironmentValue(startInfo, QueueProcessOutputVariable, outputPath);
        SetEnvironmentValue(startInfo, QueueProcessProducersDoneVariable, producersDonePath);
        SetEnvironmentValue(startInfo, QueueProcessProducerIndexVariable, producerIndex?.ToString(CultureInfo.InvariantCulture));
        SetEnvironmentValue(startInfo, QueueProcessProducerCountVariable, producerCount?.ToString(CultureInfo.InvariantCulture));
        SetEnvironmentValue(startInfo, QueueProcessItemsPerProducerVariable, itemsPerProducer?.ToString(CultureInfo.InvariantCulture));

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start queue test process.");
    }

    private static void SetEnvironmentValue(ProcessStartInfo startInfo, string name, string? value) {
        if (value is not null)
            startInfo.Environment[name] = value;
    }

    private static async Task WaitForChildSuccessAsync(Process process) {
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        try {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
        }
        catch {
            KillIfRunning(process);
            await Task.WhenAll(standardOutputTask, standardErrorTask);
            throw;
        }

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;
        process.ExitCode.Should().Be(
            0,
            "child process output: stdout={0}; stderr={1}",
            standardOutput,
            standardError);
    }

    private static void KillIfRunning(Process process) {
        if (OperatingSystem.IsWindows()) {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);

            WaitForProcessExit(process, "the process tree root");
            return;
        }

        foreach (var processId in EnumerateDescendantProcessIds(process.Id).Reverse())
            KillProcess(processId);

        if (!process.HasExited) {
            try {
                process.Kill();
            }
            catch (InvalidOperationException) when (process.HasExited) {
                // The process exited after enumeration and before the kill.
            }
        }

        WaitForProcessExit(process, "the process tree root");
    }

    private static void WaitForProcessExit(Process process, string description) {
        if (!process.WaitForExit(10_000))
            throw new InvalidOperationException($"Timed out waiting for {description} process {process.Id} to exit.");
    }

    private static IEnumerable<int> EnumerateDescendantProcessIds(int parentProcessId) {
        var pending = new Stack<int>();
        pending.Push(parentProcessId);

        while (pending.Count > 0) {
            var parentId = pending.Pop();
            foreach (var childId in EnumerateDirectChildProcessIds(parentId)) {
                pending.Push(childId);
                yield return childId;
            }
        }
    }

    private static IEnumerable<int> EnumerateDirectChildProcessIds(int parentProcessId) {
        var startInfo = new ProcessStartInfo {
            FileName = "pgrep",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-P");
        startInfo.ArgumentList.Add(parentProcessId.ToString(CultureInfo.InvariantCulture));

        using var pgrep = Process.Start(startInfo);
        if (pgrep is null)
            throw new InvalidOperationException("Could not start pgrep while enumerating child processes.");

        var output = pgrep.StandardOutput.ReadToEnd();
        if (!pgrep.WaitForExit(1_000))
            throw new InvalidOperationException(
                $"Timed out waiting for pgrep while enumerating children of process {parentProcessId}.");

        if (pgrep.ExitCode == 1)
            return [];
        if (pgrep.ExitCode != 0)
            throw new InvalidOperationException(
                $"pgrep failed with exit code {pgrep.ExitCode} while enumerating children of process {parentProcessId}.");

        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) || processId <= 0)
                    throw new InvalidOperationException(
                        $"pgrep returned an invalid child process id '{value}' for process {parentProcessId}.");

                return processId;
            })
            .ToArray();
    }

    private static void KillProcess(int processId) {
        try {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
                process.Kill();

            WaitForProcessExit(process, "a descendant");
        }
        catch (ArgumentException) {
            // The descendant exited between enumeration and termination.
        }
    }

    private static string FindNetstandardAssemblyPath() {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null) {
            if (File.Exists(Path.Combine(current.FullName, "KeelMatrix.Telemetry.slnx"))) {
                var configuration = Directory.GetParent(AppContext.BaseDirectory)!.Name;
                var candidate = Path.Combine(
                    current.FullName,
                    "src",
                    "KeelMatrix.Telemetry",
                    "bin",
                    configuration,
                    "netstandard2.0",
                    "KeelMatrix.Telemetry.dll");
                if (File.Exists(candidate))
                    return candidate;

                foreach (var fallbackConfiguration in new[] { "Release", "Debug" }) {
                    candidate = Path.Combine(
                        current.FullName,
                        "src",
                        "KeelMatrix.Telemetry",
                        "bin",
                        fallbackConfiguration,
                        "netstandard2.0",
                        "KeelMatrix.Telemetry.dll");
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            current = current.Parent;
        }

        throw new FileNotFoundException("The built netstandard2.0 Telemetry assembly was not found.");
    }

    private static object SingleClaim(object queue) {
        var claims = ((System.Collections.IEnumerable)Invoke(queue, "TryClaim", 1)!)
            .Cast<object>()
            .ToList();
        claims.Should().ContainSingle();
        return claims.Single();
    }

    private static string ClaimPath(object claim) {
        return (string)claim.GetType().GetProperty(
            "Path",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(claim)!;
    }

    private static object? Invoke(object target, string methodName, params object?[] args) {
        var type = target as Type ?? target.GetType();
        var instance = target as Type is null ? target : null;
        var method = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method =>
                method.Name == methodName &&
                method.GetParameters().Length == args.Length);
        return method.Invoke(instance, args);
    }

    private sealed class NetstandardAssemblyLoadContext : AssemblyLoadContext {
        private readonly string assemblyDirectory;
        private readonly AssemblyDependencyResolver dependencyResolver;
        private readonly string packageRoot;
        private readonly string[] packagePaths;

        internal NetstandardAssemblyLoadContext(string assemblyDirectory)
            : base(isCollectible: true) {
            this.assemblyDirectory = assemblyDirectory;
            dependencyResolver = new AssemblyDependencyResolver(
                Path.Combine(assemblyDirectory, "KeelMatrix.Telemetry.dll"));
            packageRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
            packagePaths = ReadPackagePaths(Path.Combine(assemblyDirectory, "KeelMatrix.Telemetry.deps.json"));
        }

        protected override Assembly? Load(AssemblyName assemblyName) {
            var dependencyPath = dependencyResolver.ResolveAssemblyToPath(assemblyName)
                ?? Path.Combine(assemblyDirectory, assemblyName.Name + ".dll");
            if (File.Exists(dependencyPath))
                return LoadFromAssemblyPath(dependencyPath);

            foreach (var packagePath in packagePaths) {
                var netstandardPath = Path.Combine(
                    packageRoot,
                    packagePath,
                    "lib",
                    "netstandard2.0",
                    assemblyName.Name + ".dll");
                if (File.Exists(netstandardPath))
                    return LoadFromAssemblyPath(netstandardPath);

                var libRoot = Path.Combine(packageRoot, packagePath, "lib");
                if (!Directory.Exists(libRoot))
                    continue;

                var compatiblePath = Directory.EnumerateFiles(
                        libRoot,
                        assemblyName.Name + ".dll",
                        SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (compatiblePath is not null)
                    return LoadFromAssemblyPath(compatiblePath);
            }

            return null;
        }

        private static string[] ReadPackagePaths(string dependenciesPath) {
            using var document = JsonDocument.Parse(File.ReadAllText(dependenciesPath));
            return document.RootElement
                .GetProperty("libraries")
                .EnumerateObject()
                .Where(entry => entry.Value.GetProperty("type").GetString() == "package")
                .Select(entry => entry.Value.GetProperty("path").GetString())
                .Where(path => path is not null)
                .Select(path => path!)
                .ToArray();
        }
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

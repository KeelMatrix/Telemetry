// Copyright (c) KeelMatrix

using BenchmarkDotNet.Attributes;
using KeelMatrix.Telemetry.Infrastructure;

namespace KeelMatrix.Telemetry.Benchmarks;

[MemoryDiagnoser]
public class DurableTelemetryQueueBench {
    private const string Payload = "{\"event\":\"activation\",\"tool\":\"bench\",\"tool_version\":\"1.0.0\",\"telemetry_version\":\"1.0.0\",\"schema_version\":1,\"project_hash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"installation_hash\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"runtime\":\".NET 8.0.0\",\"os\":\"linux\",\"ci\":false,\"timestamp\":\"2026-01-01T00:00:00Z\"}";

    private BenchmarkRuntimeScope scope = null!;
    private ITelemetryQueue queue = null!;
    private DurableTelemetryQueue.ClaimedItem claimedItem;
    private enum QueueScenario {
        Enqueue,
        Claim,
        Complete,
        Abandon
    }

    [IterationSetup(Target = nameof(EnqueueSmallPayload))]
    public void SetupEnqueue() {
        PrepareScenario(QueueScenario.Enqueue);
    }

    [IterationSetup(Target = nameof(ClaimSinglePendingItem))]
    public void SetupClaim() {
        PrepareScenario(QueueScenario.Claim);
    }

    [IterationSetup(Target = nameof(CompleteClaimedItem))]
    public void SetupComplete() {
        PrepareScenario(QueueScenario.Complete);
    }

    [IterationSetup(Target = nameof(AbandonClaimedItem))]
    public void SetupAbandon() {
        PrepareScenario(QueueScenario.Abandon);
    }

    [Benchmark]
    public void EnqueueSmallPayload() => queue.Enqueue(Payload);

    [Benchmark]
    public int ClaimSinglePendingItem() => queue.TryClaim(1).Count();

    [Benchmark]
    public void CompleteClaimedItem() => queue.Complete(claimedItem);

    [Benchmark]
    public void AbandonClaimedItem() => queue.Abandon(claimedItem);

    [IterationCleanup]
    public void CleanupIteration() {
        scope.Dispose();
        BenchmarkSupport.RestoreGlobalOverrides();
    }

    private void PrepareScenario(QueueScenario scenario) {
        scope?.Dispose();
        scope = new BenchmarkRuntimeScope("BENCH_QUEUE", typeof(DurableTelemetryQueueBench));
        queue = scope.CreateQueue();

        if (scenario == QueueScenario.Enqueue)
            return;

        queue.Enqueue(Payload);

        if (scenario is QueueScenario.Complete or QueueScenario.Abandon) {
            claimedItem = queue.TryClaim(1).Single();
        }
    }
}

// Copyright (c) KeelMatrix

using BenchmarkDotNet.Attributes;
using KeelMatrix.Telemetry.Infrastructure;
using KeelMatrix.Telemetry.ProjectIdentity;

namespace KeelMatrix.Telemetry.Benchmarks;

[MemoryDiagnoser]
public class TelemetryDispatcherBench {
    private BenchmarkRuntimeScope scope = null!;
    private TelemetryDispatcher dispatcher = null!;
    private readonly ResolvedTelemetryIdentity identity =
        new(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    private enum DispatcherScenario {
        Activation,
        Heartbeat,
        SuppressedHeartbeat
    }

    [IterationSetup(Target = nameof(PlanActivationAndCommit))]
    public void SetupActivation() {
        PrepareDispatcher(DispatcherScenario.Activation);
    }

    [IterationSetup(Target = nameof(PlanHeartbeatAndCommit))]
    public void SetupHeartbeat() {
        PrepareDispatcher(DispatcherScenario.Heartbeat);
    }

    [IterationSetup(Target = nameof(SuppressCommittedHeartbeat))]
    public void SetupSuppressedHeartbeat() {
        PrepareDispatcher(DispatcherScenario.SuppressedHeartbeat);
    }

    [Benchmark]
    public string? PlanActivationAndCommit() {
        var evt = dispatcher.TryCreateActivationEvent();
        if (evt is null)
            return null;

        dispatcher.CommitActivation();
        return evt.Timestamp;
    }

    [Benchmark]
    public string? PlanHeartbeatAndCommit() {
        var evt = dispatcher.TryCreateHeartbeatEvent();
        if (evt is null)
            return null;

        dispatcher.CommitHeartbeat(evt.Week);
        return evt.Week;
    }

    [Benchmark]
    public bool SuppressCommittedHeartbeat() => dispatcher.TryCreateHeartbeatEvent() is null;

    [IterationCleanup]
    public void CleanupIteration() {
        scope.Dispose();
        BenchmarkSupport.RestoreGlobalOverrides();
    }

    private void PrepareDispatcher(DispatcherScenario scenario) {
        scope?.Dispose();
        scope = new BenchmarkRuntimeScope("BENCH_DISPATCHER", typeof(TelemetryDispatcherBench));
        dispatcher = new TelemetryDispatcher(scope.RuntimeContext, scope.RuntimeInfo, identity);

        if (scenario == DispatcherScenario.SuppressedHeartbeat) {
            dispatcher.CommitHeartbeat(TelemetryClock.GetCurrentIsoWeek());
        }
    }
}

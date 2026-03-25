// Copyright (c) KeelMatrix

using BenchmarkDotNet.Attributes;
using KeelMatrix.Telemetry.Events;
using KeelMatrix.Telemetry.Serialization;

namespace KeelMatrix.Telemetry.Benchmarks;

[MemoryDiagnoser]
public class TelemetrySerializerBench {
    private TelemetryRuntimeContext runtimeContext = null!;
    private ActivationEvent activationEvent = null!;
    private HeartbeatEvent heartbeatEvent = null!;

    [GlobalSetup]
    public void GlobalSetup() {
        BenchmarkSupport.ResetTelemetryProcessState();
        runtimeContext = new TelemetryRuntimeContext("BENCH_SERIALIZER", typeof(TelemetrySerializerBench));

        activationEvent = new ActivationEvent(
            runtimeContext.ToolName,
            runtimeContext.ToolVersion,
            TelemetryConfig.TelemetryVersion,
            TelemetryConfig.SchemaVersion,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            ".NET 8.0.0",
            "linux",
            ci: false,
            "2026-01-01T00:00:00Z");

        heartbeatEvent = new HeartbeatEvent(
            runtimeContext.ToolName,
            runtimeContext.ToolVersion,
            TelemetryConfig.TelemetryVersion,
            TelemetryConfig.SchemaVersion,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "2026-W01");
    }

    [Benchmark]
    public string? SerializeActivation() => TelemetrySerializer.Serialize(activationEvent, runtimeContext.ToolName);

    [Benchmark]
    public string? SerializeHeartbeat() => TelemetrySerializer.Serialize(heartbeatEvent, runtimeContext.ToolName);

    [GlobalCleanup]
    public void GlobalCleanup() => BenchmarkSupport.RestoreGlobalOverrides();
}

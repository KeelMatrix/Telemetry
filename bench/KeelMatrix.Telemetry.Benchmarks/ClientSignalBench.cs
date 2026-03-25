// Copyright (c) KeelMatrix

using BenchmarkDotNet.Attributes;
using KeelMatrix.Telemetry.Infrastructure;

namespace KeelMatrix.Telemetry.Benchmarks;

[MemoryDiagnoser]
public class ClientSignalBench {
    private BenchmarkRuntimeScope scope = null!;
    private TelemetryClient telemetryClient = null!;
    private TelemetryDeliveryWorker worker = null!;

    [GlobalSetup]
    public void GlobalSetup() {
        scope = new BenchmarkRuntimeScope("BENCH_CLIENT_SIGNAL", typeof(ClientSignalBench));
        worker = new TelemetryDeliveryWorker(
            scope.RuntimeContext,
            scope.RuntimeInfo,
            new FixedProjectIdentityProvider(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            new RecordingTelemetrySender(shouldSucceed: true));
        telemetryClient = new TelemetryClient(worker);
    }

    [Benchmark]
    public void TrackActivation() => telemetryClient.TrackActivation();

    [Benchmark]
    public void TrackHeartbeat() => telemetryClient.TrackHeartbeat();

    [GlobalCleanup]
    public void GlobalCleanup() {
        worker.Dispose();
        scope.Dispose();
        BenchmarkSupport.RestoreGlobalOverrides();
    }
}

// Copyright (c) KeelMatrix

using System.Diagnostics;
using KeelMatrix.Telemetry.Infrastructure;
using KeelMatrix.Telemetry.ProjectIdentity;

namespace KeelMatrix.Telemetry.Benchmarks;

internal static class BenchmarkSupport {
    internal static readonly Uri OfflineTelemetryUrl = new("http://127.0.0.1:1/", UriKind.Absolute);

    internal static void ResetTelemetryProcessState() {
        TelemetryConfig.ResetProcessDisabledForTests();
        TelemetryConfig.SetUrlOverrideForTests(OfflineTelemetryUrl);
        GitDiscovery.SetStartingPointsOverrideForTests(null);
    }

    internal static void RestoreGlobalOverrides() {
        TelemetryConfig.SetUrlOverrideForTests(null);
        GitDiscovery.SetStartingPointsOverrideForTests(null);
    }

    internal static void ReportIgnoredException(Exception ex) {
        Debug.WriteLine(ex);
    }

    internal static void TryDeleteDirectory(string dir) {
        try {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) {
            ReportIgnoredException(ex);
        }
    }
}

internal sealed class BenchmarkRuntimeScope : IDisposable {
    internal BenchmarkRuntimeScope(string prefix, Type benchmarkType) {
        BenchmarkSupport.ResetTelemetryProcessState();
        ToolNameUpper = $"{prefix}_{Guid.NewGuid():N}";
        RuntimeContext = new TelemetryRuntimeContext(ToolNameUpper, benchmarkType);
        RuntimeInfo = new RuntimeInfo();
        RuntimeInfo.SetCiOverrideForTests(false);
        RuntimeContext.EnsureRootDirectoryResolvedOnWorkerThread();
        RootDirectory = RuntimeContext.GetRootDirectory();
        BenchmarkSupport.TryDeleteDirectory(RootDirectory);
    }

    internal string ToolNameUpper { get; }
    internal TelemetryRuntimeContext RuntimeContext { get; }
    internal RuntimeInfo RuntimeInfo { get; }
    internal string RootDirectory { get; }

    internal ITelemetryQueue CreateQueue() => DurableTelemetryQueue.CreateSafe(RuntimeContext);

    public void Dispose() {
        BenchmarkSupport.TryDeleteDirectory(RootDirectory);
    }
}

internal sealed class FixedProjectIdentityProvider(string projectHash, string installationHash) : IProjectIdentityProvider {
    private readonly ResolvedTelemetryIdentity identity = new(projectHash, installationHash);

    public ResolvedTelemetryIdentity EnsureResolvedOnWorkerThread() => identity;
}

internal sealed class RecordingTelemetrySender(bool shouldSucceed) : ITelemetrySender {
    private readonly List<string> sentPayloads = [];

    internal IReadOnlyList<string> SentPayloads => sentPayloads;

    public Task<bool> TrySendAsync(string json, CancellationToken token) {
        sentPayloads.Add(json);
        return Task.FromResult(shouldSucceed);
    }

    public void Dispose() {
    }
}

internal sealed class StartingPointsOverrideScope : IDisposable {
    public StartingPointsOverrideScope(params string[] startingPoints) {
        GitDiscovery.SetStartingPointsOverrideForTests(startingPoints);
    }

    public void Dispose() {
        GitDiscovery.SetStartingPointsOverrideForTests(null);
    }
}

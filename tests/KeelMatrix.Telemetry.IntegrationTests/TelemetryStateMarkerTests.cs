// Copyright (c) KeelMatrix

using FluentAssertions;

namespace KeelMatrix.Telemetry.IntegrationTests;

// TelemetryConfig.Runtime is global static state and the marker directory is per-user.
// Keep these tests non-parallel.
[CollectionDefinition(Name, DisableParallelization = true)]
public static class TelemetryStateMarkerTestsCollectionDefinition {
    public const string Name = $"{nameof(TelemetryStateMarkerTests)}.NonParallel";
}

[Collection(TelemetryStateMarkerTestsCollectionDefinition.Name)]
public sealed class TelemetryStateMarkerTests {
    private const string MarkerSubdir = "markers";

    [Fact]
    public void ShouldSendActivation_True_WhenNoMarker() {
        using var runtime = IsolatedRuntime.Create();

        var state = new TelemetryState(runtime.ProjectHash);

        state.ShouldSendActivation().Should().BeTrue();
    }

    [Fact]
    public void CommitActivation_WritesMarker_AndThenShouldSendActivationFalse() {
        using var runtime = IsolatedRuntime.Create();

        var state = new TelemetryState(runtime.ProjectHash);

        state.CommitActivation();

        state.ShouldSendActivation().Should().BeFalse();

        var expectedPath = Path.Combine(runtime.MarkerDir, $"activation.{runtime.ProjectHash}.json");
        File.Exists(expectedPath).Should().BeTrue();
    }

    [Fact]
    public void ShouldSendHeartbeat_True_WhenNoMarkerForWeek() {
        using var runtime = IsolatedRuntime.Create();

        var state = new TelemetryState(runtime.ProjectHash);
        var week = runtime.Week;

        state.ShouldSendHeartbeat(week).Should().BeTrue();
    }

    [Fact]
    public void CommitHeartbeat_WritesMarker_AndThenShouldSendHeartbeatFalse() {
        using var runtime = IsolatedRuntime.Create();

        var state = new TelemetryState(runtime.ProjectHash);
        var week = runtime.Week;

        state.CommitHeartbeat(week);

        state.ShouldSendHeartbeat(week).Should().BeFalse();

        var expectedPath = Path.Combine(runtime.MarkerDir, $"heartbeat.{runtime.ProjectHash}.{week}.json");
        File.Exists(expectedPath).Should().BeTrue();
    }

    [Fact]
    public void MarkerCleanup_DeletesOldest_WhenExceedsMaxMarkerFiles() {
        using var runtime = IsolatedRuntime.Create(prepopulateMarkers: true);

        // Creating a new state triggers cleanup in the ctor.
        _ = new TelemetryState(runtime.ProjectHash);

        var markerFiles = Directory.EnumerateFiles(runtime.MarkerDir, "*.json").ToList();
        markerFiles.Count.Should().BeLessOrEqualTo(TelemetryConfig.MaxMarkerFiles);

        // Ensure the oldest files were removed.
        // We pre-populate with "zzzz_" as the newest and "aaaa_" as the oldest.
        markerFiles.Any(p => Path.GetFileName(p).StartsWith("aaaa_", StringComparison.Ordinal)).Should().BeFalse();
        markerFiles.Any(p => Path.GetFileName(p).StartsWith("zzzz_", StringComparison.Ordinal)).Should().BeTrue();
    }

    private sealed class IsolatedRuntime : IDisposable {
        public required string RootDir { get; init; }
        public required string MarkerDir { get; init; }
        public required string ProjectHash { get; init; }
        public required string Week { get; init; }

        public static IsolatedRuntime Create(bool prepopulateMarkers = false) {
            // ToolNameUpper is part of the per-user telemetry root: "KeelMatrix/{ToolNameUpper}".
            var toolNameUpper = "INTEGRATIONTEST_" + Guid.NewGuid().ToString("N");
            TelemetryConfig.Runtime.Set(toolNameUpper, typeof(TelemetryStateMarkerTests));

            // TelemetryState expects the root directory to already be resolved.
            TelemetryConfig.Runtime.EnsureRootDirectoryResolvedOnWorkerThread();

            var root = TelemetryConfig.Runtime.GetRootDirectory();
            var markerDir = Path.Combine(root, MarkerSubdir);

            // Fresh per-test root.
            if (Directory.Exists(root)) {
                try { Directory.Delete(root, recursive: true); } catch { /* swallow */ }
            }
            Directory.CreateDirectory(markerDir);

            var runtime = new IsolatedRuntime {
                RootDir = root,
                MarkerDir = markerDir,
                ProjectHash = "proj_" + Guid.NewGuid().ToString("N"),
                Week = Infrastructure.TelemetryClock.GetCurrentIsoWeek()
            };

            if (prepopulateMarkers) {
                runtime.PrepopulateMarkersForCleanupTest();
            }

            return runtime;
        }

        private void PrepopulateMarkersForCleanupTest() {
            // Create (MaxMarkerFiles + 5) marker files with deterministic "oldest" and "newest" timestamps.
            // The production cleanup sorts by LastWriteTimeUtc (see TelemetryState.TryCleanup).
            var baseTime = DateTimeOffset.UtcNow.AddDays(-30);

            const int total = TelemetryConfig.MaxMarkerFiles + 5;
            for (int i = 0; i < total; i++) {
                // First few have prefix "aaaa_" to make it easy to assert they were removed.
                var prefix = i < 5 ? "aaaa_" : "zzzz_";
                var name = $"{prefix}{i:D5}.json";
                var path = Path.Combine(MarkerDir, name);

                File.WriteAllText(path, "{}");

                var ts = baseTime.AddMinutes(i);
                TrySetTimesUtc(path, ts);
            }
        }

        private static void TrySetTimesUtc(string path, DateTimeOffset timestampUtc) {
            try {
                File.SetLastWriteTimeUtc(path, timestampUtc.UtcDateTime);
            }
            catch {
                // swallow
            }

            // Best-effort: may not be supported on all platforms.
            try {
                File.SetCreationTimeUtc(path, timestampUtc.UtcDateTime);
            }
            catch {
                // swallow
            }
        }

        public void Dispose() {
            try {
                if (Directory.Exists(RootDir))
                    Directory.Delete(RootDir, recursive: true);
            }
            catch {
                // swallow
            }
        }
    }
}

// Copyright (c) KeelMatrix

using FluentAssertions;

namespace KeelMatrix.Telemetry.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public static class TelemetryStateMarkerTestsCollectionDefinition {
    public const string Name = $"{nameof(TelemetryStateMarkerTests)}.NonParallel";
}

[Collection(TelemetryStateMarkerTestsCollectionDefinition.Name)]
public sealed class TelemetryStateMarkerTests {
    [Fact]
    public void ShouldSendActivation_True_WhenNoMarker() {
        using var runtime = TestRuntimeScope.Create(typeof(TelemetryStateMarkerTests));

        var state = new TelemetryState(runtime.RootDir, runtime.ProjectHash);

        state.ShouldSendActivation().Should().BeTrue();
    }

    [Fact]
    public void CommitActivation_WritesMarker_AndThenShouldSendActivationFalse() {
        using var runtime = TestRuntimeScope.Create(typeof(TelemetryStateMarkerTests));

        var state = new TelemetryState(runtime.RootDir, runtime.ProjectHash);

        state.CommitActivation();

        state.ShouldSendActivation().Should().BeFalse();

        var expectedPath = Path.Combine(runtime.MarkerDir, $"activation.{runtime.ProjectHash}.json");
        File.Exists(expectedPath).Should().BeTrue();
    }

    [Fact]
    public void ShouldSendHeartbeat_True_WhenNoMarkerForWeek() {
        using var runtime = TestRuntimeScope.Create(typeof(TelemetryStateMarkerTests));

        var state = new TelemetryState(runtime.RootDir, runtime.ProjectHash);
        var week = runtime.CurrentWeek;

        state.ShouldSendHeartbeat(week).Should().BeTrue();
    }

    [Fact]
    public void CommitHeartbeat_WritesMarker_AndThenShouldSendHeartbeatFalse() {
        using var runtime = TestRuntimeScope.Create(typeof(TelemetryStateMarkerTests));

        var state = new TelemetryState(runtime.RootDir, runtime.ProjectHash);
        var week = runtime.CurrentWeek;

        state.CommitHeartbeat(week);

        state.ShouldSendHeartbeat(week).Should().BeFalse();

        var expectedPath = Path.Combine(runtime.MarkerDir, $"heartbeat.{runtime.ProjectHash}.{week}.json");
        File.Exists(expectedPath).Should().BeTrue();
    }

    [Fact]
    public void MarkerCleanup_DeletesOldest_WhenExceedsMaxMarkerFiles() {
        using var runtime = TestRuntimeScope.Create(typeof(TelemetryStateMarkerTests));
        PrepopulateMarkersForCleanupTest(runtime.MarkerDir);

        // Creating a new state triggers cleanup in the ctor.
        _ = new TelemetryState(runtime.RootDir, runtime.ProjectHash);

        var markerFiles = Directory.EnumerateFiles(runtime.MarkerDir, "*.json").ToList();
        markerFiles.Count.Should().BeLessOrEqualTo(TelemetryConfig.MaxMarkerFiles);

        // Ensure the oldest files were removed.
        // We pre-populate with "zzzz_" as the newest and "aaaa_" as the oldest.
        markerFiles.Any(p => Path.GetFileName(p).StartsWith("aaaa_", StringComparison.Ordinal)).Should().BeFalse();
        markerFiles.Any(p => Path.GetFileName(p).StartsWith("zzzz_", StringComparison.Ordinal)).Should().BeTrue();
    }

    private static void PrepopulateMarkersForCleanupTest(string markerDir) {
        Directory.CreateDirectory(markerDir);

        // Create (MaxMarkerFiles + 5) marker files with deterministic "oldest" and "newest" timestamps.
        // The production cleanup sorts by LastWriteTimeUtc (see TelemetryState.TryCleanup).
        var baseTime = DateTimeOffset.UtcNow.AddDays(-30);

        const int total = TelemetryConfig.MaxMarkerFiles + 5;
        for (int i = 0; i < total; i++) {
            // First few have prefix "aaaa_" to make it easy to assert they were removed.
            var prefix = i < 5 ? "aaaa_" : "zzzz_";
            var name = $"{prefix}{i:D5}.json";
            var path = Path.Combine(markerDir, name);

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
}

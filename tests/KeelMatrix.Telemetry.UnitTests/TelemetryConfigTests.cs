// Copyright (c) KeelMatrix

using FluentAssertions;

namespace KeelMatrix.Telemetry.UnitTests;

// TelemetryConfig has static mutable state (Runtime + env var reads).
// Ensure these tests never run in parallel with others that may touch the same state.
[CollectionDefinition(Name, DisableParallelization = true)]
public static class TelemetryConfigTestsCollectionDefinition {
    public const string Name = $"{nameof(TelemetryConfigTests)}.NonParallel";
}

[Collection(TelemetryConfigTestsCollectionDefinition.Name)]
public sealed class TelemetryConfigTests {
    private const string EnvKeelMatrixNoTelemetry = "KEELMATRIX_NO_TELEMETRY";
    private const string EnvDotNetCliTelemetryOptOut = "DOTNET_CLI_TELEMETRY_OPTOUT";
    private const string EnvDoNotTrack = "DO_NOT_TRACK";

    private static readonly string[] TruthyValues = [
        "1",
        "true",
        "yes",
        "y",
        "on",
        "TRUE",
        "Yes",
        "On"
    ];

    [Fact]
    public void IsTelemetryDisabled_ReturnsFalse_WhenAllOptOutVarsCleared() {
        using var _ = new EnvironmentVariableSnapshot(
            EnvKeelMatrixNoTelemetry,
            EnvDotNetCliTelemetryOptOut,
            EnvDoNotTrack);

        ClearOptOutVars();

        // Do not touch processDisabled (DisableTelemetryForCurrentProcess).
        TelemetryConfig.IsTelemetryDisabled().Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(GetTruthyValues))]
    public void IsTelemetryDisabled_ReturnsTrue_WhenKeelMatrixNoTelemetryTruthy(string truthy) {
        using var _ = new EnvironmentVariableSnapshot(
            EnvKeelMatrixNoTelemetry,
            EnvDotNetCliTelemetryOptOut,
            EnvDoNotTrack);

        ClearOptOutVars();
        Environment.SetEnvironmentVariable(EnvKeelMatrixNoTelemetry, truthy);

        TelemetryConfig.IsTelemetryDisabled().Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(GetTruthyValues))]
    public void IsTelemetryDisabled_ReturnsTrue_WhenDotNetCliTelemetryOptOutTruthy(string truthy) {
        using var _ = new EnvironmentVariableSnapshot(
            EnvKeelMatrixNoTelemetry,
            EnvDotNetCliTelemetryOptOut,
            EnvDoNotTrack);

        ClearOptOutVars();
        Environment.SetEnvironmentVariable(EnvDotNetCliTelemetryOptOut, truthy);

        TelemetryConfig.IsTelemetryDisabled().Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(GetTruthyValues))]
    public void IsTelemetryDisabled_ReturnsTrue_WhenDoNotTrackTruthy(string truthy) {
        using var _ = new EnvironmentVariableSnapshot(
            EnvKeelMatrixNoTelemetry,
            EnvDotNetCliTelemetryOptOut,
            EnvDoNotTrack);

        ClearOptOutVars();
        Environment.SetEnvironmentVariable(EnvDoNotTrack, truthy);

        TelemetryConfig.IsTelemetryDisabled().Should().BeTrue();
    }

    [Fact]
    public void Runtime_Set_SetsToolNameLowercase_AndResetsRootDirectory() {
        // Use a unique tool name so we never collide with other test state.
        var toolNameUpper = "UNITTEST_" + Guid.NewGuid().ToString("N");

        TelemetryConfig.Runtime.Set(toolNameUpper, typeof(TelemetryConfigTests));

        TelemetryConfig.Runtime.ToolName.Should().Be(toolNameUpper.ToLowerInvariant());

        // RootDirectory must be cleared to null so that caller thread does no I/O.
        Action act = static () => _ = TelemetryConfig.Runtime.GetRootDirectory();
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("Telemetry root directory has not been resolved yet.");
    }

    [Fact]
    public void Runtime_EnsureRootDirectoryResolvedOnWorkerThread_ProducesRootedPath() {
        var toolNameUpper = "UNITTEST_" + Guid.NewGuid().ToString("N");

        TelemetryConfig.Runtime.Set(toolNameUpper, typeof(TelemetryConfigTests));

        TelemetryConfig.Runtime.EnsureRootDirectoryResolvedOnWorkerThread();

        var root = TelemetryConfig.Runtime.GetRootDirectory();

        Path.IsPathRooted(root).Should().BeTrue();

        // Do not assert the OS-specific base directory, only that it contains "KeelMatrix/{ToolNameUpper}".
        var expectedSuffix = Path.Combine("KeelMatrix", toolNameUpper);
        root.Should().Contain(expectedSuffix);
    }

    public static TheoryData<string> GetTruthyValues() {
        var data = new TheoryData<string>();
        data.AddRange(TruthyValues);
        return data;
    }

    private static void ClearOptOutVars() {
        Environment.SetEnvironmentVariable(EnvKeelMatrixNoTelemetry, null);
        Environment.SetEnvironmentVariable(EnvDotNetCliTelemetryOptOut, null);
        Environment.SetEnvironmentVariable(EnvDoNotTrack, null);
    }

    private sealed class EnvironmentVariableSnapshot : IDisposable {
        private readonly (string Name, string? Value)[] snapshot;

        public EnvironmentVariableSnapshot(params string[] names) {
            snapshot = new (string, string?)[names.Length];
            for (int i = 0; i < names.Length; i++) {
                var name = names[i];
                snapshot[i] = (name, Environment.GetEnvironmentVariable(name));
            }
        }

        public void Dispose() {
            foreach (var (Name, Value) in snapshot) {
                Environment.SetEnvironmentVariable(Name, Value);
            }
        }
    }
}

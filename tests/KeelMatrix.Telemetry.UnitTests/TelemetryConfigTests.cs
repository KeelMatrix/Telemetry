// Copyright (c) KeelMatrix

using FluentAssertions;
using KeelMatrix.Telemetry.ProjectIdentity;

namespace KeelMatrix.Telemetry.UnitTests;

// TelemetryConfig has static mutable state (Runtime + env var reads).
// Ensure these tests never run in parallel with others that may touch the same state.
[CollectionDefinition(Name, DisableParallelization = true)]
public static class TelemetryConfigTestsCollectionDefinition {
    public const string Name = $"{nameof(TelemetryConfigTests)}.NonParallel";
}

[Collection(TelemetryConfigTestsCollectionDefinition.Name)]
public sealed class TelemetryConfigTests : IDisposable {
    private const string EnvKeelMatrixNoTelemetry = "KEELMATRIX_NO_TELEMETRY";
    private const string EnvDotNetCliTelemetryOptOut = "DOTNET_CLI_TELEMETRY_OPTOUT";
    private const string EnvDoNotTrack = "DO_NOT_TRACK";
    private const string RepositoryConfigFileName = "keelmatrix.telemetry.json";
    private static readonly string SharedTempRoot = CreateSharedTempRoot();

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

    public TelemetryConfigTests() {
        TelemetryConfig.ResetProcessDisabledForTests();
        GitDiscovery.SetStartingPointsOverrideForTests(null);
    }

    public void Dispose() {
        TelemetryConfig.ResetProcessDisabledForTests();
        GitDiscovery.SetStartingPointsOverrideForTests(null);
    }

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
    public void IsTelemetryDisabled_ReturnsTrue_WhenRepositoryConfigDisablesTelemetry() {
        using var _ = new EnvironmentVariableSnapshot(
            EnvKeelMatrixNoTelemetry,
            EnvDotNetCliTelemetryOptOut,
            EnvDoNotTrack);
        using var repo = CreateRepository("src\\tool");
        using var __ = new StartingPointsOverrideScope(repo.StartingPoint);

        ClearOptOutVars();
        File.WriteAllText(
            Path.Combine(repo.Root, RepositoryConfigFileName),
            """
            {
              "disabled": true
            }
            """);

        TelemetryConfig.IsTelemetryDisabled().Should().BeTrue();
    }

    [Fact]
    public void IsTelemetryDisabled_ReturnsTrue_WhenDotEnvDisablesTelemetry() {
        using var _ = new EnvironmentVariableSnapshot(
            EnvKeelMatrixNoTelemetry,
            EnvDotNetCliTelemetryOptOut,
            EnvDoNotTrack);
        using var repo = CreateRepository("src\\tool");
        using var __ = new StartingPointsOverrideScope(repo.StartingPoint);

        ClearOptOutVars();
        File.WriteAllText(Path.Combine(repo.Root, ".env"), "KEELMATRIX_NO_TELEMETRY=1");

        TelemetryConfig.IsTelemetryDisabled().Should().BeTrue();
    }

    [Fact]
    public void IsTelemetryDisabled_ReturnsTrue_WhenDotEnvLocalDisablesTelemetry() {
        using var _ = new EnvironmentVariableSnapshot(
            EnvKeelMatrixNoTelemetry,
            EnvDotNetCliTelemetryOptOut,
            EnvDoNotTrack);
        using var repo = CreateRepository("src\\tool");
        using var __ = new StartingPointsOverrideScope(repo.StartingPoint);

        ClearOptOutVars();
        File.WriteAllText(Path.Combine(repo.Root, ".env.local"), "KEELMATRIX_NO_TELEMETRY=1");

        TelemetryConfig.IsTelemetryDisabled().Should().BeTrue();
    }

    [Fact]
    public void IsTelemetryDisabled_ReturnsTrue_WhenLaterRepositoryRootDisablesTelemetry() {
        using var _ = new EnvironmentVariableSnapshot(
            EnvKeelMatrixNoTelemetry,
            EnvDotNetCliTelemetryOptOut,
            EnvDoNotTrack);
        using var repoWithoutOptOut = CreateRepository("src\\tool");
        using var repoWithOptOut = CreateRepository("tests\\tool");
        using var __ = new StartingPointsOverrideScope(repoWithoutOptOut.StartingPoint, repoWithOptOut.StartingPoint);

        ClearOptOutVars();
        File.WriteAllText(Path.Combine(repoWithOptOut.Root, ".env.local"), "DOTNET_CLI_TELEMETRY_OPTOUT=1");

        TelemetryConfig.IsTelemetryDisabled().Should().BeTrue();
    }

    [Fact]
    public void IsTelemetryDisabled_ReturnsFalse_WhenMultipleRepositoryRootsAreAllUnspecified() {
        using var _ = new EnvironmentVariableSnapshot(
            EnvKeelMatrixNoTelemetry,
            EnvDotNetCliTelemetryOptOut,
            EnvDoNotTrack);
        using var repoA = CreateRepository("src\\tool");
        using var repoB = CreateRepository("tests\\tool");
        using var __ = new StartingPointsOverrideScope(repoA.StartingPoint, repoB.StartingPoint);

        ClearOptOutVars();

        TelemetryConfig.IsTelemetryDisabled().Should().BeFalse();
    }

    [Fact]
    public void GetCandidateRepositoryRoots_DeduplicatesResolvedRepositoryRoots() {
        using var repo = CreateRepository("src\\tool");
        var otherStartingPoint = Path.Combine(repo.Root, "tests", "tool");
        Directory.CreateDirectory(otherStartingPoint);
        using var _ = new StartingPointsOverrideScope(repo.StartingPoint, otherStartingPoint);

        var roots = TelemetryDisableResolver.GetCandidateRepositoryRoots();

        roots.Should().ContainSingle().Which.Should().Be(repo.Root);
    }

    [Fact]
    public void IsTelemetryDisabled_PrefersDotEnvLocalOverDotEnv_WhenBothExist() {
        using var _ = new EnvironmentVariableSnapshot(
            EnvKeelMatrixNoTelemetry,
            EnvDotNetCliTelemetryOptOut,
            EnvDoNotTrack);
        using var repo = CreateRepository("src\\tool");
        using var __ = new StartingPointsOverrideScope(repo.StartingPoint);

        ClearOptOutVars();
        File.WriteAllText(Path.Combine(repo.Root, ".env"), "KEELMATRIX_NO_TELEMETRY=1");
        File.WriteAllText(Path.Combine(repo.Root, ".env.local"), "KEELMATRIX_NO_TELEMETRY=0");

        TelemetryConfig.IsTelemetryDisabled().Should().BeFalse();
    }

    [Fact]
    public void IsTelemetryDisabled_PrefersProcessEnvironmentOverRepositoryFiles_WhenEnvironmentVariableIsSet() {
        using var _ = new EnvironmentVariableSnapshot(
            EnvKeelMatrixNoTelemetry,
            EnvDotNetCliTelemetryOptOut,
            EnvDoNotTrack);
        using var repo = CreateRepository("src\\tool");
        using var __ = new StartingPointsOverrideScope(repo.StartingPoint);

        ClearOptOutVars();
        Environment.SetEnvironmentVariable(EnvKeelMatrixNoTelemetry, "false");
        File.WriteAllText(Path.Combine(repo.Root, ".env"), "KEELMATRIX_NO_TELEMETRY=1");

        TelemetryConfig.IsTelemetryDisabled().Should().BeFalse();
    }

    [Fact]
    public void IsTelemetryDisabled_ContinuesPastInvalidRepositoryConfigInEarlierRepositoryRoot() {
        using var _ = new EnvironmentVariableSnapshot(
            EnvKeelMatrixNoTelemetry,
            EnvDotNetCliTelemetryOptOut,
            EnvDoNotTrack);
        using var repoWithInvalidConfig = CreateRepository("src\\tool");
        using var repoWithOptOut = CreateRepository("tests\\tool");
        using var __ = new StartingPointsOverrideScope(repoWithInvalidConfig.StartingPoint, repoWithOptOut.StartingPoint);

        ClearOptOutVars();
        File.WriteAllText(Path.Combine(repoWithInvalidConfig.Root, RepositoryConfigFileName), "{ not-valid-json");
        File.WriteAllText(Path.Combine(repoWithOptOut.Root, ".env.local"), "DO_NOT_TRACK=1");

        Action act = () => TelemetryConfig.IsTelemetryDisabled().Should().BeTrue();
        act.Should().NotThrow();
    }

    [Fact]
    public void IsTelemetryDisabled_IgnoresUnrelatedDotEnvKeys() {
        using var _ = new EnvironmentVariableSnapshot(
            EnvKeelMatrixNoTelemetry,
            EnvDotNetCliTelemetryOptOut,
            EnvDoNotTrack);
        using var repo = CreateRepository("src\\tool");
        using var __ = new StartingPointsOverrideScope(repo.StartingPoint);

        ClearOptOutVars();
        File.WriteAllText(Path.Combine(repo.Root, ".env"), "UNRELATED_KEY=1");

        TelemetryConfig.IsTelemetryDisabled().Should().BeFalse();
    }

    [Fact]
    public void IsTelemetryDisabled_AppliesOnlyWithinResolvedRepositoryRoot() {
        using var _ = new EnvironmentVariableSnapshot(
            EnvKeelMatrixNoTelemetry,
            EnvDotNetCliTelemetryOptOut,
            EnvDoNotTrack);
        using var repoA = CreateRepository("src\\tool");
        using var repoB = CreateRepository("src\\tool");

        ClearOptOutVars();
        File.WriteAllText(Path.Combine(repoA.Root, ".env"), "KEELMATRIX_NO_TELEMETRY=1");

        using (var scope = new StartingPointsOverrideScope(repoA.StartingPoint)) {
            TelemetryConfig.IsTelemetryDisabled().Should().BeTrue();
        }

        using (var scope = new StartingPointsOverrideScope(repoB.StartingPoint)) {
            TelemetryConfig.IsTelemetryDisabled().Should().BeFalse();
        }
    }

    [Fact]
    public void IsTelemetryDisabled_ReturnsFalse_WhenRepositoryRootHasNoSupportedFiles() {
        using var _ = new EnvironmentVariableSnapshot(
            EnvKeelMatrixNoTelemetry,
            EnvDotNetCliTelemetryOptOut,
            EnvDoNotTrack);
        using var repo = CreateRepository("src\\tool");
        using var __ = new StartingPointsOverrideScope(repo.StartingPoint);

        ClearOptOutVars();

        TelemetryConfig.IsTelemetryDisabled().Should().BeFalse();
    }

    [Fact]
    public void IsTelemetryDisabled_PrefersRepositoryConfigOverDotEnvLocal() {
        using var _ = new EnvironmentVariableSnapshot(
            EnvKeelMatrixNoTelemetry,
            EnvDotNetCliTelemetryOptOut,
            EnvDoNotTrack);
        using var repo = CreateRepository("src\\tool");
        using var __ = new StartingPointsOverrideScope(repo.StartingPoint);

        ClearOptOutVars();
        File.WriteAllText(
            Path.Combine(repo.Root, RepositoryConfigFileName),
            """
            {
              "disabled": false
            }
            """);
        File.WriteAllText(Path.Combine(repo.Root, ".env.local"), "KEELMATRIX_NO_TELEMETRY=1");

        TelemetryConfig.IsTelemetryDisabled().Should().BeFalse();
    }

    [Fact]
    public void IsTelemetryDisabled_SupportsExportPrefixInDotEnvFiles() {
        using var _ = new EnvironmentVariableSnapshot(
            EnvKeelMatrixNoTelemetry,
            EnvDotNetCliTelemetryOptOut,
            EnvDoNotTrack);
        using var repo = CreateRepository("src\\tool");
        using var __ = new StartingPointsOverrideScope(repo.StartingPoint);

        ClearOptOutVars();
        File.WriteAllText(Path.Combine(repo.Root, ".env"), "EXPORT KEELMATRIX_NO_TELEMETRY=1");

        TelemetryConfig.IsTelemetryDisabled().Should().BeTrue();
    }

    [Fact]
    public void IsTelemetryDisabled_ReturnsFalse_WhenRepositoryFileValuesAreFalseyOrAbsent() {
        using var _ = new EnvironmentVariableSnapshot(
            EnvKeelMatrixNoTelemetry,
            EnvDotNetCliTelemetryOptOut,
            EnvDoNotTrack);
        using var repo = CreateRepository("src\\tool");
        using var __ = new StartingPointsOverrideScope(repo.StartingPoint);

        ClearOptOutVars();
        File.WriteAllText(Path.Combine(repo.Root, ".env"), "KEELMATRIX_NO_TELEMETRY=0");
        File.WriteAllText(Path.Combine(repo.Root, ".env.local"), "UNRELATED_KEY=1");

        TelemetryConfig.IsTelemetryDisabled().Should().BeFalse();
    }

    [Fact]
    public void RuntimeContext_SetsToolNameLowercase_AndKeepsRootDirectoryUnresolvedUntilWorkerThread() {
        // Use a unique tool name so we never collide with other test state.
        var toolNameUpper = "UNITTEST_" + Guid.NewGuid().ToString("N");

        var runtimeContext = new TelemetryRuntimeContext(toolNameUpper, typeof(TelemetryConfigTests));

        runtimeContext.ToolName.Should().Be(toolNameUpper.ToLowerInvariant());

        // RootDirectory must be cleared to null so that caller thread does no I/O.
        Action act = () => _ = runtimeContext.GetRootDirectory();
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("Telemetry root directory has not been resolved yet.");
    }

    [Fact]
    public void RuntimeContext_EnsureRootDirectoryResolvedOnWorkerThread_ProducesRootedPath() {
        var toolNameUpper = "UNITTEST_" + Guid.NewGuid().ToString("N");

        var runtimeContext = new TelemetryRuntimeContext(toolNameUpper, typeof(TelemetryConfigTests));

        runtimeContext.EnsureRootDirectoryResolvedOnWorkerThread();

        var root = runtimeContext.GetRootDirectory();

        Path.IsPathRooted(root).Should().BeTrue();

        // Do not assert the OS-specific base directory, only that it contains "KeelMatrix/{ToolNameUpper}".
        var expectedSuffix = Path.Combine("KeelMatrix", toolNameUpper);
        root.Should().Contain(expectedSuffix);
    }

    [Theory]
    [InlineData(@"..\escape")]
    [InlineData("nested/tool")]
    [InlineData(@"nested\tool")]
    [InlineData(@"C:\absolute\tool")]
    [InlineData(@"\\server\share\tool")]
    [InlineData("tool<>:\"/\\\\|?*name")]
    public void ResolveRootDirectory_SanitizesUnsafeToolNames_AndKeepsThemUnderTelemetryBase(string toolName) {
        var safeRoot = TelemetryConfig.ResolveRootDirectory("BASELINE_TOOL");
        var root = TelemetryConfig.ResolveRootDirectory(toolName);

        Path.IsPathRooted(root).Should().BeTrue();

        var telemetryBase = Path.GetDirectoryName(safeRoot);
        telemetryBase.Should().NotBeNullOrWhiteSpace();
        root.Should().StartWith(telemetryBase + Path.DirectorySeparatorChar);

        var leaf = Path.GetFileName(root);
        leaf.Should().NotBeNullOrWhiteSpace();
        leaf.Should().NotBe(".");
        leaf.Should().NotBe("..");

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
            leaf.Should().NotContain(invalidChar.ToString());

        leaf.Should().NotContain(Path.DirectorySeparatorChar.ToString());
        leaf.Should().NotContain(Path.AltDirectorySeparatorChar.ToString());
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

    private static TestRepository CreateRepository(string startingPointRelativePath) {
        return new TestRepository(startingPointRelativePath);
    }

    private static string CreateSharedTempRoot() {
        var root = Path.Combine(Path.GetTempPath(), "KeelMatrix.Telemetry.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => TryDeleteDirectory(SharedTempRoot);
        return root;
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

    private sealed class StartingPointsOverrideScope : IDisposable {
        public StartingPointsOverrideScope(params string[] startingPoints) {
            GitDiscovery.SetStartingPointsOverrideForTests(startingPoints);
        }

        public void Dispose() {
            GitDiscovery.SetStartingPointsOverrideForTests(null);
        }
    }

    private sealed class TestRepository : IDisposable {
        public TestRepository(string startingPointRelativePath) {
            Root = Path.Combine(SharedTempRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Path.Combine(Root, ".git"));

            StartingPoint = Path.Combine(Root, startingPointRelativePath);
            Directory.CreateDirectory(StartingPoint);
        }

        public string Root { get; }
        public string StartingPoint { get; }

        public void Dispose() {
            // Cleanup is deferred to process exit so temp repos are removed once per test run.
        }
    }

    private static void TryDeleteDirectory(string path) {
        try {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch {
            // swallow
        }
    }
}

// Copyright (c) KeelMatrix

using FluentAssertions;
using KeelMatrix.Telemetry.ProjectIdentity;

namespace KeelMatrix.Telemetry.UnitTests;

// TelemetryConfig has static mutable state (runtime + env var reads + test hooks).
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
        TelemetryDisableResolver.SetRepositoryDisableOverrideForTests(null);
    }

    public void Dispose() {
        TelemetryConfig.ResetProcessDisabledForTests();
        GitDiscovery.SetStartingPointsOverrideForTests(null);
        TelemetryDisableResolver.SetRepositoryDisableOverrideForTests(null);
    }

    [Fact]
    public void IsTelemetryDisabled_ReturnsFalse_WhenAllOptOutVarsCleared() {
        using var _ = CreateEnvironmentSnapshot();

        ClearOptOutVars();

        TelemetryConfig.IsTelemetryDisabled().Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(GetTruthyValues))]
    public void IsTelemetryDisabled_ReturnsTrue_WhenKeelMatrixNoTelemetryTruthy(string truthy) {
        using var _ = CreateEnvironmentSnapshot();

        ClearOptOutVars();
        Environment.SetEnvironmentVariable(EnvKeelMatrixNoTelemetry, truthy);

        TelemetryConfig.IsTelemetryDisabled().Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(GetTruthyValues))]
    public void IsTelemetryDisabled_ReturnsTrue_WhenDotNetCliTelemetryOptOutTruthy(string truthy) {
        using var _ = CreateEnvironmentSnapshot();

        ClearOptOutVars();
        Environment.SetEnvironmentVariable(EnvDotNetCliTelemetryOptOut, truthy);

        TelemetryConfig.IsTelemetryDisabled().Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(GetTruthyValues))]
    public void IsTelemetryDisabled_ReturnsTrue_WhenDoNotTrackTruthy(string truthy) {
        using var _ = CreateEnvironmentSnapshot();

        ClearOptOutVars();
        Environment.SetEnvironmentVariable(EnvDoNotTrack, truthy);

        TelemetryConfig.IsTelemetryDisabled().Should().BeTrue();
    }

    [Fact]
    public void ResolveRepositoryTelemetryDisableOnWorkerThread_ReturnsTrue_WhenRepositoryConfigDisablesTelemetry() {
        using var _ = CreateEnvironmentSnapshot();
        using var repo = CreateRepository("src", "tool");
        using var __ = new StartingPointsOverrideScope(repo.StartingPoint);

        ClearOptOutVars();
        repo.WriteFile(
            """
            {
              "disabled": true
            }
            """,
            RepositoryConfigFileName);

        TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeTrue();
        TelemetryConfig.IsTelemetryDisabled().Should().BeTrue();
    }

    [Fact]
    public void ResolveRepositoryTelemetryDisableOnWorkerThread_ReturnsTrue_WhenDotEnvDisablesTelemetry() {
        using var _ = CreateEnvironmentSnapshot();
        using var repo = CreateRepository("src", "tool");
        using var __ = new StartingPointsOverrideScope(repo.StartingPoint);

        ClearOptOutVars();
        repo.WriteFile("KEELMATRIX_NO_TELEMETRY=1", ".env");

        TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeTrue();
    }

    [Fact]
    public void ResolveRepositoryTelemetryDisableOnWorkerThread_ReturnsTrue_WhenDotEnvLocalDisablesTelemetry() {
        using var _ = CreateEnvironmentSnapshot();
        using var repo = CreateRepository("src", "tool");
        using var __ = new StartingPointsOverrideScope(repo.StartingPoint);

        ClearOptOutVars();
        repo.WriteFile("KEELMATRIX_NO_TELEMETRY=1", ".env.local");

        TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeTrue();
    }

    [Fact]
    public void ResolveRepositoryTelemetryDisableOnWorkerThread_ReturnsTrue_WhenLaterRepositoryRootDisablesTelemetry() {
        using var _ = CreateEnvironmentSnapshot();
        using var repoWithoutOptOut = CreateRepository("src", "tool");
        using var repoWithOptOut = CreateRepository("tests", "tool");
        using var __ = new StartingPointsOverrideScope(repoWithoutOptOut.StartingPoint, repoWithOptOut.StartingPoint);

        ClearOptOutVars();
        repoWithOptOut.WriteFile("DOTNET_CLI_TELEMETRY_OPTOUT=1", ".env.local");

        TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeTrue();
    }

    [Fact]
    public void ResolveRepositoryTelemetryDisableOnWorkerThread_ReturnsFalse_WhenMultipleRepositoryRootsAreAllUnspecified() {
        using var _ = CreateEnvironmentSnapshot();
        using var repoA = CreateRepository("src", "tool");
        using var repoB = CreateRepository("tests", "tool");
        using var __ = new StartingPointsOverrideScope(repoA.StartingPoint, repoB.StartingPoint);

        ClearOptOutVars();

        TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeFalse();
        TelemetryConfig.IsTelemetryDisabled().Should().BeFalse();
    }

    [Fact]
    public void GetCandidateRepositoryRoots_DeduplicatesResolvedRepositoryRoots() {
        using var repo = CreateRepository("src", "tool");
        var otherStartingPoint = repo.CreateDirectory("tests", "tool");
        using var _ = new StartingPointsOverrideScope(repo.StartingPoint, otherStartingPoint);

        var roots = TelemetryDisableResolver.GetCandidateRepositoryRoots();

        roots.Should().ContainSingle().Which.Should().Be(repo.Root);
    }

    [Theory]
    [InlineData("Directory.Build.props")]
    [InlineData("global.json")]
    public void GetCandidateRepositoryRoots_PrefersGitRootOverNestedNonGitMarker(string markerFileName) {
        using var repo = CreateRepository("src", "nested", "tool");
        using var _ = new StartingPointsOverrideScope(repo.StartingPoint);

        repo.WriteFile("KEELMATRIX_NO_TELEMETRY=1", ".env.local");
        repo.WriteFile(string.Empty, "src", markerFileName);

        var roots = TelemetryDisableResolver.GetCandidateRepositoryRoots();

        roots.Should().ContainSingle().Which.Should().Be(repo.Root);
        TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeTrue();
    }

    [Fact]
    public void ResolveRepositoryTelemetryDisableOnWorkerThread_PrefersDotEnvLocalOverDotEnv_WhenBothExist() {
        using var _ = CreateEnvironmentSnapshot();
        using var repo = CreateRepository("src", "tool");
        using var __ = new StartingPointsOverrideScope(repo.StartingPoint);

        ClearOptOutVars();
        repo.WriteFile("KEELMATRIX_NO_TELEMETRY=1", ".env");
        repo.WriteFile("KEELMATRIX_NO_TELEMETRY=0", ".env.local");

        TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeFalse();
    }

    [Fact]
    public void ResolveRepositoryTelemetryDisableOnWorkerThread_PrefersRepositoryConfigOverDotEnvLocal() {
        using var _ = CreateEnvironmentSnapshot();
        using var repo = CreateRepository("src", "tool");
        using var __ = new StartingPointsOverrideScope(repo.StartingPoint);

        ClearOptOutVars();
        repo.WriteFile(
            """
            {
              "disabled": false
            }
            """,
            RepositoryConfigFileName);
        repo.WriteFile("KEELMATRIX_NO_TELEMETRY=1", ".env.local");

        TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeFalse();
    }

    [Fact]
    public void ResolveRepositoryTelemetryDisableOnWorkerThread_ContinuesPastInvalidRepositoryConfigInEarlierRepositoryRoot() {
        using var _ = CreateEnvironmentSnapshot();
        using var repoWithInvalidConfig = CreateRepository("src", "tool");
        using var repoWithOptOut = CreateRepository("tests", "tool");
        using var __ = new StartingPointsOverrideScope(repoWithInvalidConfig.StartingPoint, repoWithOptOut.StartingPoint);

        ClearOptOutVars();
        repoWithInvalidConfig.WriteFile("{ not-valid-json", RepositoryConfigFileName);
        repoWithOptOut.WriteFile("DO_NOT_TRACK=1", ".env.local");

        Action act = () => TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeTrue();
        act.Should().NotThrow();
    }

    [Fact]
    public void ResolveRepositoryTelemetryDisableOnWorkerThread_IgnoresUnrelatedDotEnvKeys() {
        using var _ = CreateEnvironmentSnapshot();
        using var repo = CreateRepository("src", "tool");
        using var __ = new StartingPointsOverrideScope(repo.StartingPoint);

        ClearOptOutVars();
        repo.WriteFile("UNRELATED_KEY=1", ".env");

        TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeFalse();
    }

    [Fact]
    public void ResolveRepositoryTelemetryDisableOnWorkerThread_AppliesOnlyWithinResolvedRepositoryRoot() {
        using var _ = CreateEnvironmentSnapshot();
        using var repoA = CreateRepository("src", "tool");
        using var repoB = CreateRepository("src", "tool");

        ClearOptOutVars();
        repoA.WriteFile("KEELMATRIX_NO_TELEMETRY=1", ".env");

        using (var scope = new StartingPointsOverrideScope(repoA.StartingPoint)) {
            TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeTrue();
        }

        TelemetryConfig.ResetProcessDisabledForTests();

        using (var scope = new StartingPointsOverrideScope(repoB.StartingPoint)) {
            TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeFalse();
        }
    }

    [Fact]
    public void ResolveRepositoryTelemetryDisableOnWorkerThread_ReturnsFalse_WhenRepositoryRootHasNoSupportedFiles() {
        using var _ = CreateEnvironmentSnapshot();
        using var repo = CreateRepository("src", "tool");
        using var __ = new StartingPointsOverrideScope(repo.StartingPoint);

        ClearOptOutVars();

        TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeFalse();
    }

    [Fact]
    public void ResolveRepositoryTelemetryDisableOnWorkerThread_SupportsCaseInsensitiveExportPrefixInDotEnvFiles() {
        using var _ = CreateEnvironmentSnapshot();
        using var repo = CreateRepository("src", "tool");
        using var __ = new StartingPointsOverrideScope(repo.StartingPoint);

        ClearOptOutVars();
        repo.WriteFile("EXPORT KEELMATRIX_NO_TELEMETRY=1", ".env");

        TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeTrue();
    }

    [Fact]
    public void ResolveRepositoryTelemetryDisableOnWorkerThread_ReturnsFalse_WhenRepositoryFileValuesAreFalseyOrAbsent() {
        using var _ = CreateEnvironmentSnapshot();
        using var repo = CreateRepository("src", "tool");
        using var __ = new StartingPointsOverrideScope(repo.StartingPoint);

        ClearOptOutVars();
        repo.WriteFile("KEELMATRIX_NO_TELEMETRY=0", ".env");
        repo.WriteFile("UNRELATED_KEY=1", ".env.local");

        TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeFalse();
    }

    [Fact]
    public void ResolveRepositoryTelemetryDisableOnWorkerThread_DoesNotInspectRepoFiles_WhenProcessEnvIsPresentButFalsey() {
        using var _ = CreateEnvironmentSnapshot();
        using var repo = CreateRepository("src", "tool");
        using var __ = new StartingPointsOverrideScope(repo.StartingPoint);

        ClearOptOutVars();
        Environment.SetEnvironmentVariable(EnvKeelMatrixNoTelemetry, "false");
        repo.WriteFile("KEELMATRIX_NO_TELEMETRY=1", ".env");

        TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeFalse();
        TelemetryConfig.IsTelemetryDisabled().Should().BeFalse();
    }

    [Fact]
    public void ResolveRepositoryTelemetryDisableOnWorkerThread_MemoizesNegativeRepositoryDecisionForSameRepositoryRootSet() {
        using var _ = CreateEnvironmentSnapshot();
        using var repo = CreateRepository("src", "tool");
        using var __ = new StartingPointsOverrideScope(repo.StartingPoint);

        ClearOptOutVars();

        int calls = 0;
        TelemetryDisableResolver.SetRepositoryDisableOverrideForTests(() => {
            Interlocked.Increment(ref calls);
            return false;
        });

        TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeFalse();
        TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeFalse();

        calls.Should().Be(1);
        TelemetryConfig.IsTelemetryDisabled().Should().BeFalse();
    }

    [Fact]
    public void ResolveRepositoryTelemetryDisableOnWorkerThread_ReevaluatesWhenRepositoryRootSetChangesInSameProcess() {
        using var _ = CreateEnvironmentSnapshot();
        using var repoWithoutOptOut = CreateRepository("src", "tool");
        using var repoWithOptOut = CreateRepository("tests", "tool");

        ClearOptOutVars();
        repoWithOptOut.WriteFile("KEELMATRIX_NO_TELEMETRY=1", ".env.local");

        using (var scope = new StartingPointsOverrideScope(repoWithoutOptOut.StartingPoint)) {
            TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeFalse();
            TelemetryConfig.IsTelemetryDisabled().Should().BeFalse();
        }

        using (var scope = new StartingPointsOverrideScope(repoWithOptOut.StartingPoint)) {
            TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeTrue();
            TelemetryConfig.IsTelemetryDisabled().Should().BeTrue();
        }
    }

    [Fact]
    public void ResolveRepositoryTelemetryDisableOnWorkerThread_MemoizesPositiveRepositoryDecisionAndPromotesProcessDisable() {
        using var _ = CreateEnvironmentSnapshot();
        using var repo = CreateRepository("src", "tool");
        using var __ = new StartingPointsOverrideScope(repo.StartingPoint);

        ClearOptOutVars();

        int calls = 0;
        TelemetryDisableResolver.SetRepositoryDisableOverrideForTests(() => {
            Interlocked.Increment(ref calls);
            return true;
        });

        TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeTrue();
        TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeTrue();

        calls.Should().Be(1);
        TelemetryConfig.IsTelemetryDisabled().Should().BeTrue();
    }

    [Fact]
    public void ResetProcessDisabledForTests_ClearsMemoizedRepositoryDecision() {
        using var _ = CreateEnvironmentSnapshot();

        ClearOptOutVars();

        int calls = 0;
        TelemetryDisableResolver.SetRepositoryDisableOverrideForTests(() => {
            Interlocked.Increment(ref calls);
            return false;
        });

        TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeFalse();
        calls.Should().Be(1);

        TelemetryConfig.ResetProcessDisabledForTests();
        TelemetryDisableResolver.SetRepositoryDisableOverrideForTests(() => {
            Interlocked.Increment(ref calls);
            return false;
        });

        TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeFalse();
        calls.Should().Be(2);
    }

    [Fact]
    public void ResetProcessDisabledForTests_ClearsRepositoryDisableOverride() {
        using var _ = CreateEnvironmentSnapshot();

        ClearOptOutVars();

        TelemetryDisableResolver.SetRepositoryDisableOverrideForTests(() => true);

        TelemetryConfig.ResetProcessDisabledForTests();

        TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread().Should().BeFalse();
        TelemetryConfig.IsTelemetryDisabled().Should().BeFalse();
    }

    [Fact]
    public void ClientConstruction_DoesNotResolveRepositoryOptOutOnCallerThread() {
        using var _ = CreateEnvironmentSnapshot();
        using var signal = new ManualResetEventSlim(false);

        ClearOptOutVars();

        int callerThreadId = Environment.CurrentManagedThreadId;
        int observedThreadId = 0;

        TelemetryDisableResolver.SetRepositoryDisableOverrideForTests(() => {
            observedThreadId = Environment.CurrentManagedThreadId;
            signal.Set();
            return null;
        });

        var client = new Client("UNITTEST_" + Guid.NewGuid().ToString("N"), typeof(TelemetryConfigTests));
        try {
            client.TrackActivation();

            signal.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            observedThreadId.Should().NotBe(callerThreadId);
        }
        finally {
            DisposeClientWorker(client);
        }
    }

    [Fact]
    public void RuntimeContext_SetsToolNameLowercase_AndKeepsRootDirectoryUnresolvedUntilWorkerThread() {
        var toolNameUpper = "UNITTEST_" + Guid.NewGuid().ToString("N");

        var runtimeContext = new TelemetryRuntimeContext(toolNameUpper, typeof(TelemetryConfigTests));

        runtimeContext.ToolName.Should().Be(toolNameUpper.ToLowerInvariant());

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

    private static EnvironmentVariableSnapshot CreateEnvironmentSnapshot() {
        return new EnvironmentVariableSnapshot(
            EnvKeelMatrixNoTelemetry,
            EnvDotNetCliTelemetryOptOut,
            EnvDoNotTrack);
    }

    private static void ClearOptOutVars() {
        Environment.SetEnvironmentVariable(EnvKeelMatrixNoTelemetry, null);
        Environment.SetEnvironmentVariable(EnvDotNetCliTelemetryOptOut, null);
        Environment.SetEnvironmentVariable(EnvDoNotTrack, null);
    }

    private static TestRepository CreateRepository(params string[] startingPointSegments) {
        return new TestRepository(includeGitRoot: true, startingPointSegments);
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
        public TestRepository(bool includeGitRoot, params string[] startingPointSegments) {
            Root = Path.Combine(SharedTempRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);

            if (includeGitRoot)
                Directory.CreateDirectory(Path.Combine(Root, ".git"));

            StartingPoint = CreateDirectory(startingPointSegments);
        }

        public string Root { get; }
        public string StartingPoint { get; }

        public string CreateDirectory(params string[] segments) {
            var path = CombineWithRoot(segments);
            Directory.CreateDirectory(path);
            return path;
        }

        public void WriteFile(string contents, params string[] segments) {
            var path = CombineWithRoot(segments);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, contents);
        }

        public void Dispose() {
            // Cleanup is deferred to process exit so temp repos are removed once per test run.
        }

        private string CombineWithRoot(params string[] segments) {
            var path = Root;
            foreach (var segment in segments)
                path = Path.Combine(path, segment);

            return path;
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

    private static void DisposeClientWorker(Client client) {
        try {
            var innerField = typeof(Client).GetField("client", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var inner = innerField?.GetValue(client);
            if (inner is not TelemetryClient telemetryClient)
                return;

            var workerField = typeof(TelemetryClient).GetField("worker", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var worker = workerField?.GetValue(telemetryClient) as IDisposable;
            worker?.Dispose();
        }
        catch {
            // swallow
        }
    }
}

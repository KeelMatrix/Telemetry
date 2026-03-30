// Copyright (c) KeelMatrix

using FluentAssertions;

namespace KeelMatrix.Telemetry.UnitTests;

[Collection(TelemetryConfigTestsCollectionDefinition.Name)]
public sealed class RepositoryTelemetryTests : IDisposable {
    private const string EnvKeelMatrixNoTelemetry = "KEELMATRIX_NO_TELEMETRY";
    private const string EnvDotNetCliTelemetryOptOut = "DOTNET_CLI_TELEMETRY_OPTOUT";
    private const string EnvDoNotTrack = "DO_NOT_TRACK";
    private static readonly string SharedTempRoot = CreateSharedTempRoot();

    public void Dispose() {
        ClearOptOutVars();
    }

    [Fact]
    public void TryResolveRepositoryRoot_ReturnsGitRoot_FromExplicitStartingDirectory() {
        using var repo = CreateRepository("src", "tool");

        bool resolved = RepositoryTelemetry.TryResolveRepositoryRoot(repo.StartingDirectory, out string repositoryRoot);

        resolved.Should().BeTrue();
        repositoryRoot.Should().Be(repo.Root);
    }

    [Theory]
    [InlineData("Directory.Build.props")]
    [InlineData("global.json")]
    public void TryResolveRepositoryRoot_PrefersGitRootOverNestedNonGitMarker(string markerFileName) {
        using var repo = CreateRepository("src", "nested", "tool");
        repo.WriteFile(string.Empty, "src", markerFileName);

        bool resolved = RepositoryTelemetry.TryResolveRepositoryRoot(repo.StartingDirectory, out string repositoryRoot);

        resolved.Should().BeTrue();
        repositoryRoot.Should().Be(repo.Root);
    }

    [Fact]
    public void GetEffectiveStatus_ReturnsRepoLocalDefault_WhenNoOverridesExist() {
        using var _ = CreateEnvironmentSnapshot();
        using var repo = CreateRepository("src", "tool");

        ClearOptOutVars();

        RepositoryTelemetryStatus status = RepositoryTelemetry.GetEffectiveStatus(repo.Root);

        status.IsEnabled.Should().BeTrue();
        status.WinningSourceKind.Should().Be(RepositoryTelemetrySourceKind.None);
        status.WinningPath.Should().BeNull();
        status.WinningVariableName.Should().BeNull();
        status.Scope.Should().Be(RepositoryTelemetryScope.RepoLocalDefault);
        status.RepoRoot.Should().Be(repo.Root);
    }

    [Fact]
    public void GetEffectiveStatus_PrefersRepositoryConfigOverDotEnvLocal() {
        using var _ = CreateEnvironmentSnapshot();
        using var repo = CreateRepository("src", "tool");

        ClearOptOutVars();
        repo.WriteFile(
            """
            {
              "disabled": false
            }
            """,
            "keelmatrix.telemetry.json");
        repo.WriteFile("KEELMATRIX_NO_TELEMETRY=1", ".env.local");

        RepositoryTelemetryStatus status = RepositoryTelemetry.GetEffectiveStatus(repo.Root);

        status.IsEnabled.Should().BeTrue();
        status.WinningSourceKind.Should().Be(RepositoryTelemetrySourceKind.RepositoryConfig);
        status.WinningPath.Should().Be(Path.Combine(repo.Root, "keelmatrix.telemetry.json"));
        status.WinningVariableName.Should().BeNull();
        status.Scope.Should().Be(RepositoryTelemetryScope.RepoLocal);
    }

    [Fact]
    public void GetEffectiveStatus_PrefersDotEnvLocalOverDotEnv() {
        using var _ = CreateEnvironmentSnapshot();
        using var repo = CreateRepository("src", "tool");

        ClearOptOutVars();
        repo.WriteFile("KEELMATRIX_NO_TELEMETRY=1", ".env");
        repo.WriteFile("KEELMATRIX_NO_TELEMETRY=0", ".env.local");

        RepositoryTelemetryStatus status = RepositoryTelemetry.GetEffectiveStatus(repo.Root);

        status.IsEnabled.Should().BeTrue();
        status.WinningSourceKind.Should().Be(RepositoryTelemetrySourceKind.DotEnvLocal);
        status.WinningPath.Should().Be(Path.Combine(repo.Root, ".env.local"));
        status.WinningVariableName.Should().Be("KEELMATRIX_NO_TELEMETRY");
        status.Scope.Should().Be(RepositoryTelemetryScope.RepoLocal);
    }

    [Fact]
    public void GetEffectiveStatus_PrefersProcessEnvironmentOverRepositoryFiles() {
        using var _ = CreateEnvironmentSnapshot();
        using var repo = CreateRepository("src", "tool");

        ClearOptOutVars();
        Environment.SetEnvironmentVariable(EnvKeelMatrixNoTelemetry, "0");
        repo.WriteFile(
            """
            {
              "disabled": true
            }
            """,
            "keelmatrix.telemetry.json");
        repo.WriteFile("DO_NOT_TRACK=1", ".env.local");

        RepositoryTelemetryStatus status = RepositoryTelemetry.GetEffectiveStatus(repo.Root);

        status.IsEnabled.Should().BeTrue();
        status.WinningSourceKind.Should().Be(RepositoryTelemetrySourceKind.ProcessEnvironment);
        status.WinningPath.Should().BeNull();
        status.WinningVariableName.Should().Be(EnvKeelMatrixNoTelemetry);
        status.Scope.Should().Be(RepositoryTelemetryScope.ProcessEnvironment);
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

    private static TestRepository CreateRepository(params string[] startingDirectorySegments) {
        return new TestRepository(includeGitRoot: true, startingDirectorySegments);
    }

    private static string CreateSharedTempRoot() {
        string root = Path.Combine(Path.GetTempPath(), "KeelMatrix.Telemetry.RepositoryTelemetry.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => TryDeleteDirectory(SharedTempRoot);
        return root;
    }

    private static void TryDeleteDirectory(string path) {
        try {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch {
            // Best-effort cleanup at process exit.
        }
    }

    private sealed class EnvironmentVariableSnapshot : IDisposable {
        private readonly (string Name, string? Value)[] snapshot;

        public EnvironmentVariableSnapshot(params string[] names) {
            snapshot = new (string, string?)[names.Length];
            for (int i = 0; i < names.Length; i++) {
                string name = names[i];
                snapshot[i] = (name, Environment.GetEnvironmentVariable(name));
            }
        }

        public void Dispose() {
            foreach (var (name, value) in snapshot)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    private sealed class TestRepository : IDisposable {
        public TestRepository(bool includeGitRoot, params string[] startingDirectorySegments) {
            Root = Path.Combine(SharedTempRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);

            if (includeGitRoot)
                Directory.CreateDirectory(Path.Combine(Root, ".git"));

            StartingDirectory = CreateDirectory(startingDirectorySegments);
        }

        public string Root { get; }
        public string StartingDirectory { get; }

        public void WriteFile(string contents, params string[] segments) {
            string path = CombineWithRoot(segments);
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, contents);
        }

        public void Dispose() {
            // Cleanup is deferred to process exit so temp repos are removed once per test run.
        }

        private string CreateDirectory(params string[] segments) {
            string path = CombineWithRoot(segments);
            Directory.CreateDirectory(path);
            return path;
        }

        private string CombineWithRoot(params string[] segments) {
            string path = Root;
            foreach (string segment in segments)
                path = Path.Combine(path, segment);

            return path;
        }
    }
}

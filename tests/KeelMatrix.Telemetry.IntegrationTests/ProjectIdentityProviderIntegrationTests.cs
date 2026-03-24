// Copyright (c) KeelMatrix

using FluentAssertions;
using KeelMatrix.Telemetry.ProjectIdentity;

namespace KeelMatrix.Telemetry.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public static class ProjectIdentityProviderIntegrationTestsCollectionDefinition {
    public const string Name = $"{nameof(ProjectIdentityProviderIntegrationTests)}.NonParallel";
}

[Collection(ProjectIdentityProviderIntegrationTestsCollectionDefinition.Name)]
public sealed class ProjectIdentityProviderIntegrationTests : IDisposable {
    private readonly string tempRoot;
    private readonly HashSet<string> runtimeRoots = new(StringComparer.OrdinalIgnoreCase);

    public ProjectIdentityProviderIntegrationTests() {
        tempRoot = Path.Combine(Path.GetTempPath(), "KeelMatrix.Telemetry.ProjectIdentityProviderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        ResetProcessDisabledForTests();
    }

    [Fact]
    public void SameRepo_LocalGitRemote_AndCiEnvVars_ResolveSameProjectHash() {
        var repoDir = CreateGitRepo("same-repo-ci-local", "git@github.com:KeelMatrix/Telemetry.git");

        using var localEnv = TestEnvironmentScope.Clean();
        var local = ResolveIdentities(
            repoDir,
            CreateToolName("LOCAL"),
            runtimeInfo => runtimeInfo.SetCiOverrideForTests(false));

        using var ciEnv = TestEnvironmentScope.Clean(
            ("CI", "true"),
            ("GITHUB_SERVER_URL", "https://github.com"),
            ("GITHUB_REPOSITORY", "KeelMatrix/Telemetry"));

        var ci = ResolveIdentities(
            repoDir,
            CreateToolName("CI"),
            runtimeInfo => runtimeInfo.SetCiOverrideForTests(true));

        local.HasProjectIdentity.Should().BeTrue();
        ci.HasProjectIdentity.Should().BeTrue();
        local.ProjectHash.Should().Be(ci.ProjectHash);
    }

    [Fact]
    public void SameRepo_DifferentPersistedSalts_KeepProjectHashStable_AndChangeInstallationHash() {
        var repoDir = CreateGitRepo("same-repo-different-salts", "https://github.com/KeelMatrix/Telemetry.git");

        using var env = TestEnvironmentScope.Clean();

        var first = ResolveIdentities(
            repoDir,
            CreateToolName("SALT_A"),
            persistedSaltHex: new string('1', TelemetryConfig.ExpectedSaltBytes * 2));

        var second = ResolveIdentities(
            repoDir,
            CreateToolName("SALT_B"),
            persistedSaltHex: new string('2', TelemetryConfig.ExpectedSaltBytes * 2));

        first.HasProjectIdentity.Should().BeTrue();
        second.HasProjectIdentity.Should().BeTrue();
        first.ProjectHash.Should().Be(second.ProjectHash);
        first.InstallationHash.Should().NotBe(second.InstallationHash);
    }

    [Fact]
    public void SameRepo_DifferentCheckoutPaths_ResolveSameProjectHash() {
        var firstRepoDir = CreateGitRepo("checkout-a", "https://github.com/KeelMatrix/Telemetry.git");
        var secondRepoDir = CreateGitRepo("checkout-b", "ssh://git@github.com/KeelMatrix/Telemetry.git");

        using var env = TestEnvironmentScope.Clean();

        var first = ResolveIdentities(firstRepoDir, CreateToolName("CHECKOUT_A"));
        var second = ResolveIdentities(secondRepoDir, CreateToolName("CHECKOUT_B"));

        first.ProjectHash.Should().Be(second.ProjectHash);
    }

    [Fact]
    public void DifferentRepos_ResolveDifferentProjectHash() {
        var firstRepoDir = CreateGitRepo("repo-one", "https://github.com/KeelMatrix/Telemetry.git");
        var secondRepoDir = CreateGitRepo("repo-two", "https://github.com/KeelMatrix/Other.git");

        using var env = TestEnvironmentScope.Clean();

        var first = ResolveIdentities(firstRepoDir, CreateToolName("REPO_ONE"));
        var second = ResolveIdentities(secondRepoDir, CreateToolName("REPO_TWO"));

        first.ProjectHash.Should().NotBe(second.ProjectHash);
    }

    [Fact]
    public void SameProjectFileStructure_DifferentPaths_ResolveSameProjectHashWithoutGit() {
        var firstProjectDir = CreateProjectFileOnlyRoot("project-files-a");
        var secondProjectDir = CreateProjectFileOnlyRoot("project-files-b");

        using var env = TestEnvironmentScope.Clean();

        var first = ResolveIdentities(firstProjectDir, CreateToolName("FILES_A"));
        var second = ResolveIdentities(secondProjectDir, CreateToolName("FILES_B"));

        first.HasProjectIdentity.Should().BeTrue();
        second.HasProjectIdentity.Should().BeTrue();
        first.ProjectHash.Should().Be(second.ProjectHash);
    }

    [Fact]
    public void NoStableProjectIdentity_ReturnsUnavailableProjectIdentity_AndStillResolvesInstallationHash() {
        var emptyDir = CreateEmptyRoot("no-stable-identity");

        using var env = TestEnvironmentScope.Clean();

        var identities = ResolveIdentities(emptyDir, CreateToolName("NO_IDENTITY"));

        identities.HasProjectIdentity.Should().BeFalse();
        identities.ProjectHash.Should().BeNull();
        identities.InstallationHash.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void SameInstallationRoot_WithSamePersistedSalt_ResolvesSameInstallationHash() {
        var repoDir = CreateGitRepo("same-install-root", "https://github.com/KeelMatrix/Telemetry.git");
        var toolName = CreateToolName("INSTALL_ROOT");

        using var env = TestEnvironmentScope.Clean();

        var first = ResolveIdentities(
            repoDir,
            toolName,
            persistedSaltHex: new string('a', TelemetryConfig.ExpectedSaltBytes * 2));

        var second = ResolveIdentities(
            repoDir,
            toolName,
            resetRoot: false);

        first.InstallationHash.Should().Be(second.InstallationHash);
        first.ProjectHash.Should().Be(second.ProjectHash);
    }

    public void Dispose() {
        GitDiscovery.SetStartingPointsOverrideForTests(null);

        foreach (var root in runtimeRoots) {
            TryDeleteDirectory(root);
        }

        TryDeleteDirectory(tempRoot);
    }

    private ResolvedTelemetryIdentity ResolveIdentities(
        string startingPoint,
        string toolNameUpper,
        Action<RuntimeInfo>? configureRuntimeInfo = null,
        string? persistedSaltHex = null,
        bool resetRoot = true) {

        ResetProcessDisabledForTests();

        var runtimeContext = new TelemetryRuntimeContext(toolNameUpper, typeof(ProjectIdentityProviderIntegrationTests));
        runtimeContext.EnsureRootDirectoryResolvedOnWorkerThread();

        var rootDir = runtimeContext.GetRootDirectory();
        runtimeRoots.Add(rootDir);

        if (resetRoot) {
            TryDeleteDirectory(rootDir);
        }

        if (!string.IsNullOrWhiteSpace(persistedSaltHex)) {
            Directory.CreateDirectory(rootDir);
            File.WriteAllText(Path.Combine(rootDir, "telemetry.salt"), persistedSaltHex);
        }

        var runtimeInfo = new RuntimeInfo();
        configureRuntimeInfo?.Invoke(runtimeInfo);

        using var _ = new StartingPointsOverrideScope(startingPoint);
        return new ProjectIdentityProvider(runtimeContext, runtimeInfo).EnsureResolvedOnWorkerThread();
    }

    private string CreateGitRepo(string name, string originRemoteUrl) {
        var repoDir = Path.Combine(tempRoot, name);
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        File.WriteAllText(
            Path.Combine(repoDir, ".git", "config"),
            """
            [remote "origin"]
                url = PLACEHOLDER_REMOTE
            """
                .Replace("PLACEHOLDER_REMOTE", originRemoteUrl, StringComparison.Ordinal));
        return repoDir;
    }

    private string CreateProjectFileOnlyRoot(string name) {
        var root = Path.Combine(tempRoot, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Repo.sln"), "Microsoft Visual Studio Solution File, Format Version 12.00\n");
        File.WriteAllText(
            Path.Combine(root, "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        return root;
    }

    private string CreateEmptyRoot(string name) {
        var root = Path.Combine(tempRoot, name);
        Directory.CreateDirectory(root);
        return root;
    }

    private static void ResetProcessDisabledForTests() {
        var field = typeof(TelemetryConfig).GetField("processDisabled",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        field?.SetValue(null, 0);
    }

    private static string CreateToolName(string prefix) {
        return $"PROJECTIDENTITY_{prefix}_{Guid.NewGuid():N}";
    }

    private static void TryDeleteDirectory(string dir) {
        try {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch {
            // swallow
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

    private sealed class TestEnvironmentScope : IDisposable {
        private static readonly string[] KnownVars = [
            "KEELMATRIX_NO_TELEMETRY",
            "DOTNET_CLI_TELEMETRY_OPTOUT",
            "DO_NOT_TRACK",
            "CI",
            "GITHUB_SERVER_URL",
            "GITHUB_REPOSITORY",
            "CI_PROJECT_URL",
            "CI_SERVER_URL",
            "CI_PROJECT_PATH",
            "SYSTEM_COLLECTIONURI",
            "BUILD_REPOSITORY_NAME",
            "BUILD_REPOSITORY_URI",
            "BITBUCKET_GIT_HTTP_ORIGIN",
            "BITBUCKET_REPO_FULL_NAME",
            "BITBUCKET_WORKSPACE",
            "BITBUCKET_REPO_SLUG"
        ];

        private readonly (string Name, string? Value)[] saved;

        public static TestEnvironmentScope Clean(params (string Name, string? Value)[] changes) {
            var allChanges = new List<(string Name, string? Value)>(KnownVars.Length + changes.Length);
            foreach (var name in KnownVars) {
                allChanges.Add((name, null));
            }

            allChanges.AddRange(changes);
            return new TestEnvironmentScope([.. allChanges]);
        }

        private TestEnvironmentScope(params (string Name, string? Value)[] changes) {
            saved = new (string, string?)[changes.Length];

            for (var i = 0; i < changes.Length; i++) {
                var (name, value) = changes[i];
                saved[i] = (name, Environment.GetEnvironmentVariable(name));
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose() {
            foreach (var (name, value) in saved) {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}

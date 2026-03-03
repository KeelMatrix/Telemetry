// Copyright (c) KeelMatrix

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using KeelMatrix.Telemetry.ProjectIdentity;

namespace KeelMatrix.Telemetry.CiEnvironmentTests;

[CollectionDefinition("CI environment", DisableParallelization = true)]
public sealed class CiEnvironmentCollectionDefinition;

[Collection("CI environment")]
public sealed class CiGitIdentityFingerprintTests {
    [Fact]
    public void TryComputeFromCi_GitHubActions_UsesServerUrlAndRepository() {
        using var _ = EnvVarScope.Clean(
            ("CI", "true"),
            ("GITHUB_SERVER_URL", "https://GitHub.com"),
            ("GITHUB_REPOSITORY", "KeelMatrix/Telemetry"));

        using var __ = new RuntimeInfoCiScope(isCi: true);

        InvokeTryComputeFromCi(out var bytes).Should().BeTrue();

        bytes.Should().Equal(ExpectedCiFingerprint("https://github.com/keelmatrix/telemetry"));
    }

    [Fact]
    public void TryComputeFromCi_GitLab_UsesCI_PROJECT_URL() {
        using var _ = EnvVarScope.Clean(
            ("CI", "true"),
            ("CI_PROJECT_URL", "https://gitlab.com/Group/SubGroup/Repo.git"));

        using var __ = new RuntimeInfoCiScope(isCi: true);

        InvokeTryComputeFromCi(out var bytes).Should().BeTrue();
        bytes.Should().Equal(ExpectedCiFingerprint("https://gitlab.com/group/subgroup/repo"));
    }

    [Fact]
    public void TryComputeFromCi_GitLab_UsesCI_SERVER_URL_And_CI_PROJECT_PATH() {
        using var _ = EnvVarScope.Clean(
            ("CI", "true"),
            ("CI_SERVER_URL", "https://gitlab.example.com/"),
            ("CI_PROJECT_PATH", "Group/Repo"));

        using var __ = new RuntimeInfoCiScope(isCi: true);

        InvokeTryComputeFromCi(out var bytes).Should().BeTrue();
        bytes.Should().Equal(ExpectedCiFingerprint("https://gitlab.example.com/group/repo"));
    }

    [Fact]
    public void TryComputeFromCi_AzureDevOps_UsesSYSTEM_COLLECTIONURI_And_BUILD_REPOSITORY_NAME() {
        using var _ = EnvVarScope.Clean(
            ("CI", "true"),
            ("SYSTEM_COLLECTIONURI", "https://dev.azure.com/Org/"),
            ("BUILD_REPOSITORY_NAME", "Project/Repo"));

        using var __ = new RuntimeInfoCiScope(isCi: true);

        InvokeTryComputeFromCi(out var bytes).Should().BeTrue();
        bytes.Should().Equal(ExpectedCiFingerprint("https://dev.azure.com/org/project/repo"));
    }

    [Fact]
    public void TryComputeFromCi_AzureDevOps_UsesBUILD_REPOSITORY_URI() {
        using var _ = EnvVarScope.Clean(
            ("CI", "true"),
            ("BUILD_REPOSITORY_URI", "https://dev.azure.com/Org/Project/_git/Repo"));

        using var __ = new RuntimeInfoCiScope(isCi: true);

        InvokeTryComputeFromCi(out var bytes).Should().BeTrue();
        bytes.Should().Equal(ExpectedCiFingerprint("https://dev.azure.com/org/project/_git/repo"));
    }

    [Fact]
    public void TryComputeFromCi_Bitbucket_UsesBITBUCKET_GIT_HTTP_ORIGIN_And_BITBUCKET_REPO_FULL_NAME() {
        using var _ = EnvVarScope.Clean(
            ("CI", "true"),
            ("BITBUCKET_GIT_HTTP_ORIGIN", "https://bitbucket.org"),
            ("BITBUCKET_REPO_FULL_NAME", "Workspace/Repo"));

        using var __ = new RuntimeInfoCiScope(isCi: true);

        InvokeTryComputeFromCi(out var bytes).Should().BeTrue();
        bytes.Should().Equal(ExpectedCiFingerprint("https://bitbucket.org/workspace/repo"));
    }

    [Fact]
    public void TryComputeFromCi_Bitbucket_Fallback_UsesWorkspaceAndSlug() {
        using var _ = EnvVarScope.Clean(
            ("CI", "true"),
            ("BITBUCKET_GIT_HTTP_ORIGIN", "https://bitbucket.org/"),
            ("BITBUCKET_WORKSPACE", "Workspace"),
            ("BITBUCKET_REPO_SLUG", "Repo"));

        using var __ = new RuntimeInfoCiScope(isCi: true);

        InvokeTryComputeFromCi(out var bytes).Should().BeTrue();
        bytes.Should().Equal(ExpectedCiFingerprint("https://bitbucket.org/workspace/repo"));
    }

    [Fact]
    public void TryComputeFromCi_ReturnsFalse_WhenNoProviderVarsPresent() {
        using var _ = EnvVarScope.Clean(("CI", "true"));
        using var __ = new RuntimeInfoCiScope(isCi: true);

        InvokeTryComputeFromCi(out var _).Should().BeFalse();
    }

    [Fact]
    public void Normalization_IsApplied_BeforeHashing() {
        // Inputs are deliberately mixed-case, with trailing .git and a non-canonical scheme.
        // The expected hash is computed against the *normalized* key.
        using var _ = EnvVarScope.Clean(
            ("CI", "true"),
            ("GITHUB_SERVER_URL", "SSH://GitHub.COM"),
            ("GITHUB_REPOSITORY", "KeelMatrix/Telemetry.Git"));

        using var __ = new RuntimeInfoCiScope(isCi: true);

        InvokeTryComputeFromCi(out var bytes).Should().BeTrue();
        bytes.Should().Equal(ExpectedCiFingerprint("https://github.com/keelmatrix/telemetry"));
    }

    private static bool InvokeTryComputeFromCi(out byte[] fingerprintBytes) {
        var mi = typeof(CiGitIdentityFingerprint).GetMethod(
            "TryComputeFromCi",
            BindingFlags.NonPublic | BindingFlags.Static);

        mi.Should().NotBeNull();

        object?[] args = [null!];
        var ok = (bool)mi!.Invoke(null, args)!;
        fingerprintBytes = (byte[])args[0]!;
        return ok;
    }

    private static byte[] ExpectedCiFingerprint(string expectedNormalizedRepoKey) {
        // Mirrors CiGitIdentityFingerprint: SHA256( UTF8("ci.v1") + UTF8(normalizedRepoKey) )
        var prefix = Encoding.UTF8.GetBytes("ci.v1");
        var key = Encoding.UTF8.GetBytes(expectedNormalizedRepoKey);
        var payload = new byte[prefix.Length + key.Length];
        Buffer.BlockCopy(prefix, 0, payload, 0, prefix.Length);
        Buffer.BlockCopy(key, 0, payload, prefix.Length, key.Length);
        return SHA256.HashData(payload);
    }

    private sealed class EnvVarScope : IDisposable {
        private static readonly string[] KnownCiVars = [
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

        public static EnvVarScope Clean(params (string Name, string? Value)[] changes) {
            // Clear all known vars first to avoid test pollution from the host environment.
            var allChanges = new List<(string Name, string? Value)>(KnownCiVars.Length + changes.Length);
            foreach (var name in KnownCiVars) allChanges.Add((name, null));
            allChanges.AddRange(changes);
            return new EnvVarScope([.. allChanges]);
        }


        private readonly (string Name, string? Value)[] saved;

        public EnvVarScope(params (string Name, string? Value)[] changes) {
            saved = new (string, string?)[changes.Length];

            for (var i = 0; i < changes.Length; i++) {
                var (name, value) = changes[i];
                saved[i] = (name, Environment.GetEnvironmentVariable(name));
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose() {
            for (var i = 0; i < saved.Length; i++) {
                var (name, value) = saved[i];
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    private sealed class RuntimeInfoCiScope : IDisposable {
        public RuntimeInfoCiScope(bool isCi) {
            RuntimeInfo.SetCiOverrideForTests(isCi);
        }

        public void Dispose() {
            RuntimeInfo.SetCiOverrideForTests(null);
        }
    }
}

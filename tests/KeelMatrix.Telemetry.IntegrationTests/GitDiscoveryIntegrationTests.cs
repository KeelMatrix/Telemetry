// Copyright (c) KeelMatrix

using System.Text;
using FluentAssertions;
using KeelMatrix.Telemetry.ProjectIdentity;

namespace KeelMatrix.Telemetry.IntegrationTests;

// GitDiscovery relies on process-wide starting points (CurrentDirectory/BaseDirectory/EntryAssembly)
// and performs filesystem I/O. Keep non-parallel to avoid cross-test interference.
[CollectionDefinition(Name, DisableParallelization = true)]
public static class GitDiscoveryIntegrationTestsCollectionDefinition {
    public const string Name = $"{nameof(GitDiscoveryIntegrationTests)}.NonParallel";
}

[Collection(GitDiscoveryIntegrationTestsCollectionDefinition.Name)]
public sealed class GitDiscoveryIntegrationTests : IDisposable {
    private readonly string originalCurrentDirectory;
    private readonly string root;

    public GitDiscoveryIntegrationTests() {
        originalCurrentDirectory = SafeGetCurrentDirectory();

        root = Path.Combine(Path.GetTempPath(), "KeelMatrix.Telemetry.IntegrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    public void Dispose() {
        SafeSetCurrentDirectory(originalCurrentDirectory);
        TryDeleteDirectory(root);
    }

    [Fact]
    public void TryFindGitDirectory_WalksUpAndFindsDotGitDirectory() {
        var repoRoot = Path.Combine(root, "repo");
        var workDir = Path.Combine(repoRoot, "src", "deep");
        Directory.CreateDirectory(workDir);

        var dotGitDir = Path.Combine(repoRoot, ".git");
        Directory.CreateDirectory(dotGitDir);

        using var cd = new CurrentDirectoryScope(workDir);

        // Must work when starting from any applicable starting point.
        // At minimum, CurrentDirectory should be among starting points.
        var starts = GitDiscovery.GetStartingPoints().Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        starts.Should().NotBeEmpty();
        starts.Should().Contain(s => Path.GetFullPath(s) == Path.GetFullPath(workDir));

        var foundAny = false;
        foreach (var start in starts) {
            if (!IsSameOrUnder(Path.GetFullPath(start), Path.GetFullPath(repoRoot)))
                continue;

            GitDiscovery.TryFindGitDirectory(start, out var gitDir).Should().BeTrue();
            gitDir.Should().Be(Path.GetFullPath(dotGitDir));
            foundAny = true;
        }

        foundAny.Should().BeTrue("at least one of the starting points should be within the test repo root");
    }

    [Fact]
    public void TryFindGitDirectory_ResolvesGitDirFromGitFilePointer() {
        var workTree = Path.Combine(root, "worktree");
        var gitdir = Path.Combine(root, "actual_git_dir", ".git");

        Directory.CreateDirectory(workTree);
        Directory.CreateDirectory(gitdir);

        // Simulate a worktree where .git is a file: "gitdir: <path>"
        // Use relative path to ensure resolution is based on the .git file directory.
        var dotGitPointer = Path.Combine(workTree, ".git");
        var rel = Path.GetRelativePath(workTree, gitdir).Replace('\\', '/');
        File.WriteAllText(dotGitPointer, $"gitdir: {rel}\n", Encoding.UTF8);

        GitDiscovery.TryFindGitDirectory(workTree, out var resolved).Should().BeTrue();
        resolved.Should().Be(Path.GetFullPath(gitdir));
    }

    [Fact]
    public void TryReadOriginRemoteUrl_ParsesRemoteOriginFromConfigWithinCaps() {
        var gitDir = Path.Combine(root, "repo", ".git");
        Directory.CreateDirectory(gitDir);

        var configPath = Path.Combine(gitDir, "config");
        File.WriteAllText(configPath,
            "[core]\n\trepositoryformatversion = 0\n\tfilemode = true\n" +
            "[remote \"origin\"]\n\turl = https://example.com/owner/repo.git\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n",
            Encoding.UTF8);

        GitDiscovery.TryReadOriginRemoteUrl(gitDir, out var url).Should().BeTrue();
        url.Should().Be("https://example.com/owner/repo.git");

        // Too-large config must fail fast without reading.
        var tooLargeDir = Path.Combine(root, "repo2", ".git");
        Directory.CreateDirectory(tooLargeDir);

        var tooLargeConfig = Path.Combine(tooLargeDir, "config");
        WriteBytes(tooLargeConfig, TelemetryConfig.ProjectIdentity.MaxConfigBytes + 1);

        GitDiscovery.TryReadOriginRemoteUrl(tooLargeDir, out var url2).Should().BeFalse();
        url2.Should().BeEmpty();
    }

    [Fact]
    public void TryComputeRootCommitHashBestEffort_HandlesMissingOrCorruptGracefully() {
        var gitDir = Path.Combine(root, "repo", ".git");
        Directory.CreateDirectory(gitDir);

        // Missing HEAD => false, no throw.
        var actMissing = () => GitDiscovery.TryComputeRootCommitHashBestEffort(gitDir, out _);
        actMissing.Should().NotThrow();
        actMissing().Should().BeFalse();

        // Corrupt HEAD => false, no throw.
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main\n", Encoding.UTF8);
        Directory.CreateDirectory(Path.Combine(gitDir, "refs", "heads"));
        File.WriteAllText(Path.Combine(gitDir, "refs", "heads", "main"), "NOT_A_HASH\n", Encoding.UTF8);

        var actCorrupt = () => GitDiscovery.TryComputeRootCommitHashBestEffort(gitDir, out _);
        actCorrupt.Should().NotThrow();
        actCorrupt().Should().BeFalse("malformed refs should be treated as best-effort failure");

        // Corrupt loose object => false, no throw.
        // Create a valid-looking hash, then a corrupt object file (not zlib / not commit).
        var goodHash = new string('a', 40);
        File.WriteAllText(Path.Combine(gitDir, "refs", "heads", "main"), goodHash + "\n", Encoding.UTF8);

        var objDir = Path.Combine(gitDir, "objects", goodHash[..2]);
        Directory.CreateDirectory(objDir);
        var objPath = Path.Combine(objDir, goodHash[2..]);
        File.WriteAllBytes(objPath, Encoding.ASCII.GetBytes("not a zlib stream"));

        var actObj = () => GitDiscovery.TryComputeRootCommitHashBestEffort(gitDir, out _);
        actObj.Should().NotThrow();
        actObj().Should().BeFalse();
    }

    private static bool IsSameOrUnder(string candidate, string rootDir) {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(rootDir))
            return false;

        candidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        rootDir = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (OperatingSystem.IsWindows()) {
            if (candidate.Equals(rootDir, StringComparison.OrdinalIgnoreCase))
                return true;
            return candidate.StartsWith(rootDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        if (candidate.Equals(rootDir, StringComparison.Ordinal))
            return true;
        return candidate.StartsWith(rootDir + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void WriteBytes(string path, int count) {
        // Deterministic content, avoids huge string allocations.
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var buffer = new byte[16 * 1024];
        for (int i = 0; i < buffer.Length; i++) buffer[i] = (byte)'x';

        int remaining = count;
        while (remaining > 0) {
            int chunk = Math.Min(remaining, buffer.Length);
            fs.Write(buffer, 0, chunk);
            remaining -= chunk;
        }
    }

    private static string SafeGetCurrentDirectory() {
        try { return Environment.CurrentDirectory; }
        catch { return string.Empty; }
    }

    private static void SafeSetCurrentDirectory(string dir) {
        try {
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                Environment.CurrentDirectory = dir;
        }
        catch {
            // swallow
        }
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

    private sealed class CurrentDirectoryScope : IDisposable {
        private readonly string prior;

        public CurrentDirectoryScope(string dir) {
            prior = SafeGetCurrentDirectory();
            try { Environment.CurrentDirectory = dir; }
            catch { /* swallow */ }
        }

        public void Dispose() {
            SafeSetCurrentDirectory(prior);
        }
    }
}

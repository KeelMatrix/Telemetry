// Copyright (c) KeelMatrix

using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using FluentAssertions;

namespace KeelMatrix.Telemetry.IntegrationTests;

[Collection(TelemetryDeliveryWorkerIntegrationTestsCollectionDefinition.Name)]
public sealed class NetstandardGitDiscoveryIntegrationTests : IDisposable {
    private readonly string root;

    public NetstandardGitDiscoveryIntegrationTests() {
        root = Path.Combine(Path.GetTempPath(), "KeelMatrix.Telemetry.NetstandardIntegrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    public void Dispose() {
        TryDeleteDirectory(root);
    }

    [Fact]
    public void NetstandardGitPath_RejectsCorruptedZlibTrailer() {
        const string commitHash = "1111111111111111111111111111111111111111";
        var gitDir = CreateRepository(commitHash);
        var objectBytes = CreateLooseCommitObject($"tree {new string('2', 40)}\n\nroot commit\n");
        objectBytes[^1] ^= 0xff;
        File.WriteAllBytes(GetObjectPath(gitDir, commitHash), objectBytes);

        InvokeTryComputeRootCommitHash(gitDir).Should().BeFalse();
    }

    [Theory]
    [InlineData("123")]
    [InlineData("NOT_A_SHA")]
    [InlineData("000000000000000000000000000000000000000g")]
    public void NetstandardGitPath_RejectsMalformedParentHeader(string parentHash) {
        const string childCommitHash = "3333333333333333333333333333333333333333";
        var gitDir = CreateRepository(childCommitHash);
        var commitText = $"tree {new string('2', 40)}\nparent {parentHash}\n\nmalformed parent\n";
        File.WriteAllBytes(GetObjectPath(gitDir, childCommitHash), CreateLooseCommitObject(commitText));

        InvokeTryComputeRootCommitHash(gitDir).Should().BeFalse();
    }

    private bool InvokeTryComputeRootCommitHash(string gitDir) {
        var assemblyPath = FindNetstandardAssembly();
        var loadContext = new AssemblyLoadContext("KeelMatrix.Telemetry.netstandard-test", isCollectible: true);
        try {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var gitDiscovery = assembly.GetType("KeelMatrix.Telemetry.ProjectIdentity.GitDiscovery", throwOnError: true)!;
            var method = gitDiscovery.GetMethod(
                "TryComputeRootCommitHashBestEffort",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            var arguments = new object?[] { gitDir, null };
            return (bool)method.Invoke(null, arguments)!;
        }
        finally {
            loadContext.Unload();
        }
    }

    private string CreateRepository(string commitHash) {
        var gitDir = Path.Combine(root, "repo", ".git");
        Directory.CreateDirectory(Path.Combine(gitDir, "refs", "heads"));
        Directory.CreateDirectory(Path.Combine(gitDir, "objects", commitHash[..2]));
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(gitDir, "refs", "heads", "main"), commitHash + "\n", Encoding.UTF8);
        return gitDir;
    }

    private static string GetObjectPath(string gitDir, string commitHash) {
        return Path.Combine(gitDir, "objects", commitHash[..2], commitHash[2..]);
    }

    private static byte[] CreateLooseCommitObject(string commitText) {
        var payload = Encoding.UTF8.GetBytes($"commit {commitText.Length}\0{commitText}");
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionMode.Compress, leaveOpen: true))
            zlib.Write(payload, 0, payload.Length);

        return compressed.ToArray();
    }

    private static string FindNetstandardAssembly() {
        var repositoryRoot = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var assemblyPath = Path.Combine(
            repositoryRoot,
            "src",
            "KeelMatrix.Telemetry",
            "bin",
            configuration,
            "netstandard2.0",
            "KeelMatrix.Telemetry.dll");

        File.Exists(assemblyPath).Should().BeTrue("the integration test build must produce the netstandard2.0 assembly");
        return assemblyPath;
    }

    private static string FindRepositoryRoot() {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null) {
            if (File.Exists(Path.Combine(current.FullName, "KeelMatrix.Telemetry.slnx")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static void TryDeleteDirectory(string path) {
        try {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch {
            // Best effort cleanup for temporary test state.
        }
    }
}

// Copyright (c) KeelMatrix

using System.Reflection;
using System.Text;
using FluentAssertions;
using KeelMatrix.Telemetry.ProjectIdentity;

namespace KeelMatrix.Telemetry.IntegrationTests;

// ProjectFileIdentityFingerprint relies on process-wide starting points (CurrentDirectory/BaseDirectory)
// and does filesystem I/O. Keep non-parallel to avoid cross-test interference.
[CollectionDefinition(Name, DisableParallelization = true)]
public static class ProjectFileIdentityFingerprintIntegrationTestsCollectionDefinition {
    public const string Name = $"{nameof(ProjectFileIdentityFingerprintIntegrationTests)}.NonParallel";
}

[Collection(ProjectFileIdentityFingerprintIntegrationTestsCollectionDefinition.Name)]
public sealed class ProjectFileIdentityFingerprintIntegrationTests : IDisposable {
    private readonly string originalCurrentDirectory;
    private readonly string root;

    public ProjectFileIdentityFingerprintIntegrationTests() {
        originalCurrentDirectory = SafeGetCurrentDirectory();

        root = Path.Combine(Path.GetTempPath(), "KeelMatrix.Telemetry.IntegrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [Fact]
    public void PrimarySelection_PrefersNearestSln_ThenProj() {
        var level1 = Path.Combine(root, "repo");
        var level2 = Path.Combine(level1, "src");
        Directory.CreateDirectory(level2);

        var slnPath = Path.Combine(level1, "MySolution.sln");
        File.WriteAllText(slnPath, "Microsoft Visual Studio Solution File, Format Version 12.00\n", Encoding.UTF8);

        var projPath = Path.Combine(level2, "MyProject.csproj");
        File.WriteAllText(projPath, MinimalCsproj("net8.0"), Encoding.UTF8);

        InvokeTrySelectPrimaryIdentityFile(level2, out var identityRoot, out var primaryPath, out var primaryRole);

        primaryRole.Should().Be("sln");
        primaryPath.Should().Be(Path.GetFullPath(slnPath));
        identityRoot.Should().Be(Path.GetFullPath(level1));

        // If no .sln exists, it should fall back to the nearest project file.
        File.Delete(slnPath);

        InvokeTrySelectPrimaryIdentityFile(level2, out identityRoot, out primaryPath, out primaryRole);

        primaryRole.Should().Be("proj");
        primaryPath.Should().Be(Path.GetFullPath(projPath));
        identityRoot.Should().Be(Path.GetFullPath(level1));
    }

    [Fact]
    public void IdentityRoot_UsesRepoMarkersWhenPresent() {
        var repoRoot = Path.Combine(root, "monorepo");
        var sub = Path.Combine(repoRoot, "a", "b", "c");
        Directory.CreateDirectory(sub);

        // Marker: .git directory at repo root.
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));

        var slnPath = Path.Combine(repoRoot, "Root.sln");
        File.WriteAllText(slnPath, "Microsoft Visual Studio Solution File, Format Version 12.00\n", Encoding.UTF8);

        InvokeTrySelectPrimaryIdentityFile(sub, out var identityRoot, out var primaryPath, out var primaryRole);

        primaryRole.Should().Be("sln");
        primaryPath.Should().Be(Path.GetFullPath(slnPath));
        identityRoot.Should().Be(Path.GetFullPath(repoRoot));
    }

    [Fact]
    public void Fingerprint_StableUnderWhitespaceAndLineEndingChanges() {
        var repoRoot = Path.Combine(root, "repo");
        Directory.CreateDirectory(repoRoot);

        // Primary
        var slnPath = Path.Combine(repoRoot, "Repo.sln");
        File.WriteAllText(slnPath, "Microsoft Visual Studio Solution File, Format Version 12.00\r\n", Encoding.UTF8);

        // Project
        var projPath = Path.Combine(repoRoot, "App.csproj");
        const string xml1 = "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n" +
                   "  <PropertyGroup>\r\n" +
                   "    <TargetFramework> net8.0 </TargetFramework>\r\n" +
                   "  </PropertyGroup>\r\n" +
                   "</Project>\r\n";
        File.WriteAllText(projPath, xml1, Encoding.UTF8);

        // Compute
        var (ok1, fp1) = ComputeFingerprintBytesFrom(repoRoot);
        ok1.Should().BeTrue();
        fp1.Should().NotBeEmpty();

        // Rewrite with different whitespace/blank lines/line endings, but equivalent XML content.
        const string xml2 = "\n\n  <Project  Sdk=\"Microsoft.NET.Sdk\">\n" +
                   "\t<PropertyGroup>\n" +
                   "\t\t<TargetFramework>net8.0</TargetFramework>\n" +
                   "\t</PropertyGroup>\n" +
                   "</Project>\n\n";
        File.WriteAllText(projPath, xml2, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var (ok2, fp2) = ComputeFingerprintBytesFrom(repoRoot);
        ok2.Should().BeTrue();

        fp2.Should().Equal(fp1);
    }

    [Fact]
    public void Fingerprint_SameWhenPackagesChange() {
        var repoRoot = Path.Combine(root, "repo");
        Directory.CreateDirectory(repoRoot);

        var slnPath = Path.Combine(repoRoot, "Repo.sln");
        File.WriteAllText(slnPath, "Microsoft Visual Studio Solution File, Format Version 12.00\n", Encoding.UTF8);

        var projPath = Path.Combine(repoRoot, "App.csproj");
        File.WriteAllText(projPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            "  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>\n" +
            "  <ItemGroup>\n" +
            "    <PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />\n" +
            "  </ItemGroup>\n" +
            "</Project>\n",
            Encoding.UTF8);

        var (ok1, fp1) = ComputeFingerprintBytesFrom(repoRoot);
        ok1.Should().BeTrue();

        // Change package version => must affect fingerprint.
        File.WriteAllText(projPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            "  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>\n" +
            "  <ItemGroup>\n" +
            "    <PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.2\" />\n" +
            "    <PackageReference Include=\"Some.Package\" Version=\"1.0.2\" />\n" +
            "  </ItemGroup>\n" +
            "</Project>\n",
            Encoding.UTF8);

        var (ok2, fp2) = ComputeFingerprintBytesFrom(repoRoot);
        ok2.Should().BeTrue();

        fp2.Should().Equal(fp1);
    }

    [Fact]
    public void Fingerprint_RespectsFileCapsAndSkipsTooLargeInputs() {
        var repoRoot = Path.Combine(root, "repo");
        Directory.CreateDirectory(repoRoot);

        // Primary sln under cap.
        var slnPath = Path.Combine(repoRoot, "Repo.sln");
        File.WriteAllText(slnPath, "Microsoft Visual Studio Solution File, Format Version 12.00\n", Encoding.UTF8);

        var projPath = Path.Combine(repoRoot, "App.csproj");
        File.WriteAllText(projPath, MinimalCsproj("net8.0"), Encoding.UTF8);

        // Optional config file over the per-file cap should be skipped (not fatal).
        var bigProps = Path.Combine(repoRoot, "Directory.Build.props");
        var big = new string('a', TelemetryConfig.ProjectIdentity.MaxFileBytes + 1024);
        File.WriteAllText(bigProps, big, Encoding.UTF8);

        var (ok1, fp1) = ComputeFingerprintBytesFrom(repoRoot);
        ok1.Should().BeTrue();
        fp1.Should().NotBeEmpty();

        // Removing the oversized optional file should not change the fingerprint if it was skipped.
        File.Delete(bigProps);

        var (ok2, fp2) = ComputeFingerprintBytesFrom(repoRoot);
        ok2.Should().BeTrue();
        fp2.Should().Equal(fp1);

        // Now make the PRIMARY file exceed the cap: should fail to compute identity.
        var hugeSln = new string('b', TelemetryConfig.ProjectIdentity.MaxFileBytes + 10);
        File.WriteAllText(slnPath, hugeSln, Encoding.UTF8);

        using var cd = new CurrentDirectoryScope(repoRoot);
        ProjectFileIdentityFingerprint.TryComputeIdentityFingerprintFromProjectFiles(out var fp3)
            .Should().BeFalse();
        fp3.Should().BeEmpty();
    }

    public void Dispose() {
        SafeSetCurrentDirectory(originalCurrentDirectory);
        TryDeleteDirectory(root);
    }

    private static void InvokeTrySelectPrimaryIdentityFile(
        string startingPoint,
        out string identityRoot,
        out string primaryPath,
        out string primaryRole) {

        var method = typeof(ProjectFileIdentityFingerprint)
            .GetMethod("TrySelectPrimaryIdentityFile", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull("internal implementation changed; tests should be updated accordingly");

        object?[] args = [startingPoint, string.Empty, string.Empty, string.Empty];
        var ok = (bool)(method!.Invoke(null, args) ?? false);

        ok.Should().BeTrue();

        identityRoot = (string)args[1]!;
        primaryPath = (string)args[2]!;
        primaryRole = (string)args[3]!;
    }

    private static (bool ok, byte[] fingerprint) ComputeFingerprintBytesFrom(string startingPoint) {
        using var cd = new CurrentDirectoryScope(startingPoint);
        var ok = ProjectFileIdentityFingerprint.TryComputeIdentityFingerprintFromProjectFiles(out var bytes);
        return (ok, bytes);
    }

    private static string MinimalCsproj(string tfm) {
        return "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
               $"  <PropertyGroup><TargetFramework>{tfm}</TargetFramework></PropertyGroup>\n" +
               "</Project>\n";
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

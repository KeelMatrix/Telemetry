// Copyright (c) KeelMatrix

using System.Reflection;
using System.Text;
using FluentAssertions;

namespace KeelMatrix.Telemetry.UnitTests;

public sealed class ProjectFileIdentityFingerprintTests {
    private static readonly Type FingerprintType =
        typeof(TelemetryClient).Assembly.GetType("KeelMatrix.Telemetry.ProjectIdentity.ProjectFileIdentityFingerprint", throwOnError: true)!;
    private static readonly object CurrentDirectoryLock = new();

    [Fact]
    public void TrySelectPrimaryIdentityFile_PrefersFullSolutions_OverSolutionFilters_AndProjects() {
        var root = CreateTempDirectory();

        try {
            File.WriteAllText(Path.Combine(root, "repo.slnf"), "{ \"solution\": { \"path\": \"repo.sln\" } }");
            File.WriteAllText(Path.Combine(root, "repo.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(root, "repo.slnx"), "<Solution><Project Path=\"src/repo.csproj\" /></Solution>");

            var success = InvokeTrySelectPrimaryIdentityFile(
                root,
                out var identityRoot,
                out var primaryPath,
                out var primaryRole);

            success.Should().BeTrue();
            identityRoot.Should().Be(Path.GetFullPath(root));
            primaryPath.Should().Be(Path.GetFullPath(Path.Combine(root, "repo.slnx")));
            primaryRole.Should().Be("sln");
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TrySelectPrimaryIdentityFile_PrefersSolutionFilters_OverProjects_WhenNoFullSolutionExists() {
        var root = CreateTempDirectory();

        try {
            File.WriteAllText(Path.Combine(root, "repo.slnf"), "{ \"solution\": { \"path\": \"repo.sln\" } }");
            File.WriteAllText(Path.Combine(root, "repo.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            var success = InvokeTrySelectPrimaryIdentityFile(
                root,
                out _,
                out var primaryPath,
                out var primaryRole);

            success.Should().BeTrue();
            primaryPath.Should().Be(Path.GetFullPath(Path.Combine(root, "repo.slnf")));
            primaryRole.Should().Be("sln");
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CanonicalizeStructuredXml_ProducesSameBytes_ForEquivalentSlnxContent() {
        var left = Encoding.UTF8.GetBytes("<Solution Format=\"1\" Name=\"App\"><Project Path=\"src/app.csproj\" Id=\"1\" /></Solution>");
        var right = Encoding.UTF8.GetBytes(
            """
            <Solution Name="App" Format="1">
              <Project Id="1" Path="src/app.csproj"></Project>
            </Solution>
            """);

        var leftCanonical = InvokeCanonicalizeStructuredXml(left, "slnx.v1");
        var rightCanonical = InvokeCanonicalizeStructuredXml(right, "slnx.v1");

        leftCanonical.Should().Equal(rightCanonical);
    }

    [Fact]
    public void CanonicalizeStructuredJson_ProducesSameBytes_ForEquivalentSlnfContent() {
        var left = Encoding.UTF8.GetBytes("""{ "solution": { "path": "repo.sln", "projects": [ "a.csproj", "b.csproj" ] } }""");
        var right = Encoding.UTF8.GetBytes(
            """
            {
              "solution": {
                "projects": [ "a.csproj", "b.csproj" ],
                "path": "repo.sln"
              }
            }
            """);

        var leftCanonical = InvokeCanonicalizeStructuredJson(left, "slnf.v1");
        var rightCanonical = InvokeCanonicalizeStructuredJson(right, "slnf.v1");

        leftCanonical.Should().Equal(rightCanonical);
    }

    [Fact]
    public void TryComputeIdentityFingerprintFromProjectFiles_UsesSolutionFilter_WhenNoFullSolutionExists() {
        lock (CurrentDirectoryLock) {
            var root = CreateTempDirectory();
            var originalCurrentDirectory = Environment.CurrentDirectory;

            try {
                File.WriteAllText(Path.Combine(root, "Directory.Build.props"), "<Project />");
                File.WriteAllText(Path.Combine(root, "repo.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
                File.WriteAllText(Path.Combine(root, "repo.slnf"), """{ "solution": { "path": "repo.sln", "projects": [ "repo.csproj" ] } }""");

                Environment.CurrentDirectory = root;

                var withSolutionFilter = InvokeTryComputeIdentityFingerprintFromProjectFiles(out var filterFingerprint);
                File.Delete(Path.Combine(root, "repo.slnf"));
                var projectOnly = InvokeTryComputeIdentityFingerprintFromProjectFiles(out var projectFingerprint);

                withSolutionFilter.Should().BeTrue();
                projectOnly.Should().BeTrue();
                filterFingerprint.Should().NotBeEmpty();
                projectFingerprint.Should().NotBeEmpty();
                filterFingerprint.Should().NotEqual(projectFingerprint);
            }
            finally {
                Environment.CurrentDirectory = originalCurrentDirectory;
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static bool InvokeTrySelectPrimaryIdentityFile(
        string startingPoint,
        out string identityRoot,
        out string primaryPath,
        out string primaryRole) {
        var parameters = new object?[] { startingPoint, null, null, null };

        var result = FingerprintType.InvokeMember(
            "TrySelectPrimaryIdentityFile",
            BindingFlags.InvokeMethod | BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            target: null,
            args: parameters);

        identityRoot = (string)parameters[1]!;
        primaryPath = (string)parameters[2]!;
        primaryRole = (string)parameters[3]!;
        return result is bool success && success;
    }

    private static byte[] InvokeCanonicalizeStructuredXml(byte[] rawBytes, string header) =>
        (byte[])FingerprintType.InvokeMember(
            "CanonicalizeStructuredXml",
            BindingFlags.InvokeMethod | BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            target: null,
            args: [rawBytes, header])!;

    private static byte[] InvokeCanonicalizeStructuredJson(byte[] rawBytes, string header) =>
        (byte[])FingerprintType.InvokeMember(
            "CanonicalizeStructuredJson",
            BindingFlags.InvokeMethod | BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            target: null,
            args: [rawBytes, header])!;

    private static bool InvokeTryComputeIdentityFingerprintFromProjectFiles(out byte[] fingerprintBytes) {
        var parameters = new object?[] { null };

        var result = FingerprintType.InvokeMember(
            "TryComputeIdentityFingerprintFromProjectFiles",
            BindingFlags.InvokeMethod | BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            target: null,
            args: parameters);

        fingerprintBytes = (byte[])parameters[0]!;
        return result is bool success && success;
    }

    private static string CreateTempDirectory() {
        var path = Path.Combine(Path.GetTempPath(), "KeelMatrix.Telemetry.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

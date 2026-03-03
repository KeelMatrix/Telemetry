// Copyright (c) KeelMatrix

using System.Text;
using FluentAssertions;
using KeelMatrix.Telemetry.ProjectIdentity;

namespace KeelMatrix.Telemetry.IntegrationTests;

// MachineSaltProvider and TelemetryConfig.Runtime are global/static in behavior.
// Also, these tests intentionally touch environment variables.
// Keep these tests non-parallel.
[CollectionDefinition(Name, DisableParallelization = true)]
public static class MachineSaltProviderIntegrationTestsCollectionDefinition {
    public const string Name = $"{nameof(MachineSaltProviderIntegrationTests)}.NonParallel";
}

[Collection(MachineSaltProviderIntegrationTestsCollectionDefinition.Name)]
public sealed class MachineSaltProviderIntegrationTests {
    private const string SaltFileName = "telemetry.salt";

    private const string EnvKeelMatrixNoTelemetry = "KEELMATRIX_NO_TELEMETRY";
    private const string EnvDotNetCliTelemetryOptOut = "DOTNET_CLI_TELEMETRY_OPTOUT";
    private const string EnvDoNotTrack = "DO_NOT_TRACK";

    [Fact]
    public void GetOrCreateMachineSaltBytes_CreatesFileAndReturns32Bytes() {
        using var _ = new EnvironmentVariableSnapshot(EnvKeelMatrixNoTelemetry, EnvDotNetCliTelemetryOptOut, EnvDoNotTrack);
        ClearOptOutVars();

        using var runtime = IsolatedRuntime.Create();

        var bytes = MachineSaltProvider.GetOrCreateMachineSaltBytes();

        bytes.Should().NotBeNull();
        bytes.Length.Should().Be(TelemetryConfig.ExpectedSaltBytes);

        File.Exists(runtime.SaltPath).Should().BeTrue();

        var text = File.ReadAllText(runtime.SaltPath, Encoding.UTF8).Trim();
        text.Length.Should().Be(TelemetryConfig.ExpectedSaltBytes * 2, "persisted salt must be hex of 32 bytes");

        var decoded = Convert.FromHexString(text);
        decoded.Length.Should().Be(TelemetryConfig.ExpectedSaltBytes);
        decoded.Should().Equal(bytes);
    }

    [Fact]
    public void GetOrCreateMachineSaltBytes_IsStableAcrossCalls() {
        using var _ = new EnvironmentVariableSnapshot(EnvKeelMatrixNoTelemetry, EnvDotNetCliTelemetryOptOut, EnvDoNotTrack);
        ClearOptOutVars();

        using var runtime = IsolatedRuntime.Create();

        var first = MachineSaltProvider.GetOrCreateMachineSaltBytes();
        var second = MachineSaltProvider.GetOrCreateMachineSaltBytes();

        first.Should().Equal(second);

        // Also ensure the persisted value remains consistent.
        var text = File.ReadAllText(runtime.SaltPath, Encoding.UTF8).Trim();
        Convert.FromHexString(text).Should().Equal(first);
    }

    [Fact]
    public void CorruptSaltFile_IsRegeneratedAndRewritten() {
        using var _ = new EnvironmentVariableSnapshot(EnvKeelMatrixNoTelemetry, EnvDotNetCliTelemetryOptOut, EnvDoNotTrack);
        ClearOptOutVars();

        using var runtime = IsolatedRuntime.Create();

        Directory.CreateDirectory(runtime.RootDir);
        File.WriteAllText(runtime.SaltPath, "not-hex", Encoding.UTF8);

        var bytes = MachineSaltProvider.GetOrCreateMachineSaltBytes();

        bytes.Length.Should().Be(TelemetryConfig.ExpectedSaltBytes);

        var rewritten = File.ReadAllText(runtime.SaltPath, Encoding.UTF8).Trim();
        rewritten.Should().NotBe("not-hex");
        rewritten.Length.Should().Be(TelemetryConfig.ExpectedSaltBytes * 2);

        Convert.FromHexString(rewritten).Should().Equal(bytes);
    }

    [Fact]
    public void WhenTelemetryDisabled_DoesNotDoIO_ReturnsRandomSalt() {
        using var _ = new EnvironmentVariableSnapshot(EnvKeelMatrixNoTelemetry, EnvDotNetCliTelemetryOptOut, EnvDoNotTrack);
        ClearOptOutVars();
        Environment.SetEnvironmentVariable(EnvKeelMatrixNoTelemetry, "1");

        using var runtime = IsolatedRuntime.Create();

        // Ensure the filesystem is clean before the call.
        Directory.Exists(runtime.RootDir).Should().BeFalse();
        File.Exists(runtime.SaltPath).Should().BeFalse();

        var bytes = MachineSaltProvider.GetOrCreateMachineSaltBytes();

        bytes.Length.Should().Be(TelemetryConfig.ExpectedSaltBytes);

        // Disabled policy: must not create directories or touch the salt file.
        Directory.Exists(runtime.RootDir).Should().BeFalse();
        File.Exists(runtime.SaltPath).Should().BeFalse();
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

    private sealed class IsolatedRuntime : IDisposable {
        public required string RootDir { get; init; }
        public required string SaltPath { get; init; }

        public static IsolatedRuntime Create() {
            // ToolNameUpper is part of the per-user telemetry root: "KeelMatrix/{ToolNameUpper}".
            var toolNameUpper = "INTEGRATIONTEST_" + Guid.NewGuid().ToString("N");
            TelemetryConfig.Runtime.Set(toolNameUpper, typeof(MachineSaltProviderIntegrationTests));

            // MachineSaltProvider currently expects the root directory to already be resolved.
            // Resolution is compute-only and does not create directories.
            TelemetryConfig.Runtime.EnsureRootDirectoryResolvedOnWorkerThread();

            var root = TelemetryConfig.Runtime.GetRootDirectory();
            var saltPath = Path.Combine(root, SaltFileName);

            // Fresh per-test root.
            TryDeleteDirectory(root);

            return new IsolatedRuntime {
                RootDir = root,
                SaltPath = saltPath
            };
        }

        public void Dispose() {
            TryDeleteDirectory(RootDir);
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
    }
}

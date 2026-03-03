// Copyright (c) KeelMatrix

using System.Reflection;
using FluentAssertions;

namespace KeelMatrix.Telemetry.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public static class ClientEndToEndIntegrationTestsCollectionDefinition {
    public const string Name = $"{nameof(ClientEndToEndIntegrationTests)}.NonParallel";
}

[Collection(ClientEndToEndIntegrationTestsCollectionDefinition.Name)]
public sealed class ClientEndToEndIntegrationTests {
    private const string EnvKeelMatrixNoTelemetry = "KEELMATRIX_NO_TELEMETRY";
    private const string EnvDotNetCliTelemetryOptOut = "DOTNET_CLI_TELEMETRY_OPTOUT";
    private const string EnvDoNotTrack = "DO_NOT_TRACK";

    [Fact]
    public void Client_UsesNullTelemetryClient_WhenOptOutEnabled() {
        using var _ = new EnvironmentVariableSnapshot(EnvKeelMatrixNoTelemetry, EnvDotNetCliTelemetryOptOut, EnvDoNotTrack);

        Environment.SetEnvironmentVariable(EnvKeelMatrixNoTelemetry, "1");
        Environment.SetEnvironmentVariable(EnvDotNetCliTelemetryOptOut, null);
        Environment.SetEnvironmentVariable(EnvDoNotTrack, null);

        var client = new Client("INTEGRATIONTEST_OPT_OUT", typeof(ClientEndToEndIntegrationTests));

        var inner = GetInnerTelemetryClient(client);
        inner.Should().NotBeNull();
        inner!.GetType().Name.Should().Be("NullTelemetryClient");
    }

    [Fact]
    public void Client_TrackActivation_NoThrow() {
        using var _ = new EnvironmentVariableSnapshot(EnvKeelMatrixNoTelemetry, EnvDotNetCliTelemetryOptOut, EnvDoNotTrack);
        ClearOptOutVars();

        using var runtime = IsolatedRuntime.Create();

        var client = new Client(runtime.ToolNameUpper, typeof(ClientEndToEndIntegrationTests));

        Action act = () => {
            client.TrackActivation();
            client.TrackActivation();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void Client_TrackHeartbeat_NoThrow() {
        using var _ = new EnvironmentVariableSnapshot(EnvKeelMatrixNoTelemetry, EnvDotNetCliTelemetryOptOut, EnvDoNotTrack);
        ClearOptOutVars();

        using var runtime = IsolatedRuntime.Create();

        var client = new Client(runtime.ToolNameUpper, typeof(ClientEndToEndIntegrationTests));

        Action act = () => {
            client.TrackHeartbeat();
            client.TrackHeartbeat();
        };

        act.Should().NotThrow();
    }

    private static object? GetInnerTelemetryClient(Client client) {
        var field = typeof(Client).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull("Client stores the implementation in a private field");
        return field!.GetValue(client);
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
        public required string ToolNameUpper { get; init; }
        public string? RootDir { get; private set; }

        public static IsolatedRuntime Create() {
            // ToolNameUpper becomes part of the per-user telemetry root: "KeelMatrix/{ToolNameUpper}".
            var toolNameUpper = "INTEGRATIONTEST_" + Guid.NewGuid().ToString("N");
            return new IsolatedRuntime { ToolNameUpper = toolNameUpper };
        }

        public void Dispose() {
            // Best-effort cleanup of the per-test telemetry root.
            try {
                TelemetryConfig.Runtime.Set(ToolNameUpper, typeof(ClientEndToEndIntegrationTests));
                TelemetryConfig.Runtime.EnsureRootDirectoryResolvedOnWorkerThread();
                RootDir = TelemetryConfig.Runtime.GetRootDirectory();
            }
            catch {
                RootDir = null;
            }

            if (!string.IsNullOrWhiteSpace(RootDir)) {
                try {
                    if (Directory.Exists(RootDir!))
                        Directory.Delete(RootDir!, recursive: true);
                }
                catch {
                    // swallow
                }
            }
        }
    }
}

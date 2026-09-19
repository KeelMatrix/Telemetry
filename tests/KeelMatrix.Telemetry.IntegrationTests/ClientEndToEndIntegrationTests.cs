// Copyright (c) KeelMatrix

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using FluentAssertions;

namespace KeelMatrix.Telemetry.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public static class ClientEndToEndIntegrationTestsCollectionDefinition {
    public const string Name = $"{nameof(ClientEndToEndIntegrationTests)}.NonParallel";
}

[Collection(TelemetryDeliveryWorkerIntegrationTestsCollectionDefinition.Name)]
public sealed class ClientEndToEndIntegrationTests {
    private const string EnvKeelMatrixNoTelemetry = "KEELMATRIX_NO_TELEMETRY";
    private const string EnvDotNetCliTelemetryOptOut = "DOTNET_CLI_TELEMETRY_OPTOUT";
    private const string EnvDoNotTrack = "DO_NOT_TRACK";

    [Fact]
    public void Client_UsesNullTelemetryClient_WhenOptOutEnabled() {
        using var _ = new EnvironmentVariableSnapshot(EnvKeelMatrixNoTelemetry, EnvDotNetCliTelemetryOptOut, EnvDoNotTrack);
        using var server = new LocalTelemetryServer();
        using var urlOverride = new TelemetryUrlOverrideScope(server.BaseUri);

        Environment.SetEnvironmentVariable(EnvKeelMatrixNoTelemetry, "1");
        Environment.SetEnvironmentVariable(EnvDotNetCliTelemetryOptOut, null);
        Environment.SetEnvironmentVariable(EnvDoNotTrack, null);

        var client = new Client("INTEGRATIONTEST_OPT_OUT", typeof(ClientEndToEndIntegrationTests));

        var inner = GetInnerTelemetryClient(client);
        inner.Should().NotBeNull();
        inner!.GetType().Name.Should().Be("NullTelemetryClient");
        server.Received.Should().BeEmpty();
    }

    [Fact]
    public void Client_TrackActivation_NoThrow() {
        using var _ = new EnvironmentVariableSnapshot(EnvKeelMatrixNoTelemetry, EnvDotNetCliTelemetryOptOut, EnvDoNotTrack);
        ClearOptOutVars();

        using var server = new LocalTelemetryServer();
        using var urlOverride = new TelemetryUrlOverrideScope(server.BaseUri);
        using var runtime = IsolatedRuntime.Create();
        using var clientScope = new ClientScope(runtime.ToolNameUpper);

        var client = clientScope.Client;

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

        using var server = new LocalTelemetryServer();
        using var urlOverride = new TelemetryUrlOverrideScope(server.BaseUri);
        using var runtime = IsolatedRuntime.Create();
        using var clientScope = new ClientScope(runtime.ToolNameUpper);

        var client = clientScope.Client;

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
            for (var i = snapshot.Length - 1; i >= 0; i--) {
                var (Name, Value) = snapshot[i];
                Environment.SetEnvironmentVariable(Name, Value);
            }
        }
    }

    private sealed class IsolatedRuntime : IDisposable {
        public required string ToolNameUpper { get; init; }
        public string? RootDir { get; private set; }

        public static IsolatedRuntime Create() {
            // ToolNameUpper becomes part of the per-user telemetry root: "KeelMatrix/{ToolNameUpper}".
            var toolNameUpper = "IT_" + Guid.NewGuid().ToString("N")[..12];
            return new IsolatedRuntime { ToolNameUpper = toolNameUpper };
        }

        public void Dispose() {
            // Best-effort cleanup of the per-test telemetry root.
            try {
                var runtimeContext = new TelemetryRuntimeContext(ToolNameUpper, typeof(ClientEndToEndIntegrationTests));
                runtimeContext.EnsureRootDirectoryResolvedOnWorkerThread();
                RootDir = runtimeContext.GetRootDirectory();
            }
            catch {
                RootDir = null;
            }

            TestCleanup.RegisterToolForFinalCleanup(ToolNameUpper, typeof(ClientEndToEndIntegrationTests), RootDir);
        }
    }

    private sealed class ClientScope : IDisposable {
        public ClientScope(string toolNameUpper) {
            ToolNameUpper = toolNameUpper;
            Client = new Client(toolNameUpper, typeof(ClientEndToEndIntegrationTests));
        }

        public string ToolNameUpper { get; }
        public Client Client { get; }

        public void Dispose() {
            TestCleanup.DisposeCachedWorkerForTool(ToolNameUpper, typeof(ClientEndToEndIntegrationTests));
        }
    }

    private sealed class TelemetryUrlOverrideScope : IDisposable {
        public TelemetryUrlOverrideScope(Uri uri) {
            TelemetryConfig.SetUrlOverrideForTests(uri);
        }

        public void Dispose() {
            TelemetryConfig.SetUrlOverrideForTests(null);
        }
    }

    private sealed class LocalTelemetryServer : IDisposable {
        private readonly HttpListener listener;
        private readonly CancellationTokenSource cts = new();
        private readonly Task loop;

        public LocalTelemetryServer() {
            var portListener = new TcpListener(IPAddress.Loopback, 0);
            portListener.Start();
            var port = ((IPEndPoint)portListener.LocalEndpoint).Port;
            portListener.Stop();

            var prefix = $"http://127.0.0.1:{port}/";
            BaseUri = new Uri(prefix, UriKind.Absolute);
            listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();
            loop = Task.Run(AcceptLoopAsync);
        }

        public Uri BaseUri { get; }
        public ConcurrentQueue<string> Received { get; } = new();

        private async Task AcceptLoopAsync() {
            while (!cts.IsCancellationRequested) {
                HttpListenerContext? context;
                try {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch {
                    if (cts.IsCancellationRequested)
                        return;

                    continue;
                }

                try {
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
                    Received.Enqueue(await reader.ReadToEndAsync().ConfigureAwait(false));
                    context.Response.StatusCode = (int)HttpStatusCode.OK;
                    var bytes = Encoding.UTF8.GetBytes("ok");
                    await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                }
                catch {
                    // Test transport is best effort during teardown.
                }
                finally {
                    try { context.Response.OutputStream.Close(); } catch { }
                    try { context.Response.Close(); } catch { }
                }
            }
        }

        public void Dispose() {
            try { cts.Cancel(); } catch { }
            try { listener.Stop(); } catch { }
            try { listener.Close(); } catch { }
            try { loop.GetAwaiter().GetResult(); } catch { }
            cts.Dispose();
        }
    }
}

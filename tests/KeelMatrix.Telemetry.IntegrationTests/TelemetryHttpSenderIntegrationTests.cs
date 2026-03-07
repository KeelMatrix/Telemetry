// Copyright (c) KeelMatrix

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using KeelMatrix.Telemetry.Infrastructure;

namespace KeelMatrix.Telemetry.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public static class TelemetryHttpSenderIntegrationTestsCollectionDefinition {
    public const string Name = $"{nameof(TelemetryHttpSenderIntegrationTests)}.NonParallel";
}

[Collection(TelemetryHttpSenderIntegrationTestsCollectionDefinition.Name)]
public sealed class TelemetryHttpSenderIntegrationTests : IDisposable {
    private readonly LocalHttpServer server;

    public TelemetryHttpSenderIntegrationTests() {
        server = new LocalHttpServer();
        TelemetryConfig.SetUrlOverrideForTests(server.Url);
    }

    public void Dispose() {
        TelemetryConfig.SetUrlOverrideForTests(null);
        server.Dispose();
    }

    [Fact]
    public async Task TrySendAsync_ReturnsTrue_On2xxFromLocalServer() {
        server.ResponseStatusCode = HttpStatusCode.OK;
        using var sender = new TelemetryHttpSender(server.Url);

        var ok = await sender.TrySendAsync("{}", CancellationToken.None);

        ok.Should().BeTrue();
        server.Received.Count.Should().Be(1);

        server.Received[0].Method.Should().Be("POST");
        server.Received[0].ContentType.Should().Be("application/json; charset=utf-8");
        server.Received[0].Body.Should().Be("{}");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task TrySendAsync_ReturnsFalse_OnNon2xx(HttpStatusCode statusCode) {
        server.ResponseStatusCode = statusCode;
        using var sender = new TelemetryHttpSender(server.Url);

        var ok = await sender.TrySendAsync("{}", CancellationToken.None);

        ok.Should().BeFalse();
        server.Received.Count.Should().Be(1);
    }

    [Fact]
    public async Task TrySendAsync_ReturnsFalse_OnConnectionFailure_NoThrow() {
        // Ensure a port that has no listener.
        int unusedPort = GetUnusedTcpPort();
        using var sender = new TelemetryHttpSender(new Uri($"http://127.0.0.1:{unusedPort}/"));

        Func<Task> act = async () => {
            var ok = await sender.TrySendAsync("{}", CancellationToken.None);
            ok.Should().BeFalse();
        };

        await act.Should().NotThrowAsync();
    }

    private static int GetUnusedTcpPort() {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally {
            listener.Stop();
        }
    }

    private sealed class LocalHttpServer : IDisposable {
        private readonly HttpListener listener;
        private readonly CancellationTokenSource cts = new();
        private readonly Task loop;

        public Uri Url { get; }
        public HttpStatusCode ResponseStatusCode { get; set; } = HttpStatusCode.OK;

        public ConcurrentQueue<RequestRecord> ReceivedQueue { get; } = new();
        public List<RequestRecord> Received => [.. ReceivedQueue];

        public LocalHttpServer() {
            int port = GetUnusedTcpPort();
            string prefix = $"http://127.0.0.1:{port}/";

            Url = new Uri(prefix, UriKind.Absolute);

            listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();

            loop = Task.Run(() => AcceptLoopAsync(cts.Token));
        }

        public void Dispose() {
            try {
                cts.Cancel();
            }
            catch {
                // swallow
            }

            try {
                listener.Stop();
                listener.Close();
            }
            catch {
                // swallow
            }

            try {
                loop.GetAwaiter().GetResult();
            }
            catch {
                // swallow
            }

            cts.Dispose();
        }

        private async Task AcceptLoopAsync(CancellationToken token) {
            while (!token.IsCancellationRequested) {
                HttpListenerContext ctx;
                try {
                    ctx = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch {
                    break;
                }

                try {
                    string body;
                    using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8)) {
                        body = await reader.ReadToEndAsync(token).ConfigureAwait(false);
                    }

                    ReceivedQueue.Enqueue(new RequestRecord(
                        ctx.Request.HttpMethod,
                        ctx.Request.ContentType,
                        body));

                    ctx.Response.StatusCode = (int)ResponseStatusCode;
                    ctx.Response.ContentType = "application/json";
                    byte[] bytes = Encoding.UTF8.GetBytes("{}");
                    await ctx.Response.OutputStream.WriteAsync(bytes, token);
                }
                catch {
                    // swallow
                }
                finally {
                    try { ctx.Response.OutputStream.Close(); } catch { /* swallow */ }
                    try { ctx.Response.Close(); } catch { /* swallow */ }
                }
            }
        }

        public readonly record struct RequestRecord(string Method, string? ContentType, string Body);
    }
}

// Copyright (c) KeelMatrix

using System.Text;

namespace KeelMatrix.Telemetry.Infrastructure {
    /// <summary>
    /// Handles low-level transmission of telemetry payloads.
    /// </summary>
    internal sealed class TelemetryHttpSender : IDisposable {
#if NET8_0_OR_GREATER
        private readonly HttpClient httpClient = CreateHttpClient();
#else
        private HttpClient httpClient = CreateHttpClient();
        private long createdAtTicks = DateTime.UtcNow.Ticks;
        // Rotate client periodically on netstandard2.0 to avoid stale DNS
        private static readonly TimeSpan ClientLifetime = TimeSpan.FromMinutes(5);

#endif
        private readonly Uri url;

        internal TelemetryHttpSender(Uri url) {
            this.url = url;
        }

        internal async Task<bool> TrySendAsync(string json, CancellationToken token) {
            try {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                var client = GetClient();
                using var response = await client
                    .PostAsync(url, content, token)
                    .ConfigureAwait(false);

                return response.IsSuccessStatusCode;
            }
            catch {
                return false;
            }
        }

        private HttpClient GetClient() {
#if NET8_0_OR_GREATER
            return httpClient;
#else
            // netstandard2.0: periodically rotate client to force DNS re-resolution
            var now = DateTime.UtcNow;
            var created = new DateTime(Interlocked.Read(ref createdAtTicks), DateTimeKind.Utc);

            if (now - created > ClientLifetime) {
                var newClient = CreateHttpClient();
                var old = Interlocked.Exchange(ref httpClient, newClient);
                Interlocked.Exchange(ref createdAtTicks, now.Ticks);

                try { old.Dispose(); } catch { /* swallow */ }
            }

            return httpClient;
#endif
        }

        private static HttpClient CreateHttpClient() {
#if NET8_0_OR_GREATER
            var handler = new SocketsHttpHandler {
                // Forces periodic reconnect -> DNS refresh
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),

                ConnectTimeout = TimeSpan.FromSeconds(3),
                MaxConnectionsPerServer = 2
            };

            return new HttpClient(handler) {
                Timeout = TimeSpan.FromSeconds(3)
            };
#else
            return new HttpClient {
                Timeout = TimeSpan.FromSeconds(3)
            };
#endif
        }

        public void Dispose() {
            try {
                httpClient.Dispose();
            }
            catch {
                // swallow
            }
        }
    }
}

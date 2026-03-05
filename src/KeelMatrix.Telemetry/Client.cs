// Copyright (c) KeelMatrix

using KeelMatrix.Telemetry.Infrastructure;

namespace KeelMatrix.Telemetry {
    public sealed class Client {
        private readonly ITelemetryClient client;

        public Client(string toolName, Type toolType) {
            client = CreateClient(toolName, toolType);
        }

        private static ITelemetryClient CreateClient(string toolName, Type toolType) {
            try {
                if (TelemetryConfig.IsTelemetryDisabled())
                    return new NullTelemetryClient();

                var runtimeContext = new TelemetryRuntimeContext(toolName, toolType);
                var runtimeInfo = new RuntimeInfo();
                var worker = new TelemetryDeliveryWorker(runtimeContext, runtimeInfo);
                return new TelemetryClient(worker);
            }
            catch {
                // Absolute last line of defense
                return new NullTelemetryClient();
            }
        }

        /// <summary>
        /// Records a one-time activation telemetry event for the current project.
        /// The event is emitted only once and is ignored on subsequent calls.
        /// </summary>
        /// <remarks>
        /// This method is safe to call multiple times and never throws.
        /// Telemetry emission is best-effort and may be disabled via environment configuration.
        /// </remarks>
        public void TrackActivation() {
            client.TrackActivation();
        }

        /// <summary>
        /// Records a weekly heartbeat telemetry event indicating continued usage.
        /// At most one heartbeat is emitted per project per ISO week.
        /// </summary>
        /// <remarks>
        /// This method is safe to call multiple times and never throws.
        /// Telemetry emission is best-effort and may be disabled via environment configuration.
        /// </remarks>
        public void TrackHeartbeat() {
            client.TrackHeartbeat();
        }
    }
}

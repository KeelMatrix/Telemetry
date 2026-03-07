// Copyright (c) KeelMatrix

using KeelMatrix.Telemetry.Infrastructure;
using KeelMatrix.Telemetry.ProjectIdentity;

namespace KeelMatrix.Telemetry {
    public sealed class Client {
        private readonly ITelemetryClient client;

        public Client(string toolName, Type toolType) {
            client = CreateClient(toolName, toolType);
        }

        internal Client(
            string toolName,
            Type toolType,
            Func<TelemetryRuntimeContext, RuntimeInfo, IProjectIdentityProvider> projectIdentityProviderFactory) {
            client = CreateClient(toolName, toolType, projectIdentityProviderFactory);
        }

        private static ITelemetryClient CreateClient(string toolName, Type toolType) {
            return CreateClient(toolName, toolType, projectIdentityProviderFactory: null);
        }

        private static ITelemetryClient CreateClient(
            string toolName,
            Type toolType,
            Func<TelemetryRuntimeContext, RuntimeInfo, IProjectIdentityProvider>? projectIdentityProviderFactory) {
            try {
                if (TelemetryConfig.IsTelemetryDisabled())
                    return new NullTelemetryClient();

                TelemetryDeliveryWorker worker;
                if (projectIdentityProviderFactory is null) {
                    worker = TelemetryWorkerRegistry.GetOrCreate(toolName, toolType);
                }
                else {
                    var runtimeContext = new TelemetryRuntimeContext(toolName, toolType);
                    var runtimeInfo = new RuntimeInfo();
                    worker = new TelemetryDeliveryWorker(
                        runtimeContext,
                        runtimeInfo,
                        projectIdentityProviderFactory(runtimeContext, runtimeInfo));
                }

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

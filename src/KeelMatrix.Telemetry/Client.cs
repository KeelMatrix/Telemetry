// Copyright (c) KeelMatrix

using KeelMatrix.Telemetry.Infrastructure;
using KeelMatrix.Telemetry.ProjectIdentity;

namespace KeelMatrix.Telemetry {
    /// <summary>
    /// Provides a minimal, best-effort entry point for emitting anonymous usage telemetry.
    /// </summary>
    /// <remarks>
    /// The client is designed to be safe for library consumers to call from normal execution paths.
    /// Calls are non-blocking, tolerate repeated invocation, and degrade to a no-op when telemetry is disabled
    /// or initialization cannot be completed safely.
    /// </remarks>
    public sealed class Client {
        private readonly ITelemetryClient client;

        /// <summary>
        /// Initializes a telemetry client for the specified tool.
        /// </summary>
        /// <param name="toolName">
        /// A stable identifier for the consuming tool or package. The value is used to derive per-user telemetry storage
        /// and to label emitted events.
        /// </param>
        /// <param name="toolType">
        /// A type from the consuming assembly. The containing assembly version is used as the tool version reported in telemetry.
        /// </param>
        /// <remarks>
        /// Construction is best-effort and does not throw. If telemetry is disabled or the runtime cannot initialize the
        /// underlying pipeline, this instance falls back to a no-op implementation.
        /// </remarks>
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
                // Construction must never surface telemetry failures to the caller.
                return new NullTelemetryClient();
            }
        }

        /// <summary>
        /// Requests a one-time activation telemetry event for the current project.
        /// </summary>
        /// <remarks>
        /// The request is best-effort, non-blocking, and never throws. Repeated calls are safe; after activation has been
        /// recorded for the current project, later calls are ignored.
        /// </remarks>
        public void TrackActivation() {
            client.TrackActivation();
        }

        /// <summary>
        /// Requests a heartbeat telemetry event that indicates continued usage for the current project.
        /// </summary>
        /// <remarks>
        /// The request is best-effort, non-blocking, and never throws. At most one heartbeat is emitted per project per
        /// ISO week, and a newly recorded activation suppresses the heartbeat for that same week.
        /// </remarks>
        public void TrackHeartbeat() {
            client.TrackHeartbeat();
        }
    }
}

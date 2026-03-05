// Copyright (c) KeelMatrix

namespace KeelMatrix.Telemetry {
    /// <summary>
    /// Non-blocking facade for telemetry emission.
    /// Must never perform I/O or block the calling thread.
    /// </summary>
    internal sealed class TelemetryClient : ITelemetryClient {
        private readonly Infrastructure.TelemetryDeliveryWorker worker;

        internal TelemetryClient(Infrastructure.TelemetryDeliveryWorker worker) {
            this.worker = worker;
        }

        public void TrackActivation() {
            try {
                worker.RequestActivation();
            }
            catch {
                // must never throw
            }
        }

        public void TrackHeartbeat() {
            try {
                worker.RequestHeartbeat();
            }
            catch {
                // must never throw
            }
        }
    }
}

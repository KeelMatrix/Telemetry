// Copyright (c) KeelMatrix

namespace KeelMatrix.Telemetry.Events {
    /// <summary>
    /// Represents a weekly heartbeat telemetry event.
    /// </summary>
    internal sealed class HeartbeatEvent : TelemetryEventBase {
        internal HeartbeatEvent(
            string tool,
            string toolVersion,
            string telemetryVersion,
            int schemaVersion,
            string projectHash,
            string week)
            : base("heartbeat", tool, toolVersion, telemetryVersion, schemaVersion, projectHash) {
            Week = week;
        }

        /// <summary>The ISO week string (YYYY-Www).</summary>
        internal string Week { get; }
    }
}

// Copyright (c) KeelMatrix

using System.Globalization;
using KeelMatrix.Telemetry.Events;
using KeelMatrix.Telemetry.Infrastructure;
using KeelMatrix.Telemetry.ProjectIdentity;

namespace KeelMatrix.Telemetry {
    /// <summary>
    /// Encapsulates all telemetry policy and decision-making.
    /// Determines whether telemetry events should be emitted.
    /// </summary>
    internal sealed class TelemetryDispatcher {
        private readonly TelemetryState state;
        private readonly string projectHash;

        internal TelemetryDispatcher(string projectHash) {
            this.projectHash = string.IsNullOrWhiteSpace(projectHash)
                ? ProjectIdentityProvider.ComputeUninitializedPlaceholderHash()
                : projectHash;

            state = new TelemetryState(this.projectHash);
        }

        /// <summary>
        /// Determines whether an activation event should be emitted and,
        /// if so, produces the corresponding event payload.
        /// </summary>
        internal ActivationEvent? TryCreateActivationEvent() {
            if (TelemetryConfig.IsTelemetryDisabled())
                return null;

            if (!state.ShouldSendActivation())
                return null;

            return new ActivationEvent(
                TelemetryConfig.Runtime.ToolName,
                TelemetryConfig.Runtime.ToolVersion,
                TelemetryConfig.TelemetryVersion,
                TelemetryConfig.SchemaVersion,
                projectHash,
                RuntimeInfo.Runtime,
                RuntimeInfo.Os,
                RuntimeInfo.IsCi,
                DateTimeOffset.UtcNow.UtcDateTime.ToString(TelemetryConfig.TimestampFormat, CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Determines whether a heartbeat event should be emitted and,
        /// if so, produces the corresponding event payload.
        /// </summary>
        internal HeartbeatEvent? TryCreateHeartbeatEvent() {
            if (TelemetryConfig.IsTelemetryDisabled())
                return null;

            var week = TelemetryClock.GetCurrentIsoWeek();
            if (!state.ShouldSendHeartbeat(week))
                return null;

            return new HeartbeatEvent(
                TelemetryConfig.Runtime.ToolName,
                TelemetryConfig.Runtime.ToolVersion,
                TelemetryConfig.TelemetryVersion,
                TelemetryConfig.SchemaVersion,
                projectHash,
                week);
        }

        internal void CommitActivation() => state.CommitActivation();
        internal void CommitHeartbeat(string week) => state.CommitHeartbeat(week);
    }
}

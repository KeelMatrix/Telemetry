// Copyright (c) KeelMatrix

using System.Globalization;
using KeelMatrix.Telemetry.Events;
using KeelMatrix.Telemetry.Infrastructure;

namespace KeelMatrix.Telemetry {
    /// <summary>
    /// Encapsulates all telemetry policy and decision-making.
    /// Determines whether telemetry events should be emitted.
    /// </summary>
    internal sealed class TelemetryDispatcher {
        private readonly TelemetryState state;
        private readonly string projectHash;
        private readonly string installationHash;
        private readonly TelemetryRuntimeContext runtimeContext;
        private readonly RuntimeInfo runtimeInfo;

        internal TelemetryDispatcher(
            TelemetryRuntimeContext runtimeContext,
            RuntimeInfo runtimeInfo,
            ProjectIdentity.ResolvedTelemetryIdentity identities) {
            this.runtimeContext = runtimeContext;
            this.runtimeInfo = runtimeInfo;
            if (!identities.HasProjectIdentity)
                throw new ArgumentException("A stable project identity is required to create telemetry events.", nameof(identities));

            projectHash = identities.ProjectHash!;
            installationHash = identities.InstallationHash;
            state = new TelemetryState(runtimeContext.GetRootDirectory(), projectHash);
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
                runtimeContext.ToolName,
                runtimeContext.ToolVersion,
                TelemetryConfig.TelemetryVersion,
                TelemetryConfig.SchemaVersion,
                projectHash,
                installationHash,
                runtimeInfo.Runtime,
                runtimeInfo.Os,
                runtimeInfo.IsCi,
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
                runtimeContext.ToolName,
                runtimeContext.ToolVersion,
                TelemetryConfig.TelemetryVersion,
                TelemetryConfig.SchemaVersion,
                projectHash,
                installationHash,
                week);
        }

        internal void CommitActivation() => state.CommitActivation();
        internal void CommitHeartbeat(string week) => state.CommitHeartbeat(week);
    }
}

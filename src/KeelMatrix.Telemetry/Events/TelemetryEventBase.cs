// Copyright (c) KeelMatrix

namespace KeelMatrix.Telemetry.Events {
    /// <summary>
    /// Base type for all telemetry events.
    /// Contains fields common to all payloads.
    /// </summary>
    internal abstract class TelemetryEventBase {
        protected TelemetryEventBase(
            string @event,
            string tool,
            string toolVersion,
            string telemetryVersion,
            int schemaVersion,
            string projectHash) {
            Event = @event;
            Tool = tool;
            ToolVersion = toolVersion;
            TelemetryVersion = telemetryVersion;
            SchemaVersion = schemaVersion;
            ProjectHash = projectHash;
        }

        /// <summary>The event type identifier.</summary>
        public string Event { get; }

        /// <summary>The telemetry tool name.</summary>
        public string Tool { get; }

        /// <summary>The calling library's tool version.</summary>
        public string ToolVersion { get; }

        /// <summary>The telemetry (this library) version.</summary>
        public string TelemetryVersion { get; }

        /// <summary>The telemetry schema version.</summary>
        public int SchemaVersion { get; }

        /// <summary>A stable, anonymous project identifier.</summary>
        public string ProjectHash { get; }
    }
}

// Copyright (c) KeelMatrix

namespace KeelMatrix.Telemetry.Events {
    /// <summary>
    /// Represents a one-time activation telemetry event.
    /// </summary>
    internal sealed class ActivationEvent : TelemetryEventBase {
        internal ActivationEvent(
            string tool,
            string toolVersion,
            string telemetryVersion,
            int schemaVersion,
            string projectHash,
            string runtime,
            string os,
            bool ci,
            string timestamp)
            : base("activation", tool, toolVersion, telemetryVersion, schemaVersion, projectHash) {
            Runtime = runtime;
            Os = os;
            Ci = ci;
            Timestamp = timestamp;
        }

        /// <summary>The runtime identifier (e.g. net8.0).</summary>
        internal string Runtime { get; }

        /// <summary>The operating system identifier.</summary>
        internal string Os { get; }

        /// <summary>Indicates whether the tool is running in CI.</summary>
        internal bool Ci { get; }

        /// <summary>The UTC timestamp of activation.</summary>
        internal string Timestamp { get; }
    }
}

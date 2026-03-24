// Copyright (c) KeelMatrix

namespace KeelMatrix.Telemetry.ProjectIdentity {
    /// <summary>
    /// Resolved anonymous identities for telemetry emitted by a single installation context.
    /// </summary>
    internal sealed record ResolvedTelemetryIdentity(string? ProjectHash, string InstallationHash) {
        /// <summary>
        /// Indicates whether a stable anonymous consuming-codebase identity was resolved.
        /// </summary>
        internal bool HasProjectIdentity => !string.IsNullOrWhiteSpace(ProjectHash);
    }
}

// Copyright (c) KeelMatrix

namespace KeelMatrix.Telemetry {
    /// <summary>
    /// Identifies the scope that supplied the effective telemetry status.
    /// </summary>
    public enum RepositoryTelemetryScope {
        /// <summary>
        /// No override was found and the repo-local default applies.
        /// </summary>
        RepoLocalDefault = 0,

        /// <summary>
        /// A process environment variable supplied the effective status.
        /// </summary>
        ProcessEnvironment = 1,

        /// <summary>
        /// A file inside the resolved repository root supplied the effective status.
        /// </summary>
        RepoLocal = 2
    }
}

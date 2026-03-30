// Copyright (c) KeelMatrix

namespace KeelMatrix.Telemetry {
    /// <summary>
    /// Identifies the highest-precedence source that determined the effective telemetry status.
    /// </summary>
    public enum RepositoryTelemetrySourceKind {
        /// <summary>
        /// No process-level or repo-local override was found.
        /// </summary>
        None = 0,

        /// <summary>
        /// A process environment variable determined the effective status.
        /// </summary>
        ProcessEnvironment = 1,

        /// <summary>
        /// A repository-local <c>keelmatrix.telemetry.json</c> file determined the effective status.
        /// </summary>
        RepositoryConfig = 2,

        /// <summary>
        /// A repository-local <c>.env.local</c> file determined the effective status.
        /// </summary>
        DotEnvLocal = 3,

        /// <summary>
        /// A repository-local <c>.env</c> file determined the effective status.
        /// </summary>
        DotEnv = 4
    }
}

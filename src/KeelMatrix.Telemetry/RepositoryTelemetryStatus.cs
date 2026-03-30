// Copyright (c) KeelMatrix

namespace KeelMatrix.Telemetry {
    /// <summary>
    /// Describes the raw effective telemetry decision for a single repository root.
    /// </summary>
    public sealed class RepositoryTelemetryStatus {
        internal RepositoryTelemetryStatus(
            bool isEnabled,
            RepositoryTelemetrySourceKind winningSourceKind,
            string? winningPath,
            string? winningVariableName,
            RepositoryTelemetryScope scope,
            string repoRoot) {
            IsEnabled = isEnabled;
            WinningSourceKind = winningSourceKind;
            WinningPath = winningPath;
            WinningVariableName = winningVariableName;
            Scope = scope;
            RepoRoot = repoRoot;
        }

        /// <summary>
        /// Gets a value indicating whether telemetry is effectively enabled.
        /// </summary>
        public bool IsEnabled { get; }

        /// <summary>
        /// Gets the highest-precedence source that determined the effective status.
        /// </summary>
        public RepositoryTelemetrySourceKind WinningSourceKind { get; }

        /// <summary>
        /// Gets the winning repo-local file path when a repo-local file determined the status.
        /// Returns <see langword="null"/> when the winning source was a process environment variable or no override was found.
        /// </summary>
        public string? WinningPath { get; }

        /// <summary>
        /// Gets the winning environment variable name when the decision came from a variable.
        /// Returns <see langword="null"/> when the winning source was repo-local or no override was found.
        /// </summary>
        public string? WinningVariableName { get; }

        /// <summary>
        /// Gets the scope that supplied the effective status.
        /// </summary>
        public RepositoryTelemetryScope Scope { get; }

        /// <summary>
        /// Gets the repository root that was evaluated.
        /// </summary>
        public string RepoRoot { get; }
    }
}

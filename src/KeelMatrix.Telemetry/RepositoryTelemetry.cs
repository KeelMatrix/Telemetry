// Copyright (c) KeelMatrix

using KeelMatrix.Telemetry.ProjectIdentity;

namespace KeelMatrix.Telemetry {
    /// <summary>
    /// Provides repo-local telemetry inspection helpers for a single repository root.
    /// </summary>
    public static class RepositoryTelemetry {
        /// <summary>
        /// Resolves the repository root reachable from the provided starting directory.
        /// </summary>
        /// <param name="startingDirectory">The directory to start walking upward from.</param>
        /// <param name="repositoryRoot">The resolved repository root when one is found.</param>
        /// <returns><see langword="true"/> when a repository root is found; otherwise, <see langword="false"/>.</returns>
        public static bool TryResolveRepositoryRoot(string startingDirectory, out string repositoryRoot) {
            return GitDiscovery.TryFindRepositoryRoot(startingDirectory, out repositoryRoot);
        }

        /// <summary>
        /// Evaluates the effective telemetry status for the provided repository root.
        /// </summary>
        /// <param name="repositoryRoot">The repository root to inspect.</param>
        /// <returns>The effective telemetry status for that repository root.</returns>
        public static RepositoryTelemetryStatus GetEffectiveStatus(string repositoryRoot) {
            return TelemetryDisableResolver.GetEffectiveStatus(repositoryRoot);
        }
    }
}

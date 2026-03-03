// Copyright (c) KeelMatrix

namespace KeelMatrix.Telemetry {
    /// <summary>
    /// Tracks local telemetry idempotency using atomic marker files.
    /// File existence represents committed state. No locks are used.
    /// </summary>
    internal sealed class TelemetryState {
        private readonly string markerDir;
        private readonly string projectHash;

        /// <summary>
        /// Initializes a new instance for the given project hash.
        /// </summary>
        internal TelemetryState(string projectHash) {
            this.projectHash = projectHash;
            markerDir = Path.Combine(TelemetryConfig.Runtime.GetRootDirectory(), "markers");
            TryEnsureDirectory(markerDir);
            TryCleanup(markerDir);
        }

        /// <summary>
        /// Returns true if activation has not yet been recorded.
        /// </summary>
        internal bool ShouldSendActivation() {
            return !File.Exists(GetActivationPath(markerDir, projectHash));
        }

        /// <summary>
        /// Returns true if no heartbeat exists for the given ISO week.
        /// </summary>
        internal bool ShouldSendHeartbeat(string isoWeek) {
            return !File.Exists(GetHeartbeatPath(markerDir, projectHash, isoWeek));
        }

        /// <summary>
        /// Atomically records activation using CreateNew semantics.
        /// </summary>
        internal void CommitActivation() {
            TryCreateMarker(GetActivationPath(markerDir, projectHash));
        }

        /// <summary>
        /// Atomically records heartbeat for the given ISO week.
        /// </summary>
        internal void CommitHeartbeat(string isoWeek) {
            TryCreateMarker(GetHeartbeatPath(markerDir, projectHash, isoWeek));
        }

        /// <summary>
        /// Attempts to create a marker file atomically.
        /// </summary>
        private static void TryCreateMarker(string path) {
            try {
                using var _ = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch {
                // already exists or filesystem failure → ignore
            }
        }

        /// <summary>
        /// Deletes oldest marker files when count exceeds limit.
        /// </summary>
        private static void TryCleanup(string markerDir) {
            try {
                var files = Directory.EnumerateFiles(markerDir, "*.json")
                                     .Select(p => new FileInfo(p))
                                     .OrderBy(f => f.LastWriteTimeUtc)
                                     .ToList();

                var excess = files.Count - TelemetryConfig.MaxMarkerFiles;
                if (excess <= 0)
                    return;

                foreach (var f in files.Take(excess)) {
                    try { f.Delete(); } catch { /* swallow */ }
                }
            }
            catch {
                // swallow
            }
        }

        /// <summary>
        /// Ensures marker directory exists.
        /// </summary>
        private static void TryEnsureDirectory(string markerDir) {
            try {
                Directory.CreateDirectory(markerDir);
            }
            catch {
                // swallow
            }
        }

        /// <summary>
        /// Resolves activation marker path.
        /// </summary>
        private static string GetActivationPath(string markerDir, string projectHash) {
            return Path.Combine(markerDir, $"activation.{projectHash}.json");
        }

        /// <summary>
        /// Resolves heartbeat marker path.
        /// </summary>
        private static string GetHeartbeatPath(string markerDir, string projectHash, string week) {
            return Path.Combine(markerDir, $"heartbeat.{projectHash}.{week}.json");
        }
    }
}

// Copyright (c) KeelMatrix

namespace KeelMatrix.Telemetry {
    /// <summary>
    /// Tracks local telemetry idempotency using atomic marker files.
    /// File existence represents committed state. No locks are used.
    /// </summary>
    internal sealed class TelemetryState {
        private readonly string markerDir;
        private readonly string projectHash;
        private bool activationKnownCommitted;
        private readonly HashSet<string> heartbeatWeeksKnownCommitted = new(StringComparer.Ordinal);

        /// <summary>
        /// Initializes a new instance for the given project hash.
        /// </summary>
        internal TelemetryState(string rootDirectory, string projectHash) {
            this.projectHash = projectHash;
            markerDir = Path.Combine(rootDirectory, "markers");
            TryEnsureDirectory(markerDir);
            TryCleanup(markerDir);
        }

        /// <summary>
        /// Returns true if activation has not yet been recorded.
        /// </summary>
        internal bool ShouldSendActivation() {
            if (activationKnownCommitted)
                return false;

            var path = GetActivationPath(markerDir, projectHash);
            if (!SafeFileExists(path))
                return true;

            activationKnownCommitted = true;
            return false;
        }

        /// <summary>
        /// Returns true if no heartbeat exists for the given ISO week.
        /// </summary>
        internal bool ShouldSendHeartbeat(string isoWeek) {
            if (heartbeatWeeksKnownCommitted.Contains(isoWeek))
                return false;

            var path = GetHeartbeatPath(markerDir, projectHash, isoWeek);
            if (!SafeFileExists(path))
                return true;

            heartbeatWeeksKnownCommitted.Add(isoWeek);
            return false;
        }

        /// <summary>
        /// Atomically records activation using CreateNew semantics.
        /// </summary>
        internal void CommitActivation() {
            if (TryCreateMarker(GetActivationPath(markerDir, projectHash)))
                activationKnownCommitted = true;
        }

        /// <summary>
        /// Atomically records heartbeat for the given ISO week.
        /// </summary>
        internal void CommitHeartbeat(string isoWeek) {
            if (TryCreateMarker(GetHeartbeatPath(markerDir, projectHash, isoWeek)))
                heartbeatWeeksKnownCommitted.Add(isoWeek);
        }

        /// <summary>
        /// Attempts to create a marker file atomically and reports whether the committed marker is known to exist.
        /// </summary>
        private static bool TryCreateMarker(string path) {
            try {
                using var _ = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return true;
            }
            catch {
                // Already exists or creation failed. Only cache positive state if the marker is known to exist.
                return SafeFileExists(path);
            }
        }

        private static bool SafeFileExists(string path) {
            try {
                return File.Exists(path);
            }
            catch {
                return false;
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

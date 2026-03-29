// Copyright (c) KeelMatrix

namespace KeelMatrix.Telemetry {
    /// <summary>
    /// Central configuration for telemetry constants and limits.
    /// </summary>
    internal static class TelemetryConfig {
        private const string KeelMatrixTelemetryUrl = "https://telemetry.keelmatrix.com";

        private static readonly Uri ProductionUrl =
            new(KeelMatrixTelemetryUrl, UriKind.Absolute);

        private static Uri? urlOverrideForTests;

        internal static Uri Url =>
            Volatile.Read(ref urlOverrideForTests) ?? ProductionUrl;

        // Integration tests can point telemetry at a local loopback endpoint instead of production.
        internal static void SetUrlOverrideForTests(Uri? uri) {
            if (uri is not null) {
                if (!uri.IsAbsoluteUri)
                    throw new ArgumentException("Override URL must be absolute.", nameof(uri));

                // Allow plain HTTP for local test servers.
                if (uri.Scheme is not ("http" or "https"))
                    throw new ArgumentException("Override URL must use http or https.", nameof(uri));
            }

            Volatile.Write(ref urlOverrideForTests, uri);
        }

        internal static class ProjectIdentity {
            internal const int MaxUpwardSteps = 32;
            internal const int MaxConfigBytes = 512 * 1024;
            internal const int MaxPackedRefsBytes = 512 * 1024;
            internal const int MaxObjectBytesDecompressed = 512 * 1024;
            internal const int MaxCommitParentTraversal = 256;
            internal const int MaxFileBytes = 512 * 1024;
            internal const int MaxTotalFiles = 7;
            internal const int MaxProjectFiles = 3;
            internal const int MaxRecursiveDirs = 128;
            internal const int MaxRecursiveFiles = 1024;
            internal static readonly char[] Separator = [';'];
        }

        internal const string UnknownSymbol = "unknown";
        internal static string TelemetryVersion { get; }
            = typeof(Client).Assembly.GetName().Version?.ToString() ?? UnknownSymbol;

        internal const int SchemaVersion = 1;
        internal const int MaxPayloadBytes = 768;
        internal const int RuntimeMaxLength = 32;
        internal const int ToolVersionMaxLength = 16;
        internal const int ProjectHashMaxLength = 64;
        internal const int InstallationHashMaxLength = 64;
        internal const int OsMaxLength = 16;
        internal const int MaxDeadLetterItems = 400;
        internal const int MaxPendingItems = 128;
        internal const int MaxSendAttempts = 12;
        internal const int ExpectedSaltBytes = 32;
        internal const int MaxSaltFileBytes = 4 * 1024; // 4KB hard cap
        internal const int MaxMarkerFiles = 1024;
        internal static readonly TimeSpan ProcessingStaleThreshold = TimeSpan.FromMinutes(5);
        internal const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
        private static int processDisabled; // 0/1
        private static readonly object repositoryDisableDecisionLock = new();
        // Process-execution memoization for worker-thread repo-local disable discovery,
        // scoped to the current resolved repository root set.
        private static string? repositoryDisableDecisionKey;
        private static int repositoryDisableDecision = -1; // -1 unresolved, 0 enabled, 1 disabled

        internal static string ResolveRootDirectory(string toolNameUpper) {
            var safeToolName = SanitizeToolNameForPath(toolNameUpper);

            try {
                // 1) Preferred: LocalApplicationData (per-user, non-roaming).
                var local = SafeGetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (IsUsableAbsolutePath(local))
                    return Path.Combine(local, "KeelMatrix", safeToolName);

                // 2) Fallback: ApplicationData (roaming). Still per-user and usually writable.
                var roaming = SafeGetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (IsUsableAbsolutePath(roaming))
                    return Path.Combine(roaming, "KeelMatrix", safeToolName);

                // 3) Fallback: UserProfile (cross-platform). Use ".local/share" on Unix-like.
                var userProfile = SafeGetFolderPath(Environment.SpecialFolder.UserProfile);
                if (IsUsableAbsolutePath(userProfile)) {
                    if (Path.DirectorySeparatorChar != '\\') {
                        // ~/.local/share/KeelMatrix/{ToolNameUpper}
                        return Path.Combine(userProfile, ".local", "share", "KeelMatrix", safeToolName);
                    }

                    // Windows: keep it simple under user profile if nothing else is available.
                    return Path.Combine(userProfile, "AppData", "Local", "KeelMatrix", safeToolName);
                }

                // 4) Last resort: temp (always absolute).
                var temp = Path.GetTempPath();
                if (IsUsableAbsolutePath(temp))
                    return Path.Combine(temp, "KeelMatrix", safeToolName);
            }
            catch {
                // swallow and fall through to absolute temp fallback
            }

            // Absolute last line of defense: hard fallback to temp.
            return Path.Combine(Path.GetTempPath(), "KeelMatrix", safeToolName);

            static string SafeGetFolderPath(Environment.SpecialFolder folder) {
                try { return Environment.GetFolderPath(folder) ?? string.Empty; }
                catch { return string.Empty; }
            }

            static bool IsUsableAbsolutePath(string path) {
                try {
                    if (string.IsNullOrWhiteSpace(path))
                        return false;

                    path = path.Trim();

                    // Must be rooted to avoid writing under CWD.
                    if (!Path.IsPathRooted(path))
                        return false;

                    // Normalize; will throw on malformed paths.
                    _ = Path.GetFullPath(path);
                    return true;
                }
                catch {
                    return false;
                }
            }
        }

        private static string SanitizeToolNameForPath(string? toolNameUpper) {
            if (string.IsNullOrWhiteSpace(toolNameUpper))
                return UnknownSymbol;

            var trimmedToolName = toolNameUpper!.Trim();
            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new System.Text.StringBuilder(trimmedToolName.Length);

            foreach (var ch in trimmedToolName) {
                if (ch == Path.DirectorySeparatorChar
                    || ch == Path.AltDirectorySeparatorChar
                    || Array.IndexOf(invalidChars, ch) >= 0
                    || char.IsControl(ch)) {
                    builder.Append('_');
                }
                else {
                    builder.Append(ch);
                }
            }

            var sanitized = builder.ToString().Trim().TrimEnd('.', ' ');
            if (sanitized.Length == 0 || sanitized == "." || sanitized == "..")
                return UnknownSymbol;

            return sanitized;
        }

        internal static void DisableTelemetryForCurrentProcess() {
            Interlocked.Exchange(ref processDisabled, 1);
        }

        internal static void ResetProcessDisabledForTests() {
            Interlocked.Exchange(ref processDisabled, 0);
            Volatile.Write(ref repositoryDisableDecision, -1);
            Volatile.Write(ref repositoryDisableDecisionKey, null);
            TelemetryDisableResolver.SetRepositoryDisableOverrideForTests(null);
        }

        /// <summary>
        /// Determines whether telemetry is disabled for the current process through process-local
        /// or process-environment opt-out signals.
        /// </summary>
        internal static bool IsTelemetryDisabled() {
            // Process-local hard disable
            if (Volatile.Read(ref processDisabled) == 1)
                return true;

            return TelemetryDisableResolver.IsProcessTelemetryDisabled();
        }

        /// <summary>
        /// Resolves repo-local opt-out on the worker thread and promotes it to a process-local disable.
        /// Caller-thread paths must not invoke repo-local discovery because it may perform filesystem I/O.
        /// </summary>
        internal static bool ResolveRepositoryTelemetryDisableOnWorkerThread() {
            if (Volatile.Read(ref processDisabled) == 1)
                return true;

            var processDecision = TelemetryDisableResolver.GetProcessTelemetryDisableDecision();
            if (processDecision.HasValue) {
                if (processDecision.Value)
                    DisableTelemetryForCurrentProcess();

                return processDecision.Value;
            }

            var repositoryRoots = TelemetryDisableResolver.GetCandidateRepositoryRoots();
            var repositoryDecisionKey = CreateRepositoryDisableDecisionKey(repositoryRoots);
            var cachedRepositoryDecisionKey = Volatile.Read(ref repositoryDisableDecisionKey);
            var repositoryDecision = Volatile.Read(ref repositoryDisableDecision);
            if (repositoryDecision == 1 && string.Equals(cachedRepositoryDecisionKey, repositoryDecisionKey, StringComparison.Ordinal)) {
                DisableTelemetryForCurrentProcess();
                return true;
            }

            if (repositoryDecision == 0 && string.Equals(cachedRepositoryDecisionKey, repositoryDecisionKey, StringComparison.Ordinal))
                return false;

            lock (repositoryDisableDecisionLock) {
                cachedRepositoryDecisionKey = Volatile.Read(ref repositoryDisableDecisionKey);
                repositoryDecision = Volatile.Read(ref repositoryDisableDecision);
                if (repositoryDecision == -1 || !string.Equals(cachedRepositoryDecisionKey, repositoryDecisionKey, StringComparison.Ordinal)) {
                    repositoryDecision = TelemetryDisableResolver.IsRepositoryTelemetryDisabledOnWorkerThread(repositoryRoots) ? 1 : 0;
                    Volatile.Write(ref repositoryDisableDecisionKey, repositoryDecisionKey);
                    Volatile.Write(ref repositoryDisableDecision, repositoryDecision);
                }
            }

            if (repositoryDecision == 0)
                return false;

            DisableTelemetryForCurrentProcess();
            return true;
        }

        private static string CreateRepositoryDisableDecisionKey(IReadOnlyList<string> repositoryRoots) {
            if (repositoryRoots.Count == 0)
                return string.Empty;

            var comparer = Path.DirectorySeparatorChar == '\\'
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var normalizedRoots = repositoryRoots.ToArray();
            Array.Sort(normalizedRoots, comparer);
            return string.Join("\n", normalizedRoots);
        }
    }
}

// Copyright (c) KeelMatrix

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace KeelMatrix.Telemetry.ProjectIdentity {
    internal sealed class MachineSaltProvider {
        // The five-second budget bounds only publication and corrupt-salt recovery lock
        // acquisition/retry waits, shared by both lock attempts; it is not five seconds per lock.
        // Synchronous filesystem operations (directory creation, read, flush, move, delete) are
        // not deadline-bounded, so a pathologically slow filesystem can exceed this budget.
        private static readonly TimeSpan SaltResolutionBudget = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan SaltPublicationLockRetryDelay = TimeSpan.FromMilliseconds(10);
        private readonly TelemetryRuntimeContext runtimeContext;

        internal MachineSaltProvider(TelemetryRuntimeContext runtimeContext) {
            this.runtimeContext = runtimeContext;
        }

        /// <summary>
        /// Best-effort read-or-create of the persisted installation salt used to derive <c>installation_hash</c>.
        /// Stored at:
        /// Path.Combine(TelemetryRuntimeContext.GetRootDirectory(), "telemetry.salt")
        /// The persisted format remains a hex string of 32 random bytes.
        /// </summary>
        internal byte[] GetOrCreateMachineSaltBytes() {
            var path = ResolveSaltPath();

            // If telemetry is already disabled for this process, don't do I/O; return a valid value.
            // (Caller should still respect IsTelemetryDisabled() and not emit.)
            if (TelemetryConfig.IsTelemetryDisabled())
                return GenerateRandomSaltBytes();

            var resolutionDeadline = CreateResolutionDeadline();
            TryEnsureDirectory(path);

            // 1) Try read existing, with size cap + strict validation.
            if (TryReadPersistedSalt(path, out var existing))
                return existing;

            // 2) Regenerate once and require persistence. A process that loses the publication race
            // must use the valid winner, never replace it with its own value.
            var newSalt = GenerateRandomSaltBytes();
            if (TryPersistSaltAtomically(path, newSalt, resolutionDeadline)) {
                if (TryReadPersistedSalt(path, out var reread))
                    return reread;

                // If we can't read back what we wrote, treat as persistence failure.
            }

            // A competing process may have published a winner after our first read or failed
            // publication attempt. Always prefer that valid value before attempting recovery.
            if (HasResolutionTimeRemaining(resolutionDeadline) && TryReadPersistedSalt(path, out var winner))
                return winner;

            // Corrupt or oversized content may be recovered only while holding an atomic,
            // process-independent recovery lease. A stale lease fails closed rather than
            // risking deletion of a valid winner.
            if (HasResolutionTimeRemaining(resolutionDeadline) &&
                TryRecoverCorruptSalt(path, resolutionDeadline) &&
                TryReadPersistedSalt(path, out var recovered))
                return recovered;

            // 3) Cannot persist => disable telemetry for the current process.
            TelemetryConfig.DisableTelemetryForCurrentProcess();
            return GenerateRandomSaltBytes();
        }

        private static bool TryReadPersistedSalt(string path, out byte[] bytes) {
            bytes = [];

            try {
                var fi = new FileInfo(path);
                if (!fi.Exists)
                    return false;

                // Hard cap to avoid loading attacker/corrupt large files into memory.
                if (fi.Length <= 0 || fi.Length > TelemetryConfig.MaxSaltFileBytes)
                    return false;

                // Read as text (expected hex), but still validate strictly.
                var text = SafeReadAllText(path);
                if (string.IsNullOrWhiteSpace(text))
                    return false;

                text = text.Trim();

                // Preferred: strict hex of 32 bytes => 64 hex chars.
                if (text.Length == TelemetryConfig.ExpectedSaltBytes * 2 &&
                    TryDecodeHex(text, out var decoded) &&
                    decoded.Length == TelemetryConfig.ExpectedSaltBytes) {
                    bytes = decoded;
                    return true;
                }

                // Invalid content: treat as corrupt. Caller will regenerate and require persistence.
                return false;
            }
            catch {
                return false;
            }
        }

        private static bool TryPersistSaltAtomically(string path, byte[] saltBytes, long resolutionDeadline) {
            if (!TryAcquireSaltPublicationLock(path, resolutionDeadline, out var publicationLock))
                return false;

            try {
                // A valid value published by another process while this caller waited is the winner.
                if (TryReadPersistedSalt(path, out _))
                    return true;

                // Never replace an existing corrupt value here. Recovery owns deletion under the same lock.
                if (File.Exists(path))
                    return false;

                return TryPublishSaltWhileLocked(path, saltBytes);
            }
            finally {
                try { publicationLock!.Dispose(); } catch { /* swallow */ }
            }
        }

        private static bool TryRecoverCorruptSalt(string path, long resolutionDeadline) {
            if (!TryAcquireSaltPublicationLock(path, resolutionDeadline, out var publicationLock))
                return false;

            try {
                // Re-check under the recovery lease. Another process may have published
                // a valid salt between the caller's read and lease acquisition.
                if (TryReadPersistedSalt(path, out _))
                    return true;

                try {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch {
                    return false;
                }

                var replacement = GenerateRandomSaltBytes();
                return TryPublishSaltWhileLocked(path, replacement) || TryReadPersistedSalt(path, out _);
            }
            catch {
                return false;
            }
            finally {
                try { publicationLock!.Dispose(); } catch { /* swallow */ }
            }
        }

        private static bool TryPublishSaltWhileLocked(string path, byte[] saltBytes) {
            string? tmp = null;

            try {
                var saltHex = ProjectIdentityProvider.ToLowerHex(saltBytes);
                tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    var bytes = Encoding.UTF8.GetBytes(saltHex);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

#if NET8_0_OR_GREATER
                File.Move(tmp, path, overwrite: false);
#else
                // File.Move does not replace an existing destination on netstandard2.0.
                File.Move(tmp, path);
#endif
                return true;
            }
            catch {
                return false;
            }
            finally {
                if (!string.IsNullOrEmpty(tmp)) {
                    try { File.Delete(tmp); } catch { /* swallow */ }
                }
            }
        }

        private static bool TryAcquireSaltPublicationLock(string path, long resolutionDeadline, out FileStream? publicationLock) {
            var lockPath = path + ".lock";

            while (HasResolutionTimeRemaining(resolutionDeadline)) {
                try {
                    publicationLock = new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 1,
                        options: FileOptions.None);
                    return true;
                }
                catch (IOException) {
                    var remainingMilliseconds = GetRemainingMilliseconds(resolutionDeadline);
                    if (remainingMilliseconds <= 0)
                        break;

                    Thread.Sleep(Math.Min(
                        (int)SaltPublicationLockRetryDelay.TotalMilliseconds,
                        remainingMilliseconds));
                }
                catch {
                    publicationLock = null;
                    return false;
                }
            }

            publicationLock = null;
            return false;
        }

        private static long CreateResolutionDeadline() {
            var budgetTicks = (long)(SaltResolutionBudget.TotalSeconds * Stopwatch.Frequency);
            return Stopwatch.GetTimestamp() + budgetTicks;
        }

        private static bool HasResolutionTimeRemaining(long resolutionDeadline) {
            return Stopwatch.GetTimestamp() < resolutionDeadline;
        }

        private static int GetRemainingMilliseconds(long resolutionDeadline) {
            var remainingTicks = resolutionDeadline - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
                return 0;

            var remainingMilliseconds = remainingTicks * 1000 / Stopwatch.Frequency;
            return (int)Math.Min(remainingMilliseconds, int.MaxValue);
        }

        private static byte[] GenerateRandomSaltBytes() {
            var bytes = new byte[TelemetryConfig.ExpectedSaltBytes];
            using (var rng = RandomNumberGenerator.Create()) {
                rng.GetBytes(bytes);
            }
            return bytes;
        }

        private static bool TryDecodeHex(string hex, out byte[] bytes) {
            bytes = [];

            if (string.IsNullOrWhiteSpace(hex))
                return false;

            hex = hex.Trim();
            if ((hex.Length & 1) != 0)
                return false;

            int len = hex.Length / 2;
            var result = new byte[len];

            for (int i = 0; i < len; i++) {
                int hi = HexValue(hex[i * 2]);
                int lo = HexValue(hex[(i * 2) + 1]);
                if (hi < 0 || lo < 0)
                    return false;

                result[i] = (byte)((hi << 4) | lo);
            }

            bytes = result;
            return true;
        }

        private static int HexValue(char c) {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        private string ResolveSaltPath() {
            return Path.Combine(runtimeContext.GetRootDirectory(), "telemetry.salt");
        }

        private static void TryEnsureDirectory(string path) {
            try {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
            }
            catch {
                // swallow
            }
        }

        private static string SafeReadAllText(string path) {
            try {
                return File.ReadAllText(path).Trim();
            }
            catch {
                return string.Empty;
            }
        }
    }
}

// Copyright (c) KeelMatrix

using System.Globalization;
using System.Text;
using KeelMatrix.Telemetry.Storage;

namespace KeelMatrix.Telemetry.Infrastructure {
    /// <summary>
    /// Filesystem-backed durable queue using one JSON file per entry.
    /// Safe across crashes and multiple processes.
    /// </summary>
    internal sealed class DurableTelemetryQueue : ITelemetryQueue {
        private const string QueueFileTimestampFormat = "yyyyMMddHHmmssfffffff";
        private const int QueueFileTimestampLength = 21;
        private const int MaxEnvelopeBytes = 4096;
        private const string ClaimLockSuffix = ".lock";
        private const string PendingClaimLockSuffix = ".claiming.lock";

        private readonly TelemetryRuntimeContext runtimeContext;
        private readonly string pendingDir;
        private readonly string processingDir;
        private readonly string deadLetterDir;

        internal static ITelemetryQueue? CreateSafe(TelemetryRuntimeContext runtimeContext) {
            try { return new DurableTelemetryQueue(runtimeContext); }
            catch { return null; }
        }

        private DurableTelemetryQueue(TelemetryRuntimeContext runtimeContext) {
            this.runtimeContext = runtimeContext;
            string root = ResolveQueueRoot();
            pendingDir = Path.Combine(root, "pending");
            processingDir = Path.Combine(root, "processing");
            deadLetterDir = Path.Combine(root, "dead");

            Directory.CreateDirectory(pendingDir);
            Directory.CreateDirectory(processingDir);
            Directory.CreateDirectory(deadLetterDir);

            CleanupTmpFiles(pendingDir);
            CleanupTmpFiles(processingDir);
            CleanupTmpFiles(deadLetterDir);

            CrashRecovery();
        }

        private static void CleanupTmpFiles(string dir) {
            string[] files;
            try {
                files = Directory.EnumerateFiles(dir, "*.tmp").ToArray();
            }
            catch {
                // swallow
                return;
            }

            var nowUtc = TelemetryClock.UtcNow;
            foreach (var file in files) {
                try {
                    var lastWriteUtc = File.GetLastWriteTimeUtc(file);
                    if (lastWriteUtc == DateTime.MinValue || lastWriteUtc > nowUtc)
                        continue;

                    if (nowUtc - lastWriteUtc < TelemetryConfig.ProcessingStaleThreshold)
                        continue;

                    var ownershipPath = file + ".lock";
                    if (File.Exists(ownershipPath)) {
                        // A producer keeps this sidecar open until its temp file is moved.
                        // An abandoned sidecar can be acquired and removed with the temp.
                        try {
                            using (new FileStream(ownershipPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                        }
                        catch {
                            continue;
                        }

                        SafeDelete(file);
                        SafeDelete(ownershipPath);
                        continue;
                    }

                    // A writer owns its uniquely named temp file while it is open. Do not
                    // remove it if another process still holds that ownership lock.
                    using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    SafeDelete(file);
                }
                catch {
                    // The file may be an active writer or may have disappeared concurrently.
                }
            }
        }

        private void CrashRecovery() {
            RecoverStaleClaims();
        }

        /// <summary>
        /// Returns claims whose processing lease has expired to pending. The last-write time
        /// records when the claim was acquired; a live claim is not reclaimed while it remains
        /// within the five-minute lease.
        /// Lease expiry can produce a duplicate delivery window, so delivery is best-effort
        /// rather than exactly-once.
        /// </summary>
        private void RecoverStaleClaims() {
            var nowUtc = TelemetryClock.UtcNow;
            string[] files;

            try {
                files = Directory.EnumerateFiles(processingDir, "*.json").ToArray();
            }
            catch {
                return;
            }

            foreach (var file in files) {
                FileStream? claimLock = null;
                var moved = false;
                try {
                    // Only production-generated claim names are recoverable.
                    if (!TryParseTimestampFromFilename(file, out _, out _))
                        continue;

                    DateTime lastWriteUtc;
                    try {
                        lastWriteUtc = File.GetLastWriteTimeUtc(file);
                    }
                    catch {
                        // If we can't read the timestamp, we can't safely decide it's stale.
                        continue;
                    }

                    // Defensive: LastWriteTimeUtc can sometimes be default/invalid or in the future due to clock skew.
                    if (lastWriteUtc == DateTime.MinValue || lastWriteUtc > nowUtc)
                        continue;

                    var age = nowUtc - lastWriteUtc;
                    if (age < TelemetryConfig.ProcessingStaleThreshold)
                        continue;

                    var target = Path.Combine(pendingDir, Path.GetFileName(file));
                    // Never overwrite a pending copy another process may already own.
                    if (File.Exists(target))
                        continue;

                    // Claim terminal operations and stale recovery are conditional on the
                    // same generation marker. A live terminal operation wins the race, or
                    // recovery wins it; the loser must not touch a later claim.
                    var claimLockPath = GetClaimLockPath(file);
                    if (File.Exists(claimLockPath)) {
                        try {
                            claimLock = new FileStream(
                                claimLockPath,
                                FileMode.Open,
                                FileAccess.ReadWrite,
                                FileShare.None);
                        }
                        catch {
                            continue;
                        }
                    }

                    File.Move(file, target);
                    moved = true;
                }
                catch { /* swallow */ }
                finally {
                    try { claimLock?.Dispose(); }
                    catch { /* swallow */ }

                    if (moved)
                        SafeDelete(GetClaimLockPath(file));
                }
            }
        }

        /// <summary>
        /// Enqueues a payload to disk using atomic tmp + rename.
        /// </summary>
        public bool Enqueue(string payloadJson) {
            try {
                EnforceLimit();

                var envelope = new TelemetryEnvelope(payloadJson);
                var finalPath = Path.Combine(
                    pendingDir,
                    $"{envelope.EnqueuedUtc.UtcDateTime.ToString(QueueFileTimestampFormat, CultureInfo.InvariantCulture)}_{envelope.Id}.json");
                // Write fully and close a uniquely owned temp file BEFORE attempting the atomic move.
                if (!TryWritePendingAtomically(finalPath, envelope.Serialize()))
                    return false;

                try { EnforceLimitOnDirectory(pendingDir, TelemetryConfig.MaxPendingItems); }
                catch { /* swallow */ }

                return true;
            }
            catch {
                // Must never affect caller
                return false;
            }
        }

        /// <summary>
        /// Attempts to claim up to maxItems for processing.
        /// Claimed items are atomically moved into processing.
        /// </summary>
        public IEnumerable<ClaimedItem> TryClaim(int maxItems) {
            var results = new List<ClaimedItem>();

            try {
                if (maxItems <= 0)
                    return results;

                // Recovery is repeated on every claim attempt so a claim that was young at
                // startup becomes eligible later without another restart or tracking call.
                RecoverStaleClaims();

                var candidatesExamined = 0;
                foreach (var file in EnumerateFilesOrderedByFilenameTimestamp(pendingDir)) {
                    if (results.Count >= maxItems || candidatesExamined++ >= TelemetryConfig.MaxPendingItems)
                        break;

                    var name = Path.GetFileName(file);
                    var claimedPath = Path.Combine(processingDir, CreateClaimedFileName(name));
                    FileStream? pendingClaimLock = null;
                    var ownsPendingClaimLock = false;

                    try {
                        if (!TryAcquirePendingClaimLock(file, out pendingClaimLock))
                            continue;
                        ownsPendingClaimLock = true;

                        File.Move(file, claimedPath);
                        try { File.SetLastWriteTimeUtc(claimedPath, TelemetryClock.UtcNow); }
                        catch { /* swallow */ }
                    }
                    catch {
                        continue; // another process claimed it
                    }
                    finally {
                        try { pendingClaimLock?.Dispose(); }
                        catch { /* swallow */ }
                        if (ownsPendingClaimLock)
                            SafeDelete(GetPendingClaimLockPath(file));
                    }

                    if (!TryCreateClaimLock(claimedPath)) {
                        TryMoveClaimBackToPending(claimedPath, file);
                        continue;
                    }

                    if (!TryReadBoundedText(claimedPath, MaxEnvelopeBytes, out var json)) {
                        SafeDelete(claimedPath);
                        SafeDelete(GetClaimLockPath(claimedPath));
                        continue;
                    }

                    TelemetryEnvelope envelope;
                    try {
                        envelope = TelemetryEnvelope.Deserialize(json);
                    }
                    catch {
                        SafeDelete(claimedPath);
                        SafeDelete(GetClaimLockPath(claimedPath));
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(envelope.PayloadJson) ||
                        Encoding.UTF8.GetByteCount(envelope.PayloadJson) > TelemetryConfig.MaxPayloadBytes) {
                        SafeDelete(claimedPath);
                        SafeDelete(GetClaimLockPath(claimedPath));
                        continue;
                    }

                    results.Add(new ClaimedItem(claimedPath, envelope));
                }
            }
            catch {
                // ignore
            }

            return results;
        }

        /// <summary>
        /// Permanently deletes a successfully delivered item.
        /// </summary>
        public void Complete(ClaimedItem item) {
            FileStream? claimLock = null;
            try {
                if (!TryAcquireClaimLock(item.Path, out claimLock))
                    return;

                SafeDelete(item.Path);
            }
            catch {
                // swallow
            }
            finally {
                try { claimLock?.Dispose(); }
                catch { /* swallow */ }

                if (!File.Exists(item.Path))
                    SafeDelete(GetClaimLockPath(item.Path));
            }
        }

        /// <summary>
        /// Returns a claim that was not started to pending without incrementing its attempts.
        /// </summary>
        public void Release(ClaimedItem item) {
            Requeue(item, incrementAttempts: false);
        }

        /// <summary>
        /// Returns a failed item back to pending.
        /// Ensures we only delete the processing item after the updated entry is safely persisted,
        /// or after we successfully moved it to dead-letter.
        /// </summary>
        public void Abandon(ClaimedItem item) {
            Requeue(item, incrementAttempts: true);
        }

        private void Requeue(ClaimedItem item, bool incrementAttempts) {
            FileStream? claimLock = null;
            var terminal = false;
            try {
                if (!TryAcquireClaimLock(item.Path, out claimLock))
                    return;

                var env = item.Envelope;

                // If max attempts reached, try to dead-letter; do not delete unless move succeeds.
                if (incrementAttempts && env.Attempts + 1 >= TelemetryConfig.MaxSendAttempts) {
                    terminal = MoveToDeadLetterBestEffort(item.Path);
                    return;
                }

                var updated = new TelemetryEnvelope(
                    env.Id,
                    env.PayloadJson,
                    env.EnqueuedUtc
                ) {
                    Attempts = incrementAttempts ? env.Attempts + 1 : env.Attempts
                };

                var target = Path.Combine(pendingDir, Path.GetFileName(item.Path));

                // Persist updated envelope into pending atomically-ish:
                // write temp in pending dir, then move into place.
                // Only after this succeeds do we delete the processing file.
                if (!DurableTelemetryQueue.TryWritePendingAtomically(target, updated.Serialize())) {
                    // Requeue failed; keep processing file so it can be retried later
                    // (CrashRecovery will move it back to pending on next start).
                    return;
                }

                // Requeue succeeded; now safe to delete the processing item.
                SafeDelete(item.Path);
                terminal = true;
            }
            catch {
                // swallow
            }
            finally {
                try { claimLock?.Dispose(); }
                catch { /* swallow */ }

                if (terminal || !File.Exists(item.Path))
                    SafeDelete(GetClaimLockPath(item.Path));
            }
        }

        /// <summary>
        /// Attempts to write a pending item using tmp + move semantics.
        /// Returns true only if the final file is known to exist with the written content.
        /// </summary>
        private static bool TryWritePendingAtomically(string target, string content) {
            string tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string ownershipPath = tmp + ".lock";

            try {
                // Keep ownership alive across the write/rename gap. This prevents another
                // process from treating a paused producer's temp file as orphaned.
                using (new FileStream(ownershipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) {
                    File.WriteAllText(tmp, content, Encoding.UTF8);

#if NET8_0_OR_GREATER
                    // On modern runtimes, overwrite is supported directly.
                    File.Move(tmp, target, overwrite: true);
                    return true;
#else
                    // netstandard2.0: do not replace an existing target after deleting it;
                    // preserving the existing pending copy is safer than risking data loss.
                    if (File.Exists(target))
                        return false;

                    File.Move(tmp, target);
                    return File.Exists(target);
#endif
                }
            }
            catch {
                SafeDelete(tmp);
                return false;
            }
            finally {
                SafeDelete(ownershipPath);
            }
        }

        private static bool TryReadBoundedText(string path, int maxBytes, out string text) {
            text = string.Empty;

            try {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > maxBytes)
                    return false;

                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var builder = new StringBuilder(Math.Min(maxBytes, 512));
                var buffer = new char[512];
                var totalChars = 0;

                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0) {
                    totalChars += read;
                    if (totalChars > maxBytes)
                        return false;

                    builder.Append(buffer, 0, read);
                }

                text = builder.ToString();
                return Encoding.UTF8.GetByteCount(text) <= maxBytes;
            }
            catch {
                return false;
            }
        }

        private static string CreateClaimedFileName(string pendingFileName) {
            return string.Concat(
                Path.GetFileNameWithoutExtension(pendingFileName),
                ".claim.",
                Guid.NewGuid().ToString("N"),
                ".json");
        }

        private static string GetClaimLockPath(string claimPath) {
            return claimPath + ClaimLockSuffix;
        }

        private static string GetPendingClaimLockPath(string pendingPath) {
            return pendingPath + PendingClaimLockSuffix;
        }

        private static bool TryAcquirePendingClaimLock(string pendingPath, out FileStream? claimLock) {
            claimLock = null;
            var lockPath = GetPendingClaimLockPath(pendingPath);

            for (var attempt = 0; attempt < 2; attempt++) {
                try {
                    claimLock = new FileStream(
                        lockPath,
                        FileMode.CreateNew,
                        FileAccess.ReadWrite,
                        FileShare.None);
                    return true;
                }
                catch {
                    // A marker left by a terminated claimant is recoverable once it is no
                    // longer held. An active claimant keeps the marker exclusively open.
                    try {
                        using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                        SafeDelete(lockPath);
                    }
                    catch {
                        return false;
                    }
                }
            }

            return false;
        }

        private static bool TryCreateClaimLock(string claimPath) {
            try {
                using var stream = new FileStream(
                    GetClaimLockPath(claimPath),
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None);
                return true;
            }
            catch {
                return false;
            }
        }

        private static bool TryAcquireClaimLock(string claimPath, out FileStream? claimLock) {
            claimLock = null;

            try {
                claimLock = new FileStream(
                    GetClaimLockPath(claimPath),
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
                return true;
            }
            catch {
                try { claimLock?.Dispose(); }
                catch { /* swallow */ }

                claimLock = null;
                return false;
            }
        }

        private static void TryMoveClaimBackToPending(string claimPath, string pendingPath) {
            try {
                if (File.Exists(claimPath) && !File.Exists(pendingPath))
                    File.Move(claimPath, pendingPath);
            }
            catch {
                // Leave the claim for stale recovery if the move cannot be completed.
            }
            finally {
                if (!File.Exists(claimPath))
                    SafeDelete(GetClaimLockPath(claimPath));
            }
        }

        /// <summary>
        /// Moves a processing item to dead-letter.
        /// Never deletes the processing file unless the move succeeded.
        /// </summary>
        private bool MoveToDeadLetterBestEffort(string processingPath) {
            try {
                var target = Path.Combine(deadLetterDir, Path.GetFileName(processingPath));
#if NET8_0_OR_GREATER
                File.Move(processingPath, target, overwrite: true);
                EnforceLimitOnDirectory(deadLetterDir, TelemetryConfig.MaxDeadLetterItems);
                return true;
#else
                // netstandard2.0: best-effort overwrite emulation
                try {
                    if (File.Exists(target))
                        File.Delete(target);

                    File.Move(processingPath, target);
                    EnforceLimitOnDirectory(deadLetterDir, TelemetryConfig.MaxDeadLetterItems);
                    return true;
                }
                catch {
                    // If we can't move to dead-letter, leave the processing file in place.
                    // (CrashRecovery will return it to pending on next start.)
                    return false;
                }
#endif
            }
            catch {
                // swallow
                return false;
            }
        }

        /// <summary>
        /// Deletes oldest items when size limit exceeded.
        /// </summary>
        private void EnforceLimit() {
            try {
                EnforceLimitOnDirectory(pendingDir, TelemetryConfig.MaxPendingItems);
                EnforceLimitOnDirectory(deadLetterDir, TelemetryConfig.MaxDeadLetterItems);
            }
            catch {
                // swallow
            }
        }

        private static void EnforceLimitOnDirectory(string dir, int maxQueueItems) {
            try {
                var files = EnumerateFilesOrderedByFilenameTimestamp(dir).ToList();

                var excess = files.Count - maxQueueItems;
                if (excess <= 0)
                    return;

                foreach (var file in files.Take(excess))
                    SafeDelete(file);
            }
            catch {
                // swallow
            }
        }

        private static IEnumerable<string> EnumerateFilesOrderedByFilenameTimestamp(string dir) {
            var files = new List<FileTimestampSortEntry>();

            try {
                foreach (var file in Directory.EnumerateFiles(dir, "*.json")) {
                    if (!TryParseTimestampFromFilename(file, out var timestampTicks, out var fileName))
                        continue;

                    files.Add(new FileTimestampSortEntry(file, fileName, timestampTicks));
                }
            }
            catch {
                return [];
            }

            files.Sort(static (a, b) => {
                var timestampComparison = a.TimestampTicks.CompareTo(b.TimestampTicks);
                if (timestampComparison != 0)
                    return timestampComparison;

                return StringComparer.Ordinal.Compare(a.FileName, b.FileName);
            });

            return files.Select(static x => x.Path);
        }

        private static bool TryParseTimestampFromFilename(string path, out long timestampTicks, out string fileName) {
            timestampTicks = default;
            fileName = Path.GetFileName(path);

            if (string.IsNullOrEmpty(fileName))
                return false;

            if (fileName.Length <= QueueFileTimestampLength || fileName[QueueFileTimestampLength] != '_')
                return false;

            var timestampPrefix = fileName.Substring(0, QueueFileTimestampLength);
            if (!DateTime.TryParseExact(
                timestampPrefix,
                QueueFileTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedUtc)) {
                return false;
            }

            timestampTicks = parsedUtc.Ticks;
            return true;
        }

        private readonly struct FileTimestampSortEntry {
            internal string Path { get; }
            internal string FileName { get; }
            internal long TimestampTicks { get; }

            internal FileTimestampSortEntry(string path, string fileName, long timestampTicks) {
                Path = path;
                FileName = fileName;
                TimestampTicks = timestampTicks;
            }
        }

        private static void SafeDelete(string path) {
            try {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch {
                // swallow
            }
        }

        private string ResolveQueueRoot() {
            try {
                runtimeContext.EnsureRootDirectoryResolvedOnWorkerThread();
            }
            catch {
                // swallow
            }

            return Path.Combine(runtimeContext.GetRootDirectory(), "telemetry.queue");
        }

        internal readonly struct ClaimedItem {
            internal string Path { get; }
            internal TelemetryEnvelope Envelope { get; }

            internal ClaimedItem(string path, TelemetryEnvelope envelope) {
                Path = path;
                Envelope = envelope;
            }
        }
    }
}

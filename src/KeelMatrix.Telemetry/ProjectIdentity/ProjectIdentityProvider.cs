// Copyright (c) KeelMatrix

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KeelMatrix.Telemetry.ProjectIdentity {
    /// <summary>
    /// Computes and caches stable anonymous telemetry identities for a telemetry client instance.
    /// All I/O and identity detection MUST run on the telemetry worker thread.
    /// </summary>
    internal sealed class ProjectIdentityProvider : IProjectIdentityProvider {
        private readonly MachineSaltProvider machineSaltProvider;
        private readonly IdentityFingerprintPipeline identityFingerprintPipeline;

        private int isResolved; // 0 = not resolved, 1 = resolved
        private ResolvedTelemetryIdentity? cachedIdentity;

        internal ProjectIdentityProvider(TelemetryRuntimeContext runtimeContext, RuntimeInfo runtimeInfo) {
            machineSaltProvider = new MachineSaltProvider(runtimeContext);
            identityFingerprintPipeline = new IdentityFingerprintPipeline(runtimeInfo);
        }

        /// <summary>
        /// Ensures the telemetry identities are resolved and cached.
        /// MUST be called only from the telemetry worker thread.
        /// </summary>
        public ResolvedTelemetryIdentity EnsureResolvedOnWorkerThread() {
            if (Volatile.Read(ref isResolved) == 1)
                return cachedIdentity ?? throw new InvalidOperationException("Telemetry identities were marked resolved but cache is empty.");

            var machineSaltBytes = machineSaltProvider.GetOrCreateMachineSaltBytes();
            var installationHash = ComputeInstallationHash(machineSaltBytes);

            string? projectHash = null;
            try {
                if (identityFingerprintPipeline.TryComputeStableProjectFingerprintBytes(out var identityFingerprintBytes))
                    projectHash = ComputeProjectHash(identityFingerprintBytes);
            }
            catch {
                projectHash = null;
            }

            var resolvedIdentity = new ResolvedTelemetryIdentity(projectHash, installationHash);
            cachedIdentity = resolvedIdentity;
            Volatile.Write(ref isResolved, 1);
            return resolvedIdentity;
        }

        private static byte[] Sha256(byte[] input) {
            using var sha = SHA256.Create();
            return sha.ComputeHash(input);
        }

        private static byte[] Concat(byte[] a, byte[] b) {
            var combined = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, combined, 0, a.Length);
            Buffer.BlockCopy(b, 0, combined, a.Length, b.Length);
            return combined;
        }

        private static string ComputeProjectHash(byte[] fingerprintBytes) {
            return ComputeHashHex("project.v1", fingerprintBytes);
        }

        private static string ComputeInstallationHash(byte[] machineSaltBytes) {
            return ComputeHashHex("installation.v1", machineSaltBytes);
        }

        private static string ComputeHashHex(string prefix, byte[] payloadBytes) {
            var prefixBytes = Encoding.UTF8.GetBytes(prefix);
            return ToLowerHex(Sha256(Concat(prefixBytes, payloadBytes)));
        }

        internal static string ToLowerHex(byte[] bytes) {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}

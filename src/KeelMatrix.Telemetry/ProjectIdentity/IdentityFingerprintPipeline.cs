// Copyright (c) KeelMatrix

namespace KeelMatrix.Telemetry.ProjectIdentity {
    internal sealed class IdentityFingerprintPipeline {
        private readonly CiGitIdentityFingerprint ciGitIdentityFingerprint;

        internal IdentityFingerprintPipeline(RuntimeInfo runtimeInfo) {
            ciGitIdentityFingerprint = new CiGitIdentityFingerprint(runtimeInfo);
        }

        /// <summary>
        /// Resolves a stable anonymous consuming-codebase fingerprint.
        /// The result must not include installation-local inputs such as machine salt.
        /// </summary>
        internal bool TryComputeStableProjectFingerprintBytes(out byte[] fingerprintBytes) {
            try {
                if (ciGitIdentityFingerprint.TryCompute(out fingerprintBytes))
                    return true;
            }
            catch { /* swallow */ }

            try {
                if (ProjectFileIdentityFingerprint.TryComputeIdentityFingerprintFromProjectFiles(out fingerprintBytes))
                    return true;
            }
            catch { /* swallow */ }

            fingerprintBytes = [];
            return false;
        }
    }
}

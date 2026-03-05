// Copyright (c) KeelMatrix

namespace KeelMatrix.Telemetry.ProjectIdentity {
    internal sealed class IdentityFingerprintPipeline {
        private readonly CiGitIdentityFingerprint ciGitIdentityFingerprint;

        internal IdentityFingerprintPipeline(RuntimeInfo runtimeInfo) {
            ciGitIdentityFingerprint = new CiGitIdentityFingerprint(runtimeInfo);
        }

        internal bool TryComputeIdentityFingerprintBytes(out byte[] fingerprintBytes) {
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

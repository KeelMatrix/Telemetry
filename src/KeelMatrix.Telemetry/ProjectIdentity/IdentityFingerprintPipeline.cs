// Copyright (c) KeelMatrix

namespace KeelMatrix.Telemetry.ProjectIdentity {
    internal static class IdentityFingerprintPipeline {
        internal static bool TryComputeIdentityFingerprintBytes(out byte[] fingerprintBytes) {
            try {
                if (CiGitIdentityFingerprint.TryCompute(out fingerprintBytes))
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

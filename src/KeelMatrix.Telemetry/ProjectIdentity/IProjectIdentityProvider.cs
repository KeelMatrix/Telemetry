// Copyright (c) KeelMatrix

namespace KeelMatrix.Telemetry.ProjectIdentity {
    internal interface IProjectIdentityProvider {
        ResolvedTelemetryIdentity EnsureResolvedOnWorkerThread();
    }
}

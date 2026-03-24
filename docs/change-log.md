# Telemetry Identity Model

## Schema

- `SchemaVersion = 1`
- Activation payload includes:
  - `project_hash`
  - `installation_hash`
  - runtime and process context fields already emitted by activation
- Heartbeat payload includes:
  - `project_hash`
  - `installation_hash`
  - weekly usage field already emitted by heartbeat

## Identity

- `project_hash` = consuming codebase identity
- `installation_hash` = installation identity
- `telemetry.salt` is persisted only to derive installation identity
- `telemetry.salt` does not affect `project_hash`

## Resolution Order

- Resolve `project_hash` from normalized repo identity first.
- Resolve `project_hash` from stable solution or project-file structure when repo identity is unavailable.
- Skip activation and heartbeat when no stable consuming codebase identity is available.
- Resolve `installation_hash` from the persisted salt for the installation root.

## Architecture

- `ProjectIdentityProvider` resolves and caches both identities on the worker thread.
- `IdentityFingerprintPipeline` resolves stable consuming-codebase fingerprints only.
- `CiGitIdentityFingerprint` uses one shared normalized repo fingerprint scheme for CI and local git remotes.
- `TelemetryDeliveryWorker` uses the resolved identities without doing identity work on caller threads.
- `TelemetryDispatcher` uses `project_hash` for idempotency state and emits both hashes in activation and heartbeat payloads.

## Current State

- The telemetry payload model is:
  - `project_hash` = consuming codebase identity
  - `installation_hash` = installation identity
- Activation and heartbeat share the same project identity resolution path.
- Local marker files remain keyed by `project_hash`.
- `telemetry.salt` is an installation-scoped input only.

## Validation

- `dotnet test tests\KeelMatrix.Telemetry.UnitTests\KeelMatrix.Telemetry.UnitTests.csproj -v minimal`
- `dotnet test tests\KeelMatrix.Telemetry.IntegrationTests\KeelMatrix.Telemetry.IntegrationTests.csproj -v minimal`
- `dotnet test tests\KeelMatrix.Telemetry.CiEnvironmentTests\KeelMatrix.Telemetry.CiEnvironmentTests.csproj -v minimal`
- `dotnet test KeelMatrix.Telemetry.slnx --configuration Release -v minimal`

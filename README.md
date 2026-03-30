# KeelMatrix.Telemetry

KeelMatrix.Telemetry is a small .NET package for anonymous activation and usage telemetry.

It is designed for libraries and developer tools that want a minimal built-in signal without turning telemetry into a product of its own. This package is a shared internal dependency used transitively by KeelMatrix libraries. It is open source for transparency, inspection, and auditing, but it is not intended to be installed or used directly as a standalone package.

The model is intentionally narrow:

- one anonymous **activation** event per project identity
- one anonymous **heartbeat** event per project identity per ISO week
- local durable queueing for best-effort delivery
- explicit opt-out support
- explicit repo-status inspection for consuming tools

This is **not** a general-purpose analytics SDK. It is a small telemetry building block with a very small API surface.

## Install

```bash
dotnet add package KeelMatrix.Telemetry
```

## Quick start

```csharp
using KeelMatrix.Telemetry;

var client = new Client("yourtool", typeof(YourEntryPointType));

client.TrackActivation();
client.TrackHeartbeat();
```

For explicit repo-status inspection in a consuming tool:

```csharp
using KeelMatrix.Telemetry;

if (RepositoryTelemetry.TryResolveRepositoryRoot(Environment.CurrentDirectory, out string repoRoot)) {
    RepositoryTelemetryStatus status = RepositoryTelemetry.GetEffectiveStatus(repoRoot);
}
```

## Design guarantees

- Normal `Client.TrackActivation()` / `Client.TrackHeartbeat()` paths are best-effort and avoid caller-thread repo-local file and network I/O
- `RepositoryTelemetry` is an explicit inspection API for consuming tools; it may synchronously inspect repo-local config files on the caller thread
- Failures are swallowed
- Telemetry must not affect application behavior
- Minimal payloads only
- Public API kept intentionally small

## What gets sent

At most two event types are sent:

1. **Activation**
2. **Heartbeat**

Payloads include tool metadata plus anonymous project and installation identifiers. They do **not** include source code, SQL text, file contents, file paths, usernames, hostnames, or client-side IP addresses.

For exact fields, storage details, retention, and opt-out behavior, see [PRIVACY.md](./PRIVACY.md).

## Opt out

Telemetry can be disabled by configuration.

For the current process:

### PowerShell

```powershell
$env:KEELMATRIX_NO_TELEMETRY="1"
```

### Bash

```bash
export KEELMATRIX_NO_TELEMETRY=1
```

Repo-local opt-out is also supported through `keelmatrix.telemetry.json`, `.env.local`, or `.env`.

See [PRIVACY.md](./PRIVACY.md) for precedence rules, supported keys, and behavior details.

## Intended use

KeelMatrix.Telemetry works best when you want all of the following:

- a tiny integration surface
- anonymous coarse-grained usage signals
- no caller-thread repo-local file or network I/O on normal emission paths
- an explicit repo-status inspection API for tooling that intentionally asks for it
- a simple privacy story you can explain in a few minutes

If you need dashboards, funnels, user profiles, feature flags, or arbitrary event streams, use a different telemetry system.

## Repository notes

- Target frameworks: `net8.0` and `netstandard2.0`
- SourceLink and symbols are enabled
- Public API analyzers are used to keep the surface stable

## Help and security

- Privacy details: [PRIVACY.md](./PRIVACY.md)
- Security reporting: [SECURITY.md](./SECURITY.md)
- Contributing: [CONTRIBUTING.md](./CONTRIBUTING.md)

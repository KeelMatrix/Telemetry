# KeelMatrix.Telemetry

`KeelMatrix.Telemetry` is the internal telemetry foundation used by KeelMatrix libraries.

It is published as open source for transparency and auditing, but it is not intended to be installed directly in application code. Most users should install the higher-level KeelMatrix package they actually want and let this package flow transitively.

## What it provides

- Minimal anonymous telemetry
- One-time activation tracking per project identity
- Weekly heartbeat tracking per project identity
- Best-effort, non-blocking delivery
- Explicit opt-out support
- Small public API surface

## Important

This package is infrastructure, not a standalone end-user product.

If you reached this page from NuGet or an IDE of your choice, you probably want the higher-level KeelMatrix package that depends on this one, not `KeelMatrix.Telemetry` directly.

## Public API

```csharp
var client = new KeelMatrix.Telemetry.Client("YourToolName", typeof(YourType));

client.TrackActivation();
client.TrackHeartbeat();
```

The API is intentionally small because this package exists to support other KeelMatrix packages, not to expose a broad telemetry framework.

## Behavior

- Calls are fire-and-forget
- Telemetry is designed to avoid blocking the calling thread
- Failures are swallowed
- Opt-out is supported through environment variables or repo-local configuration

## Documentation

- Repository: `https://github.com/KeelMatrix/Telemetry`
- Privacy details: see `PRIVACY.md` in the repository
- Source and implementation details: see the repository README

## Support

For bugs, questions, or review of telemetry behavior, open an issue in the GitHub repository.

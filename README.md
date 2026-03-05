# KeelMatrix.Telemetry

KeelMatrix.Telemetry is a minimal, privacy-focused telemetry library for .NET libraries and tools.

It is designed for library authors who want lightweight, anonymous activation and usage signals without collecting user data, blocking application code, or introducing external dependencies.

Telemetry is opt-out, best-effort, and non-blocking.

---

## Purpose

This package provides:

- Anonymous **activation** tracking (once per project identity)
- Anonymous **weekly heartbeat** tracking (once per project identity per ISO week)
- Local durable queueing with retry
- Strictly minimal payloads
- Explicit opt-out via environment variables

It is intended to be used transitively by higher-level KeelMatrix libraries.

---

## Installation

Add the NuGet package to your library project:

```bash
dotnet add package KeelMatrix.Telemetry
```

---

## Quickstart

Example usage inside your library:

```csharp
var client = new KeelMatrix.Telemetry.Client("YourToolName", typeof(YourCallingClass));

// Fire-and-forget (non-blocking)
client.TrackActivation();
client.TrackHeartbeat();
```

Design guarantees:

- No blocking I/O on the calling thread
- Failures are swallowed
- Telemetry must never affect application behavior

---

## Telemetry Model

Two event types are emitted:

1. Activation (once per project identity)
2. Heartbeat (once per project identity per ISO week)

All events include only these base fields:

- event
- tool
- toolVersion
- telemetryVersion
- schemaVersion
- projectHash (anonymous, non-reversible)

Activation also includes:

- runtime
- os
- ci flag
- timestamp

Heartbeat also includes:

- week

No user content, source code, SQL, file paths, usernames, hostnames, or IP addresses are collected.

---

## Telemetry (Opt-Out)

Telemetry is disabled when **any** of the following environment variables is set to a truthy value:

- KEELMATRIX_NO_TELEMETRY
- DOTNET_CLI_TELEMETRY_OPTOUT
- DO_NOT_TRACK

Accepted truthy values (case-insensitive where applicable):

- 1
- true
- yes
- y
- on

If disabled, NullTelemetryClient is used and no events are sent, including any locally queued backlog.

---

## Local Storage

To ensure reliability and non-blocking behavior, the library uses local filesystem storage under a per-user telemetry root directory:

- `telemetry.queue/`
  - `pending/`
  - `processing/`
  - `dead/`
- `markers/` (idempotency for activation and weekly heartbeat)
- `telemetry.salt` (machine salt for anonymous project hashing)

These files contain only minimal telemetry metadata and never include user content.

---

## Network Endpoint

Telemetry is sent over HTTPS to:

https://keelmatrix-nuget-telemetry.dz-bb6.workers.dev

Transmission is best-effort. Failures do not propagate to callers.

---

## Data Retention

Telemetry data is retained server-side for **90 days** and then automatically deleted.

---

## Security & Privacy

- Minimal, anonymous payloads
- No personal data collection
- Explicit opt-out
- Non-blocking design

For full privacy details, see [PRIVACY.md](./PRIVACY.md).

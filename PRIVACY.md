# Privacy Policy

This repository contains **KeelMatrix.Telemetry**, a minimal, privacy-preserving telemetry library for .NET libraries and tools.

Telemetry is **opt-out**. When enabled, it is designed to be **best-effort and non-blocking**: calls from your application must never do I/O or block the calling thread, and failures are swallowed.

---

## Summary

Telemetry may emit at most two event types:

1. **Activation** — at most once per project identity.
2. **Heartbeat** — at most once per project identity per ISO week.

Telemetry does **not** collect user content, SQL, file contents, file paths, machine names, usernames, or IP addresses.

---

## How to disable telemetry (opt-out)

Telemetry is disabled when **any** of the following environment variables is set to a truthy value:

- `KEELMATRIX_NO_TELEMETRY`
- `DOTNET_CLI_TELEMETRY_OPTOUT`
- `DO_NOT_TRACK`

Accepted truthy values (case-insensitive where applicable):

- `1`
- `true`
- `yes`
- `y`
- `on`

Notes:
- If telemetry is disabled, the library does not send any events, including any locally queued backlog.
- If you need a hard disable for a single process, do it at your host/library level (e.g., avoid constructing/using your telemetry client).

---

## What is sent

All payloads are small JSON documents and include only:

Common fields (all events):
- `event` — `"activation"` or `"heartbeat"`
- `tool` — the calling library/tool identifier (a lowercase name provided by the caller)
- `toolVersion` — the calling library/tool version
- `telemetryVersion` — the KeelMatrix.Telemetry version
- `schemaVersion` — currently `1`
- `projectHash` — a stable, anonymous hash derived from local project identity (not reversible)

Activation-only:
- `runtime` — runtime identifier (e.g., ".NET 8.0" normalized)
- `os` — `"windows"`, `"linux"`, `"osx"`, or `"unknown"`
- `ci` — boolean indicating whether a CI environment is detected
- `timestamp` — UTC timestamp

Heartbeat-only:
- `week` — ISO week string (e.g., `2026-W09`)

---

## What is NOT sent

The library is intentionally limited. It does not send:

- Source code, SQL text, queries, or user content
- File paths, file contents, or directory listings
- Hostnames, usernames, machine identifiers, or MAC addresses
- IP addresses (client-side) or any attempt to fingerprint users

---

## Local storage on your machine

To be crash-safe and non-blocking, the library uses local filesystem storage under a per-user telemetry root directory:

- A durable queue directory: `telemetry.queue/` with subfolders:
  - `pending/`
  - `processing/`
  - `dead/` (dead-letter)
- Marker files directory: `markers/` used for idempotency (activation/weekly heartbeat)
- A machine salt file: `telemetry.salt` used only to stabilize the anonymous project identity hash

These files contain only minimal telemetry queue/marker data and do not include user content.

---

## Network endpoint

Telemetry is sent over HTTPS to:

`https://keelmatrix-nuget-telemetry.dz-bb6.workers.dev`

Payloads are size-limited and transmission failures are swallowed; telemetry must never affect your application behavior.

---

## Server-side retention

Telemetry data is retained for **90 days** and then **automatically deleted**.

---

## Changes to this policy

If telemetry behavior changes in a way that affects privacy, this document will be updated in the repository.

---

## Contact

Questions or concerns: open a GitHub issue in this repository.

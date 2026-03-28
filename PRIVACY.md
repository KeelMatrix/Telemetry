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

Telemetry is disabled when the highest-precedence configured source says so.

Precedence:

1. Process environment variables
2. `keelmatrix.telemetry.json`
3. `.env.local`
4. `.env`

Accepted truthy values (case-insensitive where applicable):

- `1`
- `true`
- `yes`
- `y`
- `on`

### Current process only

PowerShell:

```powershell
$env:KEELMATRIX_NO_TELEMETRY="1"
```

Supported process environment variables:

- `KEELMATRIX_NO_TELEMETRY`
- `DOTNET_CLI_TELEMETRY_OPTOUT`
- `DO_NOT_TRACK`

If any of those variables is set for the current process, that process-level value takes priority over repo-local files.

### Current repo only

Repo-local opt-out is intended for package authors working in their own repo, local tests, local CLI runs, and repo CI jobs after checkout.

Example `keelmatrix.telemetry.json`:

```json
{
  "disabled": true
}
```

Example `.env`:

```dotenv
KEELMATRIX_NO_TELEMETRY=1
```

Example `.env.local`:

```dotenv
KEELMATRIX_NO_TELEMETRY=1
```

Repo-local `.env` and `.env.local` recognize the same opt-out keys:

- `KEELMATRIX_NO_TELEMETRY`
- `DOTNET_CLI_TELEMETRY_OPTOUT`
- `DO_NOT_TRACK`

Repo-local evaluation checks all candidate repo roots discovered from the runtime starting points, deduplicates them, and disables telemetry if any repo root explicitly opts out. A repo root with no supported opt-out file is ignored.

`.env` and `.env.local` are convenience files. Only simple `KEY=VALUE` lines are supported, with an optional case-insensitive `export ` prefix.

### Global machine or CI environment

Use normal environment-variable configuration for machine-wide or CI-wide opt-out.

Notes:
- If telemetry is disabled, the library does not send any events, including any locally queued backlog.
- If you need a hard disable for a single process, do it at your host/library level (e.g., avoid constructing/using your telemetry client).

---

## What is sent

All payloads are small JSON documents and include only:

Common fields (all events):
- `event` — `"activation"` or `"heartbeat"`
- `tool` — the calling library/tool identifier (a lowercase name provided by the caller)
- `tool_version` — the calling library/tool version
- `telemetry_version` — the KeelMatrix.Telemetry version
- `schema_version` — currently `1`
- `project_hash` — consuming codebase identity (stable, anonymous, not reversible)
- `installation_hash` — installation identity (stable, anonymous, not reversible)

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
- A persisted salt file: `telemetry.salt` used only to derive installation identity

These files contain only minimal telemetry queue/marker data and do not include user content.

---

## Network endpoint

Telemetry is sent over HTTPS to:

`https://telemetry.keelmatrix.com`

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

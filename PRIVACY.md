# Privacy Policy

This document describes the telemetry behavior of **KeelMatrix.Telemetry**.

## Summary

KeelMatrix.Telemetry sends at most two anonymous event types:

1. **Activation** — at most once per project identity
2. **Heartbeat** — at most once per project identity per ISO week

Telemetry is **opt-out**.

The package is designed to be best-effort and non-blocking from normal call sites. Telemetry failures are swallowed and must not affect application behavior.

The explicit `RepositoryTelemetry` inspection API is different: it is intended for consuming tools that deliberately inspect repo-local telemetry status, and it may synchronously inspect repo-local config files on the caller thread. It does not emit telemetry.

## What telemetry is for

Telemetry is used to measure coarse-grained package usage: whether a tool or library was activated, and whether it was used again in a later week.

It is not used for content inspection, behavioral profiling, advertising, or cross-product user tracking.

## What is sent

Every payload is a small JSON document.

Common fields:

- `event` — `activation` or `heartbeat`
- `tool` — tool or package identifier provided by the caller
- `tool_version` — version of the calling assembly
- `telemetry_version` — version of KeelMatrix.Telemetry
- `schema_version` — currently `1`
- `project_hash` — stable anonymous consuming-codebase identifier
- `installation_hash` — stable anonymous installation identifier

Activation also includes:

- `runtime`
- `os`
- `ci`
- `timestamp`

Heartbeat also includes:

- `week`

## What is not sent

KeelMatrix.Telemetry does **not** send:

- source code
- SQL text or queries
- file paths
- file contents
- usernames
- hostnames
- machine identifiers or MAC addresses
- client-side IP addresses
- arbitrary custom events

The identifiers used by the package are anonymous hashes, not raw user or machine identifiers.

## Opt-out

Telemetry is disabled when the highest-precedence configured source says so.

Precedence:

1. Process environment variables
2. `keelmatrix.telemetry.json`
3. `.env.local`
4. `.env`

Accepted truthy values:

- `1`
- `true`
- `yes`
- `y`
- `on`

Supported environment variable keys:

- `KEELMATRIX_NO_TELEMETRY`
- `DOTNET_CLI_TELEMETRY_OPTOUT`
- `DO_NOT_TRACK`

### Disable for the current process

PowerShell:

```powershell
$env:KEELMATRIX_NO_TELEMETRY="1"
```

Bash:

```bash
export KEELMATRIX_NO_TELEMETRY=1
```

Process-level opt-out is checked during client construction.

### Disable for the current repository

You can disable telemetry for a repository with any of the following files:

`keelmatrix.telemetry.json`

```json
{
  "disabled": true
}
```

`.env.local`

```dotenv
KEELMATRIX_NO_TELEMETRY=1
```

`.env`

```dotenv
KEELMATRIX_NO_TELEMETRY=1
```

Notes:

- `.env` and `.env.local` support simple `KEY=VALUE` lines
- an optional case-insensitive `export ` prefix is supported
- when a Git root exists, it is preferred over higher nested non-Git repo markers such as `global.json` or `Directory.Build.props`
- for normal telemetry emission, repo-local file lookup is resolved on the worker thread, not on the caller thread
- explicit `RepositoryTelemetry` inspection evaluates the same precedence on the caller thread when a consuming tool explicitly asks for repo status

If telemetry is disabled, the package does not send events, including any locally queued backlog.

## Local storage

To support best-effort delivery and crash recovery, the package uses local per-user storage under a telemetry root directory.

It may create:

- `telemetry.queue/`
  - `pending/`
  - `processing/`
  - `dead/`
- `markers/`
- `telemetry.salt`

These files contain only minimal queue, marker, and identity data required for delivery and idempotency. They do not contain user content.

## Network endpoint

Telemetry is sent over HTTPS to:

`https://telemetry.keelmatrix.com`

Payloads are size-limited. Transmission is best-effort.

## Server-side retention

Telemetry data is retained server-side for **90 days** and then automatically deleted.

## Changes to this document

If telemetry behavior changes in a way that materially affects privacy, this file will be updated.

## Contact

For questions or concerns, open an issue in the repository.

# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [0.1.1] - 2026-09-23

### Changed

- Aligned client validation and emitted payload contracts with the Worker contract, including bounded canonical tool names, fixed-length lowercase hashes, and valid ISO weeks. Worker ingestion hardening was deployed separately and is not included in this NuGet package.
- Normalized supported CI and Git repository identity formats, including GitHub Actions, GitLab, Azure DevOps, Bitbucket, HTTPS/SSH remotes, and validated Git root-commit fallback.
- Propagated the package version `0.1.1` to assembly and file metadata as `0.1.1.0`.

### Fixed

- Bounded canonical durable-queue claim filenames so repeated recovery and reclaim cycles do not grow claim suffixes without bound.
- Enforced a finite queue-write recovery budget; after exhaustion, further enqueue I/O is suppressed until a later explicit telemetry request resets the in-process retry budget.
- Made enqueue acceptance explicit: activation and heartbeat suppression markers are committed only after durable enqueue, and failed enqueues do not suppress unsent events.
- Added crash recovery and cross-process queue ownership handling, with bounded pending/dead-letter retention and leased processing claims.
- Kept process exit non-blocking while ensuring orderly worker teardown and bounded wakeup signaling, retries, and backoff.
- Rechecked process and repository opt-out before event publication and during queue draining, returning unstarted claims without consuming failure budget when opt-out changes.
- Serialized first-run installation-salt publication and corrupt-salt recovery so concurrent processes preserve a valid persisted winner and unsafe persistence disables telemetry for the process.

## [0.1.0] - 2026-08-11

- First public NuGet.org release.

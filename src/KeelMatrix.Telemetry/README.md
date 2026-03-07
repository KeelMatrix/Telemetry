# KeelMatrix.Telemetry

`KeelMatrix.Telemetry` is the shared telemetry infrastructure package used by KeelMatrix libraries.

It is primarily intended to be consumed transitively through other KeelMatrix packages rather than referenced directly. It is not intended to be a standalone general-purpose telemetry solution.

## Intended Use

- Internal shared dependency for KeelMatrix libraries
- Privacy-first activation and usage telemetry
- Best-effort, non-blocking background delivery

## Notes

- This package is open source and available for anyone to inspect or use.
- The public API surface is intentionally small.
- Most consumers should install the higher-level KeelMatrix package they actually need, not this package directly.
- For implementation details and privacy notes, see the repository README and `PRIVACY.md`.

Source: https://github.com/KeelMatrix/Telemetry

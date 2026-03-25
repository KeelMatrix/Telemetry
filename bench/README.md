# Benchmarks

This folder contains offline performance benchmarks for `KeelMatrix.Telemetry`.

The benchmark suite is intentionally isolated from production delivery:

- no benchmark sends real HTTPS requests
- no benchmark uses the production telemetry endpoint
- filesystem benchmarks use benchmark-owned temp directories only

## How to Run

Run the PowerShell script from the repo root:

```powershell
pwsh -NoProfile -File bench/Run-Benchmarks.ps1
```

This will:

- discover all `*.Benchmarks.csproj` under `bench/`
- build and run them in `Release` for `.NET 8`
- export CSV / JSON / Markdown results into `artifacts/benchmarks/<timestamp>/`

## Common Use Cases

- run the fast offline subset used in CI:

  ```powershell
  pwsh -NoProfile -File bench/Run-Benchmarks.ps1 `
    -Job Short `
    -CoolDownSec 0 `
    -Filter "*ClientSignalBench*" ,"*TelemetrySerializerBench*" ,"*TelemetryDispatcherBench*"
  ```

- run only durable queue benchmarks:

  ```powershell
  pwsh -NoProfile -File bench/Run-Benchmarks.ps1 -Job Short -CoolDownSec 0 -Filter "*DurableTelemetryQueueBench*"
  ```

- run only synthetic project identity benchmarks:

  ```powershell
  pwsh -NoProfile -File bench/Run-Benchmarks.ps1 -Job Short -CoolDownSec 0 -Filter "*ProjectIdentityBench*"
  ```

## Notes

- CI runs only the fast deterministic subset.
- Local scratch output from BenchmarkDotNet is written under `BenchmarkDotNet.Artifacts/`.
- Timestamped exports under `artifacts/benchmarks/` are the intended results for review and CI artifacts.

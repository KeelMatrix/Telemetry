<# 
.SYNOPSIS
  Discover and run all BenchmarkDotNet projects under the ./bench folder, collect artifacts,
  and (optionally) fail on noisy results.

.DESCRIPTION
  - Finds all *.Benchmarks.csproj under the folder containing this script (./bench).
  - Runs `dotnet run -c Release -f <Framework>` in each project with BenchmarkDotNet args.
  - Exports CSV/JSON/Markdown into artifacts/benchmarks/<timestamp>/<ProjectName>/ at repo root.
  - Optionally checks relative StdDev% against a threshold and fails.
  - Supports one or more explicit benchmark filters for CI-friendly subsets.
#>

[CmdletBinding()]
param(
  [string[]]$Filter = @('*'),
  [ValidateSet('Default','Short','Medium','Long')]
  [string]$Job = 'Default',
  [string]$Framework = 'net8.0',
  [string]$ArtifactsRoot = 'artifacts/benchmarks',
  [double]$MaxStdevPct = 0,
  [switch]$Ci,
  [int]$CoolDownSec = 480,
  [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Heading($text){ Write-Host "=== $text ===" -ForegroundColor Cyan }
function New-Timestamp(){ Get-Date -Format 'yyyyMMdd-HHmmss' }

$ScriptDir = Split-Path -Parent $PSCommandPath
$RepoRoot  = Resolve-Path (Join-Path $ScriptDir '..')

Set-Location $RepoRoot

$BenchRoot = $ScriptDir
Write-Heading "Discovering benchmark projects under $BenchRoot"
$benchProjects = @(
  Get-ChildItem -Path $BenchRoot -Recurse -Filter *.Benchmarks.csproj -ErrorAction SilentlyContinue |
  Select-Object -ExpandProperty FullName
)

if(-not $benchProjects -or $benchProjects.Count -eq 0){
  Write-Warning "No *.Benchmarks.csproj found under $BenchRoot."
  exit 2
}

$stamp = New-Timestamp
$runRoot = Join-Path $RepoRoot (Join-Path $ArtifactsRoot $stamp)
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null

Write-Host "Artifacts path: $runRoot"

if($DryRun){
  Write-Heading "Dry-Run: would run these projects"
  $benchProjects | ForEach-Object { Write-Host " - $_" }
  exit 0
}

& dotnet --info | Out-Null

$filters = @(
  $Filter |
  ForEach-Object {
    if ([string]::IsNullOrWhiteSpace($_)) { return }

    foreach($part in ($_ -split ',')) {
      $normalized = $part.Trim().Trim('"').Trim("'")
      if(-not [string]::IsNullOrWhiteSpace($normalized)) {
        $normalized
      }
    }
  } |
  Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)
if(-not $filters -or $filters.Count -eq 0){
  $filters = @('*')
}

$total = 0
$failed = 0
$noiseBreaches = @()
$allCsv = @()

foreach($proj in $benchProjects){
  $projName = Split-Path $proj -LeafBase
  $projOut = Join-Path $runRoot $projName
  New-Item -ItemType Directory -Force -Path $projOut | Out-Null

  Write-Heading "Running $projName ($Framework, Job: $Job, Filter: $($filters -join ', '))"
  Push-Location (Split-Path $proj -Parent)

  $exporters = $Ci ? @('json','github','csv') : @('github','markdown','html','csv')
  $bdnArgs = @('--artifacts', "$projOut", '--join')

  $bdnArgs = $bdnArgs + @('--filter') + $filters

  $bdnArgs = $bdnArgs + @('--exporters') + $exporters

  if($Job -ne 'Default'){ $bdnArgs += @('--job', $Job) }

  $cmd = @(
    'run','--project',"$proj",
    '-c','Release',
    '-f',"$Framework",
    '--'
  ) + $bdnArgs

  Write-Host ("dotnet " + ($cmd -join ' '))
  & dotnet @cmd
  $exitCode = $LASTEXITCODE
  if ($exitCode -ne 0) {
    Write-Error "dotnet run failed ($exitCode) for $projName"
    $failed++
    Pop-Location
    continue
  }
  Pop-Location

  $csvs = Get-ChildItem -Path $projOut -Recurse -Filter *.csv -ErrorAction SilentlyContinue
  if($csvs){
    $allCsv += $csvs.FullName
  }
  $total++
}

Write-Heading "Post-processing results"

function TryParse-Number([string]$s){
  if([string]::IsNullOrWhiteSpace($s)){ return $null }
  $ns = $s.Trim() -replace '[^\d\.,-]'
  if($ns -match '^\d{1,3}(\.\d{3})+,\d+$'){ $ns = $ns -replace '\.','' -replace ',','.' }
  elseif($ns -match '^\d{1,3}(,\d{3})+\.\d+$'){ $ns = $ns -replace ',','' }
  [double]::TryParse($ns, [System.Globalization.CultureInfo]::InvariantCulture, [ref]([double]$out = 0)) | Out-Null
  if($?) { return $out } else { return $null }
}

$rows = @()
foreach($csv in $allCsv){
  try{
    $content = Get-Content $csv
    if(-not $content){ continue }
    $header = $content[0].Split(',')
    $iMethod   = [Array]::IndexOf($header,'Method')
    $iMean     = [Array]::IndexOf($header,'Mean')
    $iStdDev   = [Array]::IndexOf($header,'StdDev')
    $iAllocated= [Array]::IndexOf($header,'Allocated')
    for($i=1; $i -lt $content.Count; $i++){
      $cols = $content[$i].Split(',')
      if($cols.Count -lt 2){ continue }
      $method = if($iMethod -ge 0){ $cols[$iMethod] } else { '(unknown)' }
      $mean   = if($iMean   -ge 0){ TryParse-Number $cols[$iMean] } else { $null }
      $std    = if($iStdDev -ge 0){ TryParse-Number $cols[$iStdDev] } else { $null }
      $alloc  = if($iAllocated -ge 0){ $cols[$iAllocated] } else { $null }

      $pct = $null
      if($mean -and $mean -ne 0 -and $std -ne $null){
        $pct = [math]::Round(($std / $mean) * 100.0, 2)
      }
      $rows += [pscustomobject]@{
        Csv = $csv
        Method = $method
        Mean   = $mean
        StdDev = $std
        StdDevPct = $pct
        Allocated = $alloc
      }

      if($MaxStdevPct -gt 0 -and $pct -ne $null -and $pct -gt $MaxStdevPct){
        $noiseBreaches += [pscustomobject]@{
          Csv = $csv; Method = $method; StdDevPct = $pct; Limit = $MaxStdevPct
        }
      }
    }
  } catch {
    Write-Warning "Failed to parse CSV: $csv. $_"
  }
}

if($Ci){
  $md = New-Object System.Text.StringBuilder
  [void]$md.AppendLine("# Benchmark Summary ($stamp)")
  [void]$md.AppendLine()
  [void]$md.AppendLine("**Projects:** $total  |  **CSV Files:** $($allCsv.Count)")
  if($MaxStdevPct -gt 0){
    if($noiseBreaches.Count -gt 0){
      [void]$md.AppendLine()
      [void]$md.AppendLine("**Noise breaches (> $MaxStdevPct% StdDev):**")
      foreach($b in $noiseBreaches){
        [void]$md.AppendLine((" - `{0}` :: `{1}` → {2}%") -f (Split-Path $b.Csv -Leaf), $b.Method, $b.StdDevPct)
      }
    } else {
      [void]$md.AppendLine()
      [void]$md.AppendLine("No noise breaches (StdDev <= $MaxStdevPct%).")
    }
  }
  $summaryPath = Join-Path $runRoot "SUMMARY.md"
  $md.ToString() | Set-Content -Encoding UTF8 $summaryPath
  Write-Host ""
  Write-Host "CI Summary written to $summaryPath"
}

if ($CoolDownSec -gt 0 -and -not $Ci) {
  Write-Host "Cooling down for $CoolDownSec seconds (local run)."
  Start-Sleep -Seconds $CoolDownSec
} else {
  Write-Host "Skipping cooldown (CI or CoolDownSec=0)."
}

if($failed -gt 0){
  Write-Error "$failed project(s) failed to run."
  exit 3
}
if($noiseBreaches.Count -gt 0){
  Write-Error "$($noiseBreaches.Count) benchmark(s) exceeded StdDev% limit ($MaxStdevPct%)."
  exit 4
}

Write-Heading "Done"

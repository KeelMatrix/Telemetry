$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$packageDirectory = Join-Path ([IO.Path]::GetTempPath()) "keelmatrix-release-validation-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $packageDirectory | Out-Null

try {
    New-Item -ItemType File -Path (Join-Path $packageDirectory 'KeelMatrix.Telemetry.0.1.1.nupkg') | Out-Null
    New-Item -ItemType File -Path (Join-Path $packageDirectory 'KeelMatrix.Telemetry.0.1.1.snupkg') | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $packageDirectory 'unexpected') | Out-Null
    Set-Content -LiteralPath (Join-Path $packageDirectory 'unexpected/nested.txt') -Value 'unexpected'

    $output = & pwsh -NoProfile -File (Join-Path $repositoryRoot 'scripts/Validate-Release.ps1') `
        -Version '0.1.1' `
        -RepositoryRoot $repositoryRoot `
        -PackageDirectory $packageDirectory 2>&1 | Out-String

    if ($LASTEXITCODE -eq 0) {
        throw 'Validate-Release.ps1 accepted an unexpected nested package-directory entry.'
    }

    if ($output -notmatch 'Unexpected files or directories in the package directory') {
        throw "Validate-Release.ps1 failed for an unexpected reason: $output"
    }

    Write-Output 'Validate-Release nested-entry regression passed.'
}
finally {
    if (Test-Path -LiteralPath $packageDirectory) {
        Remove-Item -LiteralPath $packageDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

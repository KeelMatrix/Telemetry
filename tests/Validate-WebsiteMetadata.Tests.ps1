$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$validatorPath = Join-Path $repositoryRoot 'scripts/Validate-WebsiteMetadata.ps1'
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "keelmatrix-website-metadata-test-$([Guid]::NewGuid().ToString('N'))"))
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())

function Invoke-WebsiteMetadataValidator([string]$Root) {
    $output = Invoke-NestedPwsh -NoProfile -File $validatorPath -RepositoryRoot $Root 2>&1 | Out-String
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = $output
    }
}

function Assert-WebsiteMetadataFails([string]$ExpectedMessage) {
    $result = Invoke-WebsiteMetadataValidator $fixtureRoot
    if ($result.ExitCode -eq 0) {
        throw "Validate-WebsiteMetadata.ps1 accepted invalid fixture metadata: $ExpectedMessage"
    }

    if ($result.Output -notmatch [regex]::Escape($ExpectedMessage)) {
        throw "Validate-WebsiteMetadata.ps1 failed for an unexpected reason. Expected '$ExpectedMessage'; output: $($result.Output)"
    }
}

try {
    New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'src/Telemetry') -Force | Out-Null

    $projectPath = Join-Path $fixtureRoot 'src/Telemetry/Telemetry.csproj'
    $manifestPath = Join-Path $fixtureRoot 'keelmatrix.website.json'
    $projectTemplate = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <PackageId>KeelMatrix.Telemetry</PackageId>
    <Authors>KeelMatrix</Authors>
    <Description>Shared internal telemetry infrastructure.</Description>
    <PackageTags>telemetry;keelmatrix-internal-package;keelmatrix-primary</PackageTags>
    <PackageProjectUrl>https://github.com/KeelMatrix/Telemetry</PackageProjectUrl>
    <RepositoryUrl>https://github.com/KeelMatrix/Telemetry</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>
</Project>
'@
    $manifestTemplate = @'
{
  "schemaVersion": 1,
  "packages": {
    "KeelMatrix.Telemetry": {
      "visibility": "internal-package",
      "role": "primary"
    }
  }
}
'@

    Set-Content -LiteralPath $projectPath -Value $projectTemplate -Encoding utf8NoBOM
    Set-Content -LiteralPath $manifestPath -Value $manifestTemplate -Encoding utf8NoBOM

    $actualResult = Invoke-WebsiteMetadataValidator $repositoryRoot
    if ($actualResult.ExitCode -ne 0 -or $actualResult.Output -notmatch 'Website package metadata validated') {
        throw "Validate-WebsiteMetadata.ps1 rejected the repository metadata: $($actualResult.Output)"
    }

    $validResult = Invoke-WebsiteMetadataValidator $fixtureRoot
    if ($validResult.ExitCode -ne 0 -or $validResult.Output -notmatch 'Website package metadata validated') {
        throw "Validate-WebsiteMetadata.ps1 rejected a valid fixture: $($validResult.Output)"
    }

    Set-Content -LiteralPath $manifestPath -Value $manifestTemplate.Replace('"schemaVersion": 1', '"schemaVersion": 2') -Encoding utf8NoBOM
    Assert-WebsiteMetadataFails 'schemaVersion'

    Set-Content -LiteralPath $manifestPath -Value $manifestTemplate.Replace('"internal-package"', '"public-product"') -Encoding utf8NoBOM
    Assert-WebsiteMetadataFails 'visibility tags'

    Set-Content -LiteralPath $manifestPath -Value $manifestTemplate -Encoding utf8NoBOM
    Set-Content -LiteralPath $projectPath -Value $projectTemplate.Replace('keelmatrix-internal-package;keelmatrix-primary', 'KeelMatrix-internal-package;keelmatrix-primary') -Encoding utf8NoBOM
    Assert-WebsiteMetadataFails 'visibility tags'

    Set-Content -LiteralPath $projectPath -Value $projectTemplate.Replace('keelmatrix-internal-package;keelmatrix-primary', 'keelmatrix-internal-package;keelmatrix-public-product;keelmatrix-primary') -Encoding utf8NoBOM
    Assert-WebsiteMetadataFails 'visibility tags'

    Set-Content -LiteralPath $projectPath -Value $projectTemplate.Replace('keelmatrix-internal-package;keelmatrix-primary', 'keelmatrix-internal-package;keelmatrix-component') -Encoding utf8NoBOM
    Assert-WebsiteMetadataFails 'role tags'

    Set-Content -LiteralPath $projectPath -Value $projectTemplate.Replace('https://github.com/KeelMatrix/Telemetry</PackageProjectUrl>', 'https://keelmatrix.com</PackageProjectUrl>') -Encoding utf8NoBOM
    Assert-WebsiteMetadataFails 'PackageProjectUrl'

    Set-Content -LiteralPath $projectPath -Value $projectTemplate.Replace('https://github.com/KeelMatrix/Telemetry</RepositoryUrl>', 'https://keelmatrix.com</RepositoryUrl>') -Encoding utf8NoBOM
    Assert-WebsiteMetadataFails 'RepositoryUrl'

    Set-Content -LiteralPath $projectPath -Value $projectTemplate.Replace('</PropertyGroup>', "    <PackageType>DotnetTool</PackageType>`n  </PropertyGroup>") -Encoding utf8NoBOM
    Assert-WebsiteMetadataFails 'Dependency package'

    Set-Content -LiteralPath $projectPath -Value $projectTemplate -Encoding utf8NoBOM
    $staleManifest = @'
{
  "schemaVersion": 1,
  "packages": {
    "KeelMatrix.Stale": {
      "visibility": "internal-package",
      "role": "component"
    },
    "KeelMatrix.Telemetry": {
      "visibility": "internal-package",
      "role": "primary"
    }
  }
}
'@
    Set-Content -LiteralPath $manifestPath -Value $staleManifest -Encoding utf8NoBOM
    Assert-WebsiteMetadataFails 'KeelMatrix.Stale'

    Write-Output 'Validate-WebsiteMetadata regression checks passed.'
}
finally {
    if ($fixtureRoot.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and $fixtureRoot -ne $temporaryRoot -and (Test-Path -LiteralPath $fixtureRoot)) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}

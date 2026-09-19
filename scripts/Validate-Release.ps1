[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$Tag,

    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),

    [string]$PackageDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/packages'),

    [switch]$RequireFinalizedChangelog,

    [switch]$RunConsumerSmoke
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Fail([string]$Message) {
    throw "Release validation failed: $Message"
}

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        Fail $Message
    }
}

function Get-BigEndianUInt32([byte[]]$Bytes, [int]$Offset) {
    return ([uint32]$Bytes[$Offset] -shl 24) -bor
        ([uint32]$Bytes[$Offset + 1] -shl 16) -bor
        ([uint32]$Bytes[$Offset + 2] -shl 8) -bor
        [uint32]$Bytes[$Offset + 3]
}

function Get-PngInfo([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $signature = [byte[]](0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a)
    Assert-Condition ($bytes.Length -ge 24) "Icon '$Path' is shorter than a PNG header."
    Assert-Condition (@(Compare-Object -ReferenceObject $signature -DifferenceObject $bytes[0..7]).Count -eq 0) "Icon '$Path' is not a PNG."
    Assert-Condition ([Text.Encoding]::ASCII.GetString($bytes[12..15]) -eq 'IHDR') "Icon '$Path' has no PNG IHDR chunk."

    return [pscustomobject]@{
        Width = Get-BigEndianUInt32 $bytes 16
        Height = Get-BigEndianUInt32 $bytes 20
        Length = $bytes.Length
    }
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToUpperInvariant()
}

function Get-ZipEntryHash([IO.Compression.ZipArchiveEntry]$Entry) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = $Entry.Open()
    try {
        return (($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) -join '').ToUpperInvariant()
    }
    finally {
        $stream.Dispose()
        $sha.Dispose()
    }
}

function Read-ZipEntryText([IO.Compression.ZipArchiveEntry]$Entry) {
    $stream = $Entry.Open()
    $reader = [IO.StreamReader]::new($stream)
    try {
        return $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Invoke-Checked([string]$FilePath, [string[]]$Arguments, [string]$Description) {
    $output = (& $FilePath @Arguments 2>&1 | Out-String).Trim()
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        Fail "$Description failed with exit code $exitCode. Output: $output"
    }
    return $output
}

$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$packageRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path

Assert-Condition ($Version -match '^\d+\.\d+\.\d+$') "Version '$Version' is not a stable X.Y.Z version."
if ($Tag) {
    Assert-Condition ($Tag -eq "v$Version") "Tag '$Tag' does not match version '$Version'."
}

if ($RequireFinalizedChangelog) {
    $changelogPath = Join-Path $root 'CHANGELOG.md'
    Assert-Condition (Test-Path -LiteralPath $changelogPath -PathType Leaf) "CHANGELOG.md is missing."
    $changelog = [IO.File]::ReadAllText($changelogPath)
    $escapedVersion = [regex]::Escape($Version)
    $releaseHeader = [regex]::Match($changelog, "(?m)^## \[$escapedVersion\] - (?<date>\d{4}-\d{2}-\d{2})\s*$")
    Assert-Condition $releaseHeader.Success "CHANGELOG.md has no finalized '$Version' release entry."

    $releaseDate = [DateTime]::ParseExact(
        $releaseHeader.Groups['date'].Value,
        'yyyy-MM-dd',
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal)
    Assert-Condition ($releaseDate.Date -le [DateTime]::UtcNow.Date) "CHANGELOG.md release date cannot be in the future."

    $entryStart = $releaseHeader.Index + $releaseHeader.Length
    $remaining = $changelog.Substring($entryStart)
    $nextHeader = [regex]::Match($remaining, '(?m)^## \[')
    $entry = if ($nextHeader.Success) { $remaining.Substring(0, $nextHeader.Index) } else { $remaining }
    Assert-Condition ($entry -notmatch '(?i)\b(planned|not yet published|tbd)\b') "The '$Version' changelog entry still contains pre-release wording."
}

$expectedPackages = @(
    "KeelMatrix.Telemetry.$Version.nupkg",
    "KeelMatrix.Telemetry.$Version.snupkg"
)
$actualEntries = @(Get-ChildItem -LiteralPath $packageRoot -Force -Recurse)
$unexpectedEntries = @($actualEntries | ForEach-Object {
        [IO.Path]::GetRelativePath($packageRoot, $_.FullName).Replace('\', '/')
    } | Where-Object { $_ -notin $expectedPackages })
Assert-Condition ($unexpectedEntries.Count -eq 0) "Unexpected files or directories in the package directory: $($unexpectedEntries -join ', ')."
$actualPackages = @(Get-ChildItem -LiteralPath $packageRoot -File | Where-Object { $_.Name -like '*.nupkg' -or $_.Name -like '*.snupkg' } | Select-Object -ExpandProperty Name)
$unexpected = @($actualPackages | Where-Object { $_ -notin $expectedPackages })
$missing = @($expectedPackages | Where-Object { $_ -notin $actualPackages })
Assert-Condition ($unexpected.Count -eq 0) "Unexpected package artifacts: $($unexpected -join ', ')."
Assert-Condition ($missing.Count -eq 0) "Missing package artifacts: $($missing -join ', ')."
Assert-Condition ($actualPackages.Count -eq $expectedPackages.Count) "Expected exactly $($expectedPackages.Count) package artifacts, found $($actualPackages.Count)."

$rootIconPath = Join-Path $root 'icon.png'
$projectIconPath = Join-Path $root 'src/KeelMatrix.Telemetry/icon.png'
foreach ($iconPath in @($rootIconPath, $projectIconPath)) {
    Assert-Condition (Test-Path -LiteralPath $iconPath -PathType Leaf) "Required icon '$iconPath' is missing."
    $iconInfo = Get-PngInfo $iconPath
    Assert-Condition ($iconInfo.Width -eq 512 -and $iconInfo.Height -eq 512) "Required icon '$iconPath' must be 512x512; found $($iconInfo.Width)x$($iconInfo.Height)."
    Assert-Condition ($iconInfo.Length -le 200KB) "Required icon '$iconPath' exceeds the 200 KB limit."
}
Assert-Condition ((Get-Sha256 $rootIconPath) -eq (Get-Sha256 $projectIconPath)) 'The repository-root and project-local icons differ.'

Add-Type -AssemblyName System.IO.Compression.FileSystem
$packagePath = Join-Path $packageRoot "KeelMatrix.Telemetry.$Version.nupkg"
$symbolPath = Join-Path $packageRoot "KeelMatrix.Telemetry.$Version.snupkg"
$expectedAssemblyVersion = "$Version.0"
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "keelmatrix-release-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null

try {
    $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $entries = @($archive.Entries)
        $entryNames = @($entries | Select-Object -ExpandProperty FullName)
        Assert-Condition ($entryNames -contains 'README.md') 'The package does not contain README.md at its root.'
        Assert-Condition ($entryNames -contains 'icon.png') 'The package does not contain icon.png at its root.'
        Assert-Condition (@($entryNames | Where-Object { $_ -match '(?i)(^|/)(\.env(?:\..*)?|.*\.(pfx|snk|key|pem))$' }).Count -eq 0) 'The package contains a sensitive environment or key file.'

        $iconEntry = $entries | Where-Object FullName -eq 'icon.png' | Select-Object -First 1
        Assert-Condition ((Get-ZipEntryHash $iconEntry) -eq (Get-Sha256 $projectIconPath)) 'The packaged icon differs from the resolved project-local icon.'

        $nuspecEntry = $entries | Where-Object FullName -like '*.nuspec' | Select-Object -First 1
        Assert-Condition ($null -ne $nuspecEntry) 'The package has no nuspec.'
        $nuspec = [xml](Read-ZipEntryText $nuspecEntry)
        $metadata = $nuspec.SelectSingleNode("//*[local-name()='metadata']")
        Assert-Condition ($null -ne $metadata) 'The nuspec has no metadata element.'
        Assert-Condition ($metadata.SelectSingleNode("*[local-name()='id']").InnerText -eq 'KeelMatrix.Telemetry') 'The nuspec package ID is incorrect.'
        Assert-Condition ($metadata.SelectSingleNode("*[local-name()='version']").InnerText -eq $Version) 'The nuspec version does not match the release version.'
        Assert-Condition ($metadata.SelectSingleNode("*[local-name()='readme']").InnerText -eq 'README.md') 'The nuspec README metadata is incorrect.'
        Assert-Condition ($metadata.SelectSingleNode("*[local-name()='icon']").InnerText -eq 'icon.png') 'The nuspec icon metadata is incorrect.'
        $license = $metadata.SelectSingleNode("*[local-name()='license']")
        Assert-Condition ($null -ne $license -and $license.GetAttribute('type') -eq 'expression' -and $license.InnerText -eq 'MIT') 'The nuspec license metadata is incorrect.'
        $repository = $metadata.SelectSingleNode("*[local-name()='repository']")
        Assert-Condition ($null -ne $repository -and $repository.GetAttribute('url') -eq 'https://github.com/KeelMatrix/Telemetry') 'The nuspec repository metadata is incorrect.'

        foreach ($tfm in @('net8.0', 'netstandard2.0')) {
            $dllEntry = $entries | Where-Object FullName -eq "lib/$tfm/KeelMatrix.Telemetry.dll" | Select-Object -First 1
            Assert-Condition ($null -ne $dllEntry) "The package is missing the $tfm assembly."
            $dllPath = Join-Path $temporaryRoot "$tfm-KeelMatrix.Telemetry.dll"
            $dllStream = $dllEntry.Open()
            $dllFile = [IO.File]::Create($dllPath)
            try { $dllStream.CopyTo($dllFile) } finally { $dllFile.Dispose(); $dllStream.Dispose() }
            $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($dllPath).Version.ToString()
            $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($dllPath).FileVersion
            Assert-Condition ($assemblyVersion -eq $expectedAssemblyVersion) "$tfm assembly version is '$assemblyVersion', expected '$expectedAssemblyVersion'."
            Assert-Condition ($fileVersion -eq $expectedAssemblyVersion) "$tfm file version is '$fileVersion', expected '$expectedAssemblyVersion'."
        }
    }
    finally { $archive.Dispose() }

    $symbols = [IO.Compression.ZipFile]::OpenRead($symbolPath)
    try {
        $symbolNames = @($symbols.Entries | Select-Object -ExpandProperty FullName)
        foreach ($tfm in @('net8.0', 'netstandard2.0')) {
            Assert-Condition ($symbolNames -contains "lib/$tfm/KeelMatrix.Telemetry.pdb") "The symbol package is missing the $tfm PDB."
        }
    }
    finally { $symbols.Dispose() }

    if ($RunConsumerSmoke) {
        $consumerRoot = Join-Path $temporaryRoot 'consumer'
        $consumerPackages = Join-Path $temporaryRoot 'nuget-packages'
        $consumerProject = Join-Path $consumerRoot 'ReleaseConsumer.csproj'
        $consumerConfig = Join-Path $consumerRoot 'NuGet.config'
        New-Item -ItemType Directory -Path $consumerRoot | Out-Null
        $escapedPackageRoot = [Security.SecurityElement]::Escape($packageRoot)
        $escapedNugetSource = [Security.SecurityElement]::Escape('https://api.nuget.org/v3/index.json')
        [IO.File]::WriteAllText($consumerProject, @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="KeelMatrix.Telemetry" Version="$Version" />
  </ItemGroup>
</Project>
"@)
        [IO.File]::WriteAllText((Join-Path $consumerRoot 'Program.cs'), @"
using KeelMatrix.Telemetry;
Console.WriteLine(typeof(Client).Assembly.GetName().Version?.ToString());
"@)
        [IO.File]::WriteAllText($consumerConfig, @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$escapedPackageRoot" />
    <add key="nuget.org" value="$escapedNugetSource" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local">
      <package pattern="KeelMatrix.Telemetry" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@)

        $oldNugetPackages = $env:NUGET_PACKAGES
        try {
            $env:NUGET_PACKAGES = $consumerPackages
            Invoke-Checked 'dotnet' @('restore', $consumerProject, '--configfile', $consumerConfig, '--no-cache', '--force-evaluate') 'Consumer restore' | Out-Null
            Invoke-Checked 'dotnet' @('build', $consumerProject, '--configuration', 'Release', '--no-restore') 'Consumer build' | Out-Null
            $consumerOutput = Invoke-Checked 'dotnet' @('run', '--project', $consumerProject, '--configuration', 'Release', '--no-build', '--no-restore') 'Consumer smoke'
            Assert-Condition ($consumerOutput -match [regex]::Escape($expectedAssemblyVersion)) "Consumer smoke reported '$consumerOutput' instead of '$expectedAssemblyVersion'."
        }
        finally { $env:NUGET_PACKAGES = $oldNugetPackages }
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Output "Release validation passed for KeelMatrix.Telemetry $Version."

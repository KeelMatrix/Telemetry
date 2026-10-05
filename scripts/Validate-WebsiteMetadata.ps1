[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw "Website metadata validation failed: $Message"
    }
}

function Assert-JsonObjectProperties([System.Text.Json.JsonElement]$Element, [string[]]$ExpectedNames, [string]$Context) {
    Assert-Condition ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) "$Context must be a JSON object."

    $actualNames = [System.Collections.Generic.List[string]]::new()
    foreach ($property in $Element.EnumerateObject()) {
        $actualNames.Add($property.Name)
    }

    Assert-Condition ($actualNames.Count -eq $ExpectedNames.Count) "$Context has unexpected or duplicate properties."
    foreach ($expectedName in $ExpectedNames) {
        $matchingNames = 0
        foreach ($actualName in $actualNames) {
            if ([string]::Equals($actualName, $expectedName, [StringComparison]::Ordinal)) {
                $matchingNames++
            }
        }

        Assert-Condition ($matchingNames -eq 1) "$Context must contain exactly one '$expectedName' property."
    }
}

function Get-RequiredJsonProperty([System.Text.Json.JsonElement]$Element, [string]$Name, [string]$Context) {
    $matches = [System.Collections.Generic.List[System.Text.Json.JsonElement]]::new()
    foreach ($property in $Element.EnumerateObject()) {
        if ([string]::Equals($property.Name, $Name, [StringComparison]::Ordinal)) {
            $matches.Add($property.Value)
        }
    }

    Assert-Condition ($matches.Count -eq 1) "$Context must contain exactly one '$Name' property."
    return $matches[0]
}

function Get-EvaluatedProjectProperties([string]$ProjectPath) {
    $output = & dotnet msbuild $ProjectPath -getProperty:IsPackable,PackageId,Description,PackageTags,PackageProjectUrl,RepositoryUrl,RepositoryType,PackageType,Authors -nologo 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Website metadata validation failed: could not evaluate '$ProjectPath' with dotnet msbuild. $($output | Out-String)"
    }

    try {
        return (($output | Out-String) | ConvertFrom-Json -AsHashtable).Properties
    }
    catch {
        throw "Website metadata validation failed: could not parse MSBuild properties for '$ProjectPath'. $($_.Exception.Message)"
    }
}

$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$manifestPath = Join-Path $root 'keelmatrix.website.json'
Assert-Condition (Test-Path -LiteralPath $manifestPath -PathType Leaf) 'keelmatrix.website.json is missing from the repository root.'

$manifestText = Get-Content -LiteralPath $manifestPath -Raw
$manifestDocument = $null
try {
    $manifestDocument = [System.Text.Json.JsonDocument]::Parse($manifestText)
    $manifestRoot = $manifestDocument.RootElement
    Assert-JsonObjectProperties $manifestRoot @('schemaVersion', 'packages') 'The manifest root'

    $schemaVersion = Get-RequiredJsonProperty $manifestRoot 'schemaVersion' 'The manifest root'
    Assert-Condition ($schemaVersion.ValueKind -eq [System.Text.Json.JsonValueKind]::Number -and $schemaVersion.GetInt32() -eq 1) 'schemaVersion must be 1.'

    $manifestPackagesElement = Get-RequiredJsonProperty $manifestRoot 'packages' 'The manifest root'
    Assert-Condition ($manifestPackagesElement.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) 'packages must be a JSON object.'

    $manifestPackages = [System.Collections.Generic.List[object]]::new()
    foreach ($packageProperty in $manifestPackagesElement.EnumerateObject()) {
        Assert-JsonObjectProperties $packageProperty.Value @('visibility', 'role') "Package '$($packageProperty.Name)'"

        $visibilityElement = Get-RequiredJsonProperty $packageProperty.Value 'visibility' "Package '$($packageProperty.Name)'"
        $roleElement = Get-RequiredJsonProperty $packageProperty.Value 'role' "Package '$($packageProperty.Name)'"
        Assert-Condition ($visibilityElement.ValueKind -eq [System.Text.Json.JsonValueKind]::String) "Package '$($packageProperty.Name)' visibility must be a string."
        Assert-Condition ($roleElement.ValueKind -eq [System.Text.Json.JsonValueKind]::String) "Package '$($packageProperty.Name)' role must be a string."

        $visibility = $visibilityElement.GetString()
        $role = $roleElement.GetString()
        Assert-Condition ($visibility -ceq 'public-product' -or $visibility -ceq 'internal-package') "Package '$($packageProperty.Name)' has an unsupported visibility value."
        Assert-Condition ($role -ceq 'primary' -or $role -ceq 'component') "Package '$($packageProperty.Name)' has an unsupported role value."

        $manifestPackages.Add([pscustomobject]@{
            PackageId = $packageProperty.Name
            Visibility = $visibility
            Role = $role
        })
    }
}
catch {
    throw "Website metadata validation failed: invalid keelmatrix.website.json. $($_.Exception.Message)"
}
finally {
    if ($null -ne $manifestDocument) {
        $manifestDocument.Dispose()
    }
}

$projectFiles = @(Get-ChildItem -LiteralPath $root -Filter '*.csproj' -File -Recurse | Where-Object {
    $_.FullName -notmatch '[\\/](bin|obj|\.git)[\\/]'
})
$packablePackages = [System.Collections.Generic.List[object]]::new()
foreach ($projectFile in $projectFiles) {
    $properties = Get-EvaluatedProjectProperties $projectFile.FullName
    if ($properties.IsPackable -ceq 'true') {
        Assert-Condition (-not [string]::IsNullOrWhiteSpace($properties.PackageId)) "Packable project '$($projectFile.FullName)' has no evaluated PackageId."
        $packablePackages.Add([pscustomobject]@{
            PackageId = [string]$properties.PackageId
            Properties = $properties
            ProjectPath = $projectFile.FullName
        })
    }
}

Assert-Condition ($packablePackages.Count -gt 0) 'No packable package projects were found.'

foreach ($package in $packablePackages) {
    $manifestMatches = @($manifestPackages | Where-Object { [string]::Equals($_.PackageId, $package.PackageId, [StringComparison]::Ordinal) })
    Assert-Condition ($manifestMatches.Count -eq 1) "Packable package '$($package.PackageId)' must have exactly one manifest entry."

    $properties = $package.Properties
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($properties.Description)) "Package '$($package.PackageId)' must have a description."
    Assert-Condition ($properties.Authors -ceq 'KeelMatrix') "Package '$($package.PackageId)' authors must be KeelMatrix."
    Assert-Condition ($properties.PackageProjectUrl -ceq 'https://github.com/KeelMatrix/Telemetry') "Package '$($package.PackageId)' PackageProjectUrl must be the canonical Telemetry repository URL."
    Assert-Condition ($properties.RepositoryUrl -ceq 'https://github.com/KeelMatrix/Telemetry') "Package '$($package.PackageId)' RepositoryUrl must be the canonical Telemetry repository URL."
    Assert-Condition ($properties.RepositoryType -ceq 'git') "Package '$($package.PackageId)' RepositoryType must be git."
    Assert-Condition ([string]::IsNullOrWhiteSpace($properties.PackageType) -or $properties.PackageType -ceq 'Dependency') "Package '$($package.PackageId)' must remain a NuGet Dependency package."

    $tags = @(([string]$properties.PackageTags -split ';') | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $manifestPackage = $manifestMatches[0]
    $expectedVisibilityTag = if ($manifestPackage.Visibility -ceq 'public-product') { 'keelmatrix-public-product' } else { 'keelmatrix-internal-package' }
    $expectedRoleTag = if ($manifestPackage.Role -ceq 'primary') { 'keelmatrix-primary' } else { 'keelmatrix-component' }
    $visibilityTags = @($tags | Where-Object { @('keelmatrix-public-product', 'keelmatrix-internal-package') -ccontains $_ })
    $roleTags = @($tags | Where-Object { @('keelmatrix-primary', 'keelmatrix-component') -ccontains $_ })

    Assert-Condition ($visibilityTags.Count -eq 1 -and $visibilityTags[0] -ceq $expectedVisibilityTag) "Package '$($package.PackageId)' source visibility tags must contain exactly '$expectedVisibilityTag' and agree with the manifest."
    Assert-Condition ($roleTags.Count -eq 1 -and $roleTags[0] -ceq $expectedRoleTag) "Package '$($package.PackageId)' source role tags must contain exactly '$expectedRoleTag' and agree with the manifest."
}

foreach ($manifestPackage in $manifestPackages) {
    $projectMatches = @($packablePackages | Where-Object { [string]::Equals($_.PackageId, $manifestPackage.PackageId, [StringComparison]::Ordinal) })
    Assert-Condition ($projectMatches.Count -eq 1) "Manifest package '$($manifestPackage.PackageId)' must match exactly one packable project in this repository."
}

$publicPackages = @($manifestPackages | Where-Object { $_.Visibility -ceq 'public-product' })
if ($publicPackages.Count -gt 0) {
    $primaryPackages = @($publicPackages | Where-Object { $_.Role -ceq 'primary' })
    Assert-Condition ($primaryPackages.Count -eq 1) 'A public product family must have exactly one primary package.'
}

Write-Output 'Website package metadata validated.'

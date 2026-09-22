[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackageDirectory,
    [Parameter(Mandatory)]
    [string]$RepositoryCommit,
    [Parameter(Mandatory)]
    [string]$ReleaseTag,
    [Parameter(Mandatory)]
    [string]$ManifestPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packageVersion = '1.0.0'
$packageIds = @(
    'Katasec.Forge.Mcl.Parser',
    'Katasec.Forge.Mcl.Core',
    'Katasec.Forge.Mcl.ChatClients',
    'Katasec.Forge.Mcl.Scout',
    'Katasec.Forge.Mcl.MissionRegistry',
    'Katasec.Forge.Mcl.Serve',
    'Katasec.Forge.Docker'
)

if ([string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
    throw 'GITHUB_TOKEN is required to query and publish private packages.'
}

function Read-PackageMetadata {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$PackageId
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq "$PackageId.nuspec" } | Select-Object -First 1
        if ($null -eq $entry) { throw "Missing nuspec for $PackageId." }
        $reader = [IO.StreamReader]::new($entry.Open())
        try {
            [xml]$nuspec = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }

    $metadata = $nuspec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    $repository = $metadata.SelectSingleNode('./*[local-name()="repository"]')
    if ($metadata.id -ne $PackageId -or $metadata.version -ne $packageVersion) {
        throw "Unexpected package identity in $PackageId."
    }
    if ($null -eq $repository -or
        $repository.url -ne 'https://github.com/katasec/forge-mcl' -or
        $repository.commit -ne $RepositoryCommit) {
        throw "Unexpected repository metadata in $PackageId."
    }
}

function Get-PackageVersions {
    param([Parameter(Mandatory)] [string]$PackageId)

    $escapedId = [uri]::EscapeDataString($PackageId)
    $uri = "https://api.github.com/orgs/katasec/packages/nuget/$escapedId/versions?per_page=100"
    $headers = @{
        Accept = 'application/vnd.github+json'
        Authorization = "Bearer $env:GITHUB_TOKEN"
        'X-GitHub-Api-Version' = '2022-11-28'
    }
    try {
        return @(Invoke-RestMethod -Headers $headers -Uri $uri)
    }
    catch {
        $statusCode = $_.Exception.Response.StatusCode.value__
        if ($statusCode -eq 404) { return @() }
        throw
    }
}

function Assert-PackageVersionAbsent {
    param([Parameter(Mandatory)] [string]$PackageId)

    if (@(Get-PackageVersions -PackageId $PackageId | Where-Object { $_.name -eq $packageVersion }).Count -ne 0) {
        throw "Refusing to publish existing immutable package version $PackageId $packageVersion."
    }
}

function Assert-PublishedPrivatePackage {
    param([Parameter(Mandatory)] [string]$PackageId)

    $escapedId = [uri]::EscapeDataString($PackageId)
    $uri = "https://api.github.com/orgs/katasec/packages/nuget/$escapedId"
    $headers = @{
        Accept = 'application/vnd.github+json'
        Authorization = "Bearer $env:GITHUB_TOKEN"
        'X-GitHub-Api-Version' = '2022-11-28'
    }
    $package = Invoke-RestMethod -Headers $headers -Uri $uri
    if ($package.visibility -ne 'private' -or $package.repository.full_name -ne 'katasec/forge-mcl') {
        throw "Published package $PackageId is not private and linked to katasec/forge-mcl."
    }
    if (@(Get-PackageVersions -PackageId $PackageId | Where-Object { $_.name -eq $packageVersion }).Count -ne 1) {
        throw "Published package $PackageId does not have exactly one $packageVersion version."
    }
}

& "$PSScriptRoot/../verify-packages.ps1" -PackageDirectory $PackageDirectory -RepositoryCommit $RepositoryCommit

$manifestPackages = @()
foreach ($packageId in $packageIds) {
    $path = Join-Path $PackageDirectory "$packageId.$packageVersion.nupkg"
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing packed package: $packageId." }
    Read-PackageMetadata -Path $path -PackageId $packageId
    $manifestPackages += [ordered]@{
        id = $packageId
        version = $packageVersion
        file = [IO.Path]::GetFileName($path)
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
    }
}

$manifestDirectory = Split-Path -Parent $ManifestPath
New-Item -ItemType Directory -Force -Path $manifestDirectory | Out-Null
([ordered]@{
    schema = 1
    repository = 'katasec/forge-mcl'
    tag = $ReleaseTag
    commit = $RepositoryCommit
    packages = $manifestPackages
} | ConvertTo-Json -Depth 4) | Set-Content -Encoding utf8 -NoNewline -LiteralPath $ManifestPath

foreach ($packageId in $packageIds) {
    $path = Join-Path $PackageDirectory "$packageId.$packageVersion.nupkg"
    Read-PackageMetadata -Path $path -PackageId $packageId
    Assert-PackageVersionAbsent -PackageId $packageId
    dotnet nuget push $path --source github --api-key $env:GITHUB_TOKEN --force-english-output
    if ($LASTEXITCODE -ne 0) { throw "Package publication failed for $packageId." }
}

foreach ($packageId in $packageIds) {
    Assert-PublishedPrivatePackage -PackageId $packageId
}

Write-Host "PASS: published and validated private package set $ReleaseTag at $RepositoryCommit."

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PackageDirectory,
    [Parameter(Mandatory)] [string]$RepositoryCommit,
    [Parameter(Mandatory)] [string]$ReleaseTag,
    [Parameter(Mandatory)] [string]$TrainManifestPath,
    [Parameter(Mandatory)] [string]$ManifestPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/release-train.ps1"
$train = Get-ReleaseTrain -ManifestPath $TrainManifestPath
if ($ReleaseTag -ne $train.tag) { throw "Release tag $ReleaseTag does not match train $($train.tag)." }
if ([string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) { throw 'GITHUB_TOKEN is required to query and publish private packages.' }

function Read-PackageMetadata {
    param([Parameter(Mandatory)] [string]$Path, [Parameter(Mandatory)] $Package)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq "$($Package.id).nuspec" } | Select-Object -First 1
        if ($null -eq $entry) { throw "Missing nuspec for $($Package.id)." }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $archive.Dispose() }

    $metadata = $nuspec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    $repository = $metadata.SelectSingleNode('./*[local-name()="repository"]')
    if ($metadata.id -ne $Package.id -or $metadata.version -ne $Package.version) { throw "Unexpected package identity in $($Package.id)." }
    if ($null -eq $repository -or $repository.url -ne 'https://github.com/katasec/forge-mcl' -or $repository.commit -ne $RepositoryCommit) {
        throw "Unexpected repository metadata in $($Package.id)."
    }
    $actualDependencies = @{}
    foreach ($dependency in @($metadata.SelectNodes('.//*[local-name()="dependency"]'))) {
        if ($dependency.id -like 'Katasec.Forge.*') { $actualDependencies[$dependency.id] = $dependency.version }
    }
    $expectedDependencies = Get-ReleaseTrainDependencies -Train $train -PackageId $Package.id
    if ($actualDependencies.Count -ne $expectedDependencies.Count) { throw "Unexpected internal dependency count in $($Package.id)." }
    foreach ($dependencyId in $expectedDependencies.Keys) {
        if ($actualDependencies[$dependencyId] -ne $expectedDependencies[$dependencyId]) { throw "Unexpected internal dependency range in $($Package.id) for $dependencyId." }
    }
}

function Get-PackageVersions {
    param([Parameter(Mandatory)] [string]$PackageId)

    $uri = "https://api.github.com/orgs/katasec/packages/nuget/$([uri]::EscapeDataString($PackageId))/versions?per_page=100"
    $headers = @{ Accept = 'application/vnd.github+json'; Authorization = "Bearer $env:GITHUB_TOKEN"; 'X-GitHub-Api-Version' = '2022-11-28' }
    try { return @(Invoke-RestMethod -Headers $headers -Uri $uri) }
    catch {
        if ($_.Exception.Response.StatusCode.value__ -eq 404) { return @() }
        throw
    }
}

function Get-RemotePackageState {
    param([Parameter(Mandatory)] $Package)
    return @(Get-PackageVersions -PackageId $Package.id | Where-Object { $_.name -eq $Package.version })
}

function Assert-PublishedPrivatePackage {
    param([Parameter(Mandatory)] $Package)

    $uri = "https://api.github.com/orgs/katasec/packages/nuget/$([uri]::EscapeDataString($Package.id))"
    $headers = @{ Accept = 'application/vnd.github+json'; Authorization = "Bearer $env:GITHUB_TOKEN"; 'X-GitHub-Api-Version' = '2022-11-28' }
    $remotePackage = Invoke-RestMethod -Headers $headers -Uri $uri
    if ($remotePackage.visibility -ne 'private' -or $remotePackage.repository.full_name -ne 'katasec/forge-mcl') {
        throw "Published package $($Package.id) is not private and linked to katasec/forge-mcl."
    }
    if ((Get-RemotePackageState -Package $Package).Count -ne 1) { throw "Published package $($Package.id) does not have exactly one $($Package.version) version." }
}

function Get-PackagePath {
    param([Parameter(Mandatory)] $Package)
    return Join-Path $PackageDirectory "$($Package.id).$($Package.version).nupkg"
}

function New-ReleaseEvidence {
    param([Parameter(Mandatory)] [hashtable]$Hashes, [Parameter(Mandatory)] [string]$Source)

    $manifestDirectory = Split-Path -Parent $ManifestPath
    New-Item -ItemType Directory -Force -Path $manifestDirectory | Out-Null
    $packages = foreach ($package in $train.packages) {
        [ordered]@{ id = $package.id; version = $package.version; file = "$($package.id).$($package.version).nupkg"; sha256 = $Hashes[$package.id] }
    }
    ([ordered]@{
        schema = 1
        repository = 'katasec/forge-mcl'
        tag = $train.tag
        commit = $RepositoryCommit
        release_train_sha256 = $train._manifestSha256
        package_bytes_source = $Source
        packages = @($packages)
    } | ConvertTo-Json -Depth 5) | Set-Content -Encoding utf8 -NoNewline -LiteralPath $ManifestPath
}

function Verify-RemotePackageBytes {
    param([Parameter(Mandatory)] $Package)

    $temporaryPath = [IO.Path]::GetTempFileName()
    try {
        $uri = "https://nuget.pkg.github.com/katasec/download/$([uri]::EscapeDataString($Package.id))/$($Package.version)/$($Package.id).$($Package.version).nupkg"
        $headers = @{ Accept = 'application/octet-stream'; Authorization = "Bearer $env:GITHUB_TOKEN" }
        Invoke-WebRequest -Headers $headers -Uri $uri -OutFile $temporaryPath
        Read-PackageMetadata -Path $temporaryPath -Package $Package
        return (Get-FileHash -Algorithm SHA256 -LiteralPath $temporaryPath).Hash.ToLowerInvariant()
    }
    finally { Remove-Item -Force -LiteralPath $temporaryPath -ErrorAction SilentlyContinue }
}

& "$PSScriptRoot/../verify-packages.ps1" -PackageDirectory $PackageDirectory -RepositoryCommit $RepositoryCommit -TrainManifestPath $TrainManifestPath
$states = @($train.packages | ForEach-Object { (Get-RemotePackageState -Package $_).Count })
if ($states | Where-Object { $_ -gt 1 }) { throw 'A release-train package version is not uniquely addressable.' }
$allAbsent = @($states | Where-Object { $_ -eq 0 }).Count -eq $states.Count
$allPresent = @($states | Where-Object { $_ -eq 1 }).Count -eq $states.Count

if ($allAbsent) {
    $hashes = @{}
    foreach ($package in $train.packages) {
        $path = Get-PackagePath -Package $package
        if (-not (Test-Path -LiteralPath $path)) { throw "Missing packed package: $($package.id)." }
        Read-PackageMetadata -Path $path -Package $package
        if ((Get-RemotePackageState -Package $package).Count -ne 0) { throw "Refusing to publish existing immutable package version $($package.id) $($package.version)." }
        $hashes[$package.id] = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
        dotnet nuget push $path --source github --api-key $env:GITHUB_TOKEN --force-english-output
        if ($LASTEXITCODE -ne 0) { throw "Package publication failed for $($package.id)." }
    }
    foreach ($package in $train.packages) { Assert-PublishedPrivatePackage -Package $package }
    New-ReleaseEvidence -Hashes $hashes -Source 'release-workflow'
    Write-Host "PASS: published and validated private package train $($train.tag)."
    exit 0
}

if (-not $allPresent) { throw 'Release train is partially published. Do not delete, overwrite, or push a subset; create a new additive train.' }

$recoveredHashes = @{}
foreach ($package in $train.packages) {
    Assert-PublishedPrivatePackage -Package $package
    $recoveredHashes[$package.id] = Verify-RemotePackageBytes -Package $package
}
New-ReleaseEvidence -Hashes $recoveredHashes -Source 'github-packages-recovery'
Write-Host "PASS: recovered release evidence for already-complete train $($train.tag) without a package push."

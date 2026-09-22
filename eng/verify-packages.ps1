[CmdletBinding()]
param(
    [string]$PackageDirectory = "$PSScriptRoot/../artifacts/packages"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expected = [ordered]@{
    'Katasec.Forge.Mcl.Parser' = @{ Version = '1.0.0'; Dependencies = @{} }
    'Katasec.Forge.Mcl.Core' = @{ Version = '1.0.0'; Dependencies = @{ 'Katasec.Forge.Mcl.Parser' = '[1.0.0]' } }
    'Katasec.Forge.Mcl.ChatClients' = @{ Version = '1.0.0'; Dependencies = @{ 'Katasec.Forge.Mcl.Core' = '[1.0.0]' } }
    'Katasec.Forge.Mcl.Scout' = @{ Version = '1.0.0'; Dependencies = @{ 'Katasec.Forge.Mcl.Core' = '[1.0.0]' } }
    'Katasec.Forge.Mcl.MissionRegistry' = @{ Version = '1.0.0'; Dependencies = @{ 'Katasec.Forge.Mcl.Core' = '[1.0.0]' } }
    'Katasec.Forge.Mcl.Serve' = @{ Version = '1.0.0'; Dependencies = @{} }
    'Katasec.Forge.Docker' = @{ Version = '0.1.0'; Dependencies = @{} }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($packageId in $expected.Keys) {
    $package = Join-Path $PackageDirectory "$packageId.$($expected[$packageId].Version).nupkg"
    if (-not (Test-Path -LiteralPath $package)) {
        throw "Missing packed package: $packageId."
    }
    $archive = [System.IO.Compression.ZipFile]::OpenRead($package)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq "$packageId.nuspec" } | Select-Object -First 1
        if ($null -eq $entry) { throw "Missing nuspec for $packageId." }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $nuspec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        if ($metadata.id -ne $packageId -or $metadata.version -ne $expected[$packageId].Version) {
            throw "Unexpected package identity in $packageId."
        }
        $repository = $metadata.SelectSingleNode('./*[local-name()="repository"]')
        if ($null -eq $repository -or $repository.url -ne 'https://github.com/katasec/forge-mcl') {
            throw "Missing private-repository metadata in $packageId."
        }
        $actual = @{}
        foreach ($dependency in @($metadata.SelectNodes('.//*[local-name()="dependency"]'))) {
            if ($dependency.id -like 'Katasec.Forge.*') { $actual[$dependency.id] = $dependency.version }
        }
        if ($actual.Count -ne $expected[$packageId].Dependencies.Count) { throw "Unexpected internal dependency count in $packageId." }
        foreach ($dependency in $expected[$packageId].Dependencies.Keys) {
            if ($actual[$dependency] -ne $expected[$packageId].Dependencies[$dependency]) {
                throw "Unexpected internal dependency range in $packageId for $dependency."
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

Write-Host 'PASS: all private package identities and exact internal dependency ranges match policy.'

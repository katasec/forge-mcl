[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repoPrefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

foreach ($project in Get-ChildItem -Path $repoRoot -Recurse -Filter *.csproj -File) {
    [xml]$xml = Get-Content -Raw $project.FullName
    foreach ($reference in @($xml.SelectNodes('//*[local-name()="ProjectReference"]'))) {
        $include = [string]$reference.Include
        $resolved = [IO.Path]::GetFullPath((Join-Path $project.DirectoryName $include))
        if (-not $resolved.StartsWith($repoPrefix, [System.StringComparison]::Ordinal)) {
            throw "Cross-repository ProjectReference in $($project.FullName): $include"
        }
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "Missing ProjectReference target in $($project.FullName): $include"
        }
    }
}

Write-Host 'PASS: every ProjectReference resolves inside forge-mcl.'

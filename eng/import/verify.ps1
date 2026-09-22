[CmdletBinding()]
param(
    [string]$ManifestPath = "$PSScriptRoot/forge-mcl-v1.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$manifest = Get-Content -Raw $ManifestPath | ConvertFrom-Json
$utf8 = [System.Text.UTF8Encoding]::new($false)
$ordinal = [System.StringComparer]::Ordinal

function Get-Sha256([string]$Text) {
    $bytes = $utf8.GetBytes($Text)
    return ([System.Security.Cryptography.SHA256]::HashData($bytes) | ForEach-Object ToString x2) -join ''
}

function Get-ExpectedTargetPath([string]$SourcePath) {
    $testPrefix = 'src/ForgeMission.Tests/'
    if ($SourcePath.StartsWith($testPrefix, [System.StringComparison]::Ordinal)) {
        return 'tests/ForgeMission.Mcl.Tests/' + $SourcePath.Substring($testPrefix.Length)
    }

    return $SourcePath
}

if ($manifest.schemaVersion -ne 1 -or $manifest.sourceCommit -ne 'd2c0c121bbe4180b60ddd44fa5f18872e2402771') {
    throw 'The import manifest must identify schema 1 and the locked MCL source commit.'
}

$records = @($manifest.records)
if ($records.Count -ne 186 -or $manifest.recordCount -ne 186) {
    throw "Expected exactly 186 import records; found $($records.Count)."
}

$canonical = [System.Text.StringBuilder]::new()
$paths = [System.Text.StringBuilder]::new()
$previousPath = $null
$targets = [System.Collections.Generic.HashSet[string]]::new($ordinal)
$transformed = 0

foreach ($record in $records) {
    if ($previousPath -ne $null -and $ordinal.Compare($previousPath, [string]$record.sourcePath) -ge 0) {
        throw "Import records are not ordinal sourcePath-sorted at '$($record.sourcePath)'."
    }
    $previousPath = [string]$record.sourcePath

    if ([string]::IsNullOrWhiteSpace($record.mode) -or $record.type -ne 'blob' -or $record.blob -notmatch '^[0-9a-f]{40}$') {
        throw "Invalid git record for '$($record.sourcePath)'."
    }
    if ($record.targetPath -ne (Get-ExpectedTargetPath $record.sourcePath) -or -not $targets.Add([string]$record.targetPath)) {
        throw "Invalid or duplicate target path for '$($record.sourcePath)'."
    }
    if ($record.preserveBlob -eq $false -and [string]::IsNullOrWhiteSpace([string]$record.bootstrapTransform)) {
        throw "A transformed import record must name its bounded bootstrap transform: $($record.sourcePath)."
    }

    [void]$canonical.Append("$($record.mode) $($record.type) $($record.blob)")
    [void]$canonical.Append([char]9)
    [void]$canonical.Append("$($record.sourcePath)`n")
    [void]$paths.Append("$($record.sourcePath)`n")
}

if ((Get-Sha256 $canonical.ToString()) -ne $manifest.sourceRecordSha256 -or $manifest.sourceRecordSha256 -ne 'a883dd656a5eab6318f7dfec7f3ec77f76a4a366a5f83b4766b55733fdff9c03') {
    throw 'The canonical tab-split source-record SHA-256 does not match the locked import digest.'
}
if ((Get-Sha256 $paths.ToString()) -ne $manifest.pathListSha256 -or $manifest.pathListSha256 -ne '72447a494e3771b22f835e1ce34545e0dac3bfbe0791729de32e2efd6c4ef3e6') {
    throw 'The ordinal source-path SHA-256 does not match the locked import digest.'
}

foreach ($record in $records) {
    $target = Join-Path $repoRoot $record.targetPath
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
        throw "Imported target is missing: $($record.targetPath)."
    }
    if ($record.preserveBlob -ne $false) {
        $blob = (git -C $repoRoot hash-object -- $target).Trim()
        if ($blob -ne $record.blob) {
            throw "Imported target blob differs from source: $($record.targetPath)."
        }
    }
    else {
        $transformed++
    }
    $stage = @(git -C $repoRoot ls-files --stage -- $record.targetPath)
    if ($stage.Count -ne 1 -or $stage[0] -notmatch '^([0-7]{6})\s+') {
        throw "Imported target is not tracked exactly once: $($record.targetPath)."
    }
    if ($Matches[1] -ne $record.mode) {
        throw "Imported target mode differs from source: $($record.targetPath)."
    }
}

$prohibitedPrefixes = @(
    'missions/', 'src/ForgeMission.Application', 'src/ForgeMission.ClientRuntime', 'src/ForgeMission.Desktop',
    'src/ForgeMission.Orchestration', 'src/ForgeMission.Runner', 'src/ForgeMission.Rooms',
    'src/ForgeMission.Conversation', 'src/ForgeMission.Api', 'src/ForgeMission.Billing', 'src/ForgeUI'
)
foreach ($path in @(git -C $repoRoot ls-files)) {
    foreach ($prefix in $prohibitedPrefixes) {
        if ($path.StartsWith($prefix, [System.StringComparison]::Ordinal)) {
            throw "Prohibited product source is tracked in forge-mcl: $path."
        }
    }
}

Write-Host "PASS: 186 source records are locked to $($manifest.sourceCommit); $($records.Count - $transformed) preserved blobs match and $transformed bounded bootstrap transforms are declared."

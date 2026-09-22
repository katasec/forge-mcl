[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Tag,
    [string]$Remote = 'origin',
    [switch]$Checkout
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-GitOutput {
    param([Parameter(Mandatory)] [string[]]$Arguments)

    $output = @(& git @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "Git command failed: git $($Arguments -join ' ')" }
    return $output
}

if ($Tag -notmatch '^mcl-v\d+\.\d+\.\d+$') { throw "Unexpected release tag: $Tag" }

$validationRef = "refs/tags/release-validation/$Tag"
$mainRef = "refs/remotes/$Remote/main"
$null = Get-GitOutput -Arguments @(
    'fetch', '--no-tags', $Remote,
    "+refs/tags/${Tag}:${validationRef}",
    "+refs/heads/main:${mainRef}"
)

if ((Get-GitOutput -Arguments @('cat-file', '-t', $validationRef) | Select-Object -First 1).Trim() -ne 'tag') {
    throw "$Tag must be an annotated tag."
}

$commit = (Get-GitOutput -Arguments @('rev-parse', "${validationRef}^{}") | Select-Object -First 1).Trim()
$main = (Get-GitOutput -Arguments @('rev-parse', $mainRef) | Select-Object -First 1).Trim()
if ($commit -ne $main) { throw "$Tag must peel to the current $Remote/main commit." }

if ($Checkout) {
    $null = Get-GitOutput -Arguments @('checkout', '--detach', $commit)
    $head = (Get-GitOutput -Arguments @('rev-parse', 'HEAD') | Select-Object -First 1).Trim()
    if ($head -ne $commit) { throw "Release checkout does not match the peeled $Tag commit." }
    $dirty = Get-GitOutput -Arguments @('status', '--porcelain')
    if (-not [string]::IsNullOrWhiteSpace(($dirty -join "`n"))) { throw 'Release checkout is not clean.' }
}

[pscustomobject]@{ Tag = $Tag; Commit = $commit; ValidationRef = $validationRef }

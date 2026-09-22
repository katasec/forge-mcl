[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Git {
    param(
        [Parameter(Mandatory)] [string]$WorkingDirectory,
        [Parameter(Mandatory)] [string[]]$Arguments
    )

    Push-Location $WorkingDirectory
    try {
        $output = @(& git @Arguments)
        if ($LASTEXITCODE -ne 0) { throw "Git fixture command failed: git $($Arguments -join ' ')" }
        return $output
    }
    finally { Pop-Location }
}

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ("forge-mcl-release-tag-" + [guid]::NewGuid().ToString('N'))
$origin = Join-Path $fixtureRoot 'origin.git'
$seed = Join-Path $fixtureRoot 'seed'
$worktree = Join-Path $fixtureRoot 'worktree'
$resolver = Join-Path $PSScriptRoot 'resolve-release-tag.ps1'

try {
    New-Item -ItemType Directory -Force -Path $fixtureRoot, $seed | Out-Null
    Invoke-Git -WorkingDirectory $fixtureRoot -Arguments @('init', '--bare', $origin) | Out-Null
    Invoke-Git -WorkingDirectory $seed -Arguments @('init', '-b', 'main') | Out-Null
    Invoke-Git -WorkingDirectory $seed -Arguments @('config', 'user.email', 'release-fixture@example.invalid') | Out-Null
    Invoke-Git -WorkingDirectory $seed -Arguments @('config', 'user.name', 'Release fixture') | Out-Null
    Invoke-Git -WorkingDirectory $seed -Arguments @('commit', '--allow-empty', '-m', 'seed') | Out-Null
    $commit = (Invoke-Git -WorkingDirectory $seed -Arguments @('rev-parse', 'HEAD') | Select-Object -First 1).Trim()
    Invoke-Git -WorkingDirectory $seed -Arguments @('remote', 'add', 'origin', $origin) | Out-Null
    Invoke-Git -WorkingDirectory $seed -Arguments @('push', 'origin', 'main') | Out-Null
    Invoke-Git -WorkingDirectory $seed -Arguments @('tag', '-a', 'mcl-v1.0.1', '-m', 'annotated fixture') | Out-Null
    Invoke-Git -WorkingDirectory $seed -Arguments @('push', 'origin', 'refs/tags/mcl-v1.0.1') | Out-Null
    Invoke-Git -WorkingDirectory $seed -Arguments @('tag', 'mcl-v1.0.2') | Out-Null
    Invoke-Git -WorkingDirectory $seed -Arguments @('push', 'origin', 'refs/tags/mcl-v1.0.2') | Out-Null

    Invoke-Git -WorkingDirectory $fixtureRoot -Arguments @('clone', '--no-tags', '--branch', 'main', $origin, $worktree) | Out-Null
    Invoke-Git -WorkingDirectory $worktree -Arguments @('update-ref', 'refs/tags/mcl-v1.0.1', $commit) | Out-Null
    $canonicalBefore = (Invoke-Git -WorkingDirectory $worktree -Arguments @('rev-parse', 'refs/tags/mcl-v1.0.1') | Select-Object -First 1).Trim()

    Push-Location $worktree
    try { $release = & $resolver -Tag 'mcl-v1.0.1' -Checkout }
    finally { Pop-Location }
    if ($release.Commit -ne $commit) { throw 'Annotated fixture did not resolve to the remote peeled commit.' }
    $validationType = (Invoke-Git -WorkingDirectory $worktree -Arguments @('cat-file', '-t', $release.ValidationRef) | Select-Object -First 1).Trim()
    if ($validationType -ne 'tag') { throw 'Annotated fixture validation ref is not an annotated tag.' }
    $canonicalAfter = (Invoke-Git -WorkingDirectory $worktree -Arguments @('rev-parse', 'refs/tags/mcl-v1.0.1') | Select-Object -First 1).Trim()
    if ($canonicalAfter -ne $canonicalBefore) { throw 'Resolver changed the checkout-like canonical tag ref.' }
    $head = (Invoke-Git -WorkingDirectory $worktree -Arguments @('rev-parse', 'HEAD') | Select-Object -First 1).Trim()
    if ($head -ne $commit) { throw 'Resolver did not check out the remote peeled commit.' }

    $lightweightRejected = $false
    Push-Location $worktree
    try { $null = & $resolver -Tag 'mcl-v1.0.2' -Checkout }
    catch {
        if ($_.Exception.Message -match 'must be an annotated tag') { $lightweightRejected = $true } else { throw }
    }
    finally { Pop-Location }
    if (-not $lightweightRejected) { throw 'Lightweight tag fixture was not rejected.' }

    Write-Host 'PASS: release tag resolver ignores checkout-like local tag state and rejects lightweight tags.'
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -Recurse -Force -LiteralPath $fixtureRoot }
}

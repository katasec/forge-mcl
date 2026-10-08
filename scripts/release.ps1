# Reserve one Git tag for the merged commit; publish only a complete verified draft.
[CmdletBinding()]
param(
    [ValidateSet('prepare', 'publish')][string]$Action,
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$ReleaseTag = $env:RELEASE_TAG,
    [string]$Source = $env:SOURCE_SHA,
    [string]$Assets = $env:CLI_OUTPUT
)

. "$PSScriptRoot/version.ps1"
$script:releaseRids = @('osx-arm64', 'linux-x64', 'linux-arm64', 'win-arm64')

function Invoke-CliRelease {
    if (-not $Repository) { throw 'GITHUB_REPOSITORY is required for release operations.' }
    Assert-FullHistory
    Invoke-Git fetch origin --tags | Write-Host
    if ($Action -eq 'prepare') { Prepare-Release; return }
    if ($Action -ne 'publish') { throw 'Choose prepare or publish.' }
    if ((Invoke-Git rev-parse HEAD) -ne $Source) { throw 'Publish checkout does not match the prepared source SHA.' }
    Assert-ReleaseTag $ReleaseTag $Source
    Publish-Release
}

function Prepare-Release {
    $event = Read-MergeEvent
    $sourceSha = $event.pull_request.merge_commit_sha
    if ((Invoke-Git rev-parse HEAD) -ne $sourceSha) { throw 'Checkout must be the exact PR merge commit, not its head or moving main.' }
    if (Invoke-Git status --porcelain) { throw 'Release preparation requires a clean checkout.' }
    Assert-MainAncestor $sourceSha
    $labels = @($event.pull_request.labels | ForEach-Object { $_.name })
    if ($labels -contains 'release:major') { throw 'Major releases require a separate explicit operator request or approval; automatic X increments are disabled.' }
    $tags = @(Get-CliTags | Where-Object { (Invoke-Git rev-parse "refs/tags/$_^{commit}") -eq $sourceSha })
    if ($tags.Count -gt 1) { throw 'Multiple CLI version tags at this source SHA; reservation is ambiguous.' }
    $tag = $tags | Select-Object -First 1
    if (-not $tag) {
        $bump = 'minor'
        if ($labels -contains 'release:patch') { $bump = 'patch' }
        $tag = Get-NextCliTag $bump
        Invoke-Gh api "repos/$Repository/git/refs" --method POST -f "ref=refs/tags/$tag" -f "sha=$sourceSha" | Write-Host
        Invoke-Git fetch origin --tags | Write-Host
    }
    Assert-ReleaseTag $tag $sourceSha
    Write-Host "Prepared $tag at $sourceSha"
    if ($env:GITHUB_OUTPUT) {
        Add-Content $env:GITHUB_OUTPUT "tag=$tag" -Encoding utf8
        Add-Content $env:GITHUB_OUTPUT "source_sha=$sourceSha" -Encoding utf8
    }
}

function Publish-Release {
    if (-not $Assets) { throw 'CLI_OUTPUT must name the downloaded release asset directory.' }
    Assert-AssetSet $Assets
    $release = Find-Release $ReleaseTag
    if ($release -and -not $release.draft) {
        Assert-RemoteAssets $release
        Write-Host "Already published $ReleaseTag; immutable assets and latest remain unchanged."
        return
    }
    if (-not $release) {
        Invoke-Gh release create $ReleaseTag --repo $Repository --target $Source --verify-tag --title $ReleaseTag --generate-notes --draft | Write-Host
        $release = Find-Release $ReleaseTag
    }
    if (-not $release.draft) { throw 'Asset replacement is permitted only for an unpublished draft.' }
    $files = @(Get-ExpectedAssetNames | ForEach-Object { Join-Path $Assets $_ })
    Invoke-Gh release upload $ReleaseTag --repo $Repository @files --clobber | Write-Host
    Assert-RemoteAssets (Find-Release $ReleaseTag)
    $latest = Test-LatestDescendant
    Invoke-Gh release edit $ReleaseTag --repo $Repository --draft=false "--latest=$($latest.ToString().ToLowerInvariant())" | Write-Host
    $published = Find-Release $ReleaseTag
    if ($published.draft) { throw 'Release remained draft after publication.' }
    Write-Host "Published $ReleaseTag at $Source (latest=$latest)."
}

function Read-MergeEvent {
    if (-not $env:GITHUB_EVENT_PATH) { throw 'GITHUB_EVENT_PATH must name the frozen merged-PR event.' }
    $event = Get-Content $env:GITHUB_EVENT_PATH -Raw | ConvertFrom-Json
    if ($env:GITHUB_EVENT_NAME -ne 'pull_request_target' -or $event.action -ne 'closed' -or
        -not $event.pull_request.merged -or $event.pull_request.base.ref -ne 'main' -or
        $event.repository.full_name -ne $Repository) { throw 'Only a merged PR into this repository main may reserve a release.' }
    return $event
}

function Assert-MainAncestor {
    param([string]$Commit)
    & git -C $script:repositoryRoot merge-base --is-ancestor $Commit origin/main
    if ($LASTEXITCODE -ne 0) { throw 'Release source is not on fetched origin/main.' }
}

function Get-ExpectedAssetNames {
    foreach ($runtime in $script:releaseRids) { "forge-$runtime.zip"; "forge-$runtime.zip.sha256" }
}

function Assert-AssetSet {
    param([string]$Directory)
    $expected = @(Get-ExpectedAssetNames | Sort-Object)
    $actual = @(Get-ChildItem $Directory -File -Filter '*.zip*' | Select-Object -ExpandProperty Name | Sort-Object)
    if (($actual -join ',') -ne ($expected -join ',')) { throw 'Release requires exactly four platform ZIPs and their four checksums.' }
    foreach ($runtime in $script:releaseRids) {
        $name = "forge-$runtime.zip"
        $checksum = (Get-Content (Join-Path $Directory "$name.sha256") -Raw).Trim()
        $hash = (Get-FileHash (Join-Path $Directory $name) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($checksum -cne "$hash  $name") { throw "Checksum mismatch for $name." }
    }
}

function Assert-RemoteAssets {
    param($Release)
    $expected = @(Get-ExpectedAssetNames | Sort-Object)
    $actual = @($Release.assets | ForEach-Object { $_.name } | Sort-Object)
    if (($actual -join ',') -ne ($expected -join ',')) { throw 'Remote release asset set is incomplete or unexpected.' }
    $temporary = Join-Path ([IO.Path]::GetTempPath()) "forge-release-$([guid]::NewGuid())"
    New-Item -ItemType Directory $temporary | Out-Null
    try {
        Invoke-Gh release download $ReleaseTag --repo $Repository --pattern 'forge-*.zip*' --dir $temporary | Write-Host
        Assert-AssetSet $temporary
    } finally { Remove-Item $temporary -Recurse -Force }
}

function Find-Release {
    param([string]$Tag)
    $pages = Invoke-Gh api "repos/$Repository/releases?per_page=100" --paginate --slurp | ConvertFrom-Json
    foreach ($page in $pages) {
        foreach ($release in $page) { if ($release.tag_name -ceq $Tag) { return $release } }
    }
    return $null
}

function Test-LatestDescendant {
    $latest = Invoke-Gh api "repos/$Repository/releases/latest" | ConvertFrom-Json
    $latestSource = Invoke-Git rev-parse "refs/tags/$($latest.tag_name)^{commit}"
    & git -C $script:repositoryRoot merge-base --is-ancestor $latestSource $Source
    $code = $LASTEXITCODE
    if ($code -gt 1) { throw 'Cannot compare current latest release ancestry.' }
    return $code -eq 0
}

function Invoke-Gh {
    $output = & gh @args
    if ($LASTEXITCODE -ne 0) { throw "gh operation failed ($LASTEXITCODE)." }
    return ($output -join "`n").Trim()
}

Invoke-CliRelease

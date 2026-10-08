# Exercise Git identity and release failure boundaries in a disposable repository; never call GitHub.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:testRoot = Join-Path ([IO.Path]::GetTempPath()) "forge-cli-tests-$([guid]::NewGuid())"
$script:fixture = Join-Path $script:testRoot 'checkout'
$script:remote = Join-Path $script:testRoot 'origin.git'
$script:remoteAssets = Join-Path $script:testRoot 'remote-assets'
$script:mockRelease = $null
$script:latestTag = 'v0.9.5'
$script:uploadCount = 0
$script:failUpload = $false
$script:passed = 0

function Test-CliScripts {
    Initialize-Fixture
    . "$script:fixture/scripts/version.ps1"
    Test-VersionPolicy
    Test-ReleaseBoundaries
    Test-BuildFailure
    Write-Host "PASS: $script:passed CLI script boundary checks."
}

function Test-VersionPolicy {
    Assert-Equal (Get-CliVersion).Version '0.9.5-dev.0' 'Exact tag is still development locally'
    Add-FixtureCommit 'next'
    Assert-Equal (Get-CliVersion).Version '0.9.5-dev.1' 'Commit distance'
    Assert-Equal (Get-NextCliTag) 'v0.10.0' 'Default minor resets patch'
    Assert-Equal (Get-NextCliTag patch) 'v0.9.6' 'Explicit patch'
    Invoke-Git tag component-v9.9.9 | Out-Null
    Invoke-Git tag v09.9.9 | Out-Null
    Assert-Equal (Get-NextCliTag) 'v0.10.0' 'Ignore component and invalid SemVer tags'
    Set-Content (Join-Path $script:fixture 'untracked.txt') dirty
    Assert-True ((Get-CliVersion).Identity.EndsWith('.dirty')) 'Dirty development metadata'
    Remove-Item (Join-Path $script:fixture 'untracked.txt')
    Assert-Fails { Assert-ReleaseTag v0.9.5 (Invoke-Git rev-parse HEAD) } 'not' 'Wrong source cannot use a release tag'
    $shallow = Join-Path $script:testRoot 'shallow'
    Invoke-FixtureGit @('clone', '--quiet', '--depth=1', [uri]::new($script:remote, [UriKind]::Absolute).AbsoluteUri, $shallow)
    $originalRoot = $script:repositoryRoot
    try { $script:repositoryRoot = $shallow; Assert-Fails { Get-CliVersion } 'Shallow' 'Shallow history rejected' }
    finally { $script:repositoryRoot = $originalRoot }
    $empty = Join-Path $script:testRoot 'no-tags'
    Invoke-FixtureGit @('clone', '--quiet', '--no-tags', $script:remote, $empty)
    try { $script:repositoryRoot = $empty; Assert-Fails { Get-CliVersion } 'No CLI version tags' 'Missing tags rejected' }
    finally { $script:repositoryRoot = $originalRoot }
}

function Test-ReleaseBoundaries {
    $testSource = Invoke-Git rev-parse HEAD
    Write-MergeEvent $testSource @()
    . "$script:fixture/scripts/release.ps1" -Action prepare -Repository test/forge-mcl
    Assert-Equal (Invoke-Git rev-parse 'v0.10.0^{commit}') $testSource 'Minor reservation at exact source'
    . "$script:fixture/scripts/release.ps1" -Action prepare -Repository test/forge-mcl
    Assert-Equal (@(Get-CliTags).Count) 2 'Reservation rerun reuses tag'
    Assert-Equal (Get-CliVersion v0.10.0).Identity "0.10.0+$testSource" 'Official identity'
    Set-Content (Join-Path $script:fixture 'untracked.txt') dirty
    Assert-Fails { Get-CliVersion v0.10.0 } 'clean' 'Dirty official build refused'
    Remove-Item (Join-Path $script:fixture 'untracked.txt')
    $fixtureAssets = Write-FixtureAssets
    $script:failUpload = $true
    Assert-Fails { . "$script:fixture/scripts/release.ps1" -Action publish -Repository test/forge-mcl -ReleaseTag v0.10.0 -Source $testSource -Assets $fixtureAssets } 'gh operation failed' 'Failed upload stays draft'
    Assert-True $script:mockRelease.draft 'No publication after partial upload'
    . "$script:fixture/scripts/release.ps1" -Action publish -Repository test/forge-mcl -ReleaseTag v0.10.0 -Source $testSource -Assets $fixtureAssets
    Assert-True (-not $script:mockRelease.draft) 'Complete draft retry publishes'
    Assert-Equal $script:uploadCount 2 'Retry replaces entire draft asset set'
    . "$script:fixture/scripts/release.ps1" -Action publish -Repository test/forge-mcl -ReleaseTag v0.10.0 -Source $testSource -Assets $fixtureAssets
    Assert-Equal $script:uploadCount 2 'Completed rerun cannot upload or replace assets'
    Set-Content (Join-Path $fixtureAssets 'forge-osx-arm64.zip.sha256') invalid
    Assert-Fails { Assert-AssetSet $fixtureAssets } 'Checksum mismatch' 'Checksum failure blocks publication'
    Remove-Item (Join-Path $fixtureAssets 'forge-osx-arm64.zip.sha256')
    Assert-Fails { Assert-AssetSet $fixtureAssets } 'exactly four' 'Incomplete set rejected'
    Add-FixtureCommit 'patch'
    Write-MergeEvent (Invoke-Git rev-parse HEAD) @('release:patch')
    . "$script:fixture/scripts/release.ps1" -Action prepare -Repository test/forge-mcl
    Assert-True (@(Get-CliTags) -contains 'v0.10.1') 'Bug-fix reservation increments patch'
    $script:latestTag = 'v0.10.1'
    $Source = $testSource
    Assert-True (-not (Test-LatestDescendant)) 'Older source cannot become latest'
    Write-MergeEvent (Invoke-Git rev-parse HEAD) @('release:major')
    Assert-Fails { . "$script:fixture/scripts/release.ps1" -Action prepare -Repository test/forge-mcl } 'Major releases require' 'Major never automatic'
    Invoke-Git tag v0.10.2 | Out-Null
    Write-MergeEvent (Invoke-Git rev-parse HEAD) @()
    Assert-Fails { . "$script:fixture/scripts/release.ps1" -Action prepare -Repository test/forge-mcl } 'ambiguous' 'Conflicting source tags refused'
    Write-MergeEvent $testSource @()
    Assert-Fails { . "$script:fixture/scripts/release.ps1" -Action prepare -Repository test/forge-mcl } 'exact PR merge commit' 'Moving checkout rejected'
}

function Test-BuildFailure {
    $before = $script:uploadCount
    function dotnet { $script:capturedCompile = @($args); $global:LASTEXITCODE = 0 }
    Set-Content (Join-Path $script:fixture 'untracked.txt') dirty
    $expectedIdentity = (Get-CliVersion).Identity
    . "$script:fixture/scripts/build.ps1" -Action build -ReleaseTag ''
    Assert-True ($script:capturedCompile -contains "-p:InformationalVersion=$expectedIdentity") 'Managed build preserves complete dirty identity'
    . "$script:fixture/scripts/build.ps1" -Action test -ReleaseTag ''
    Assert-True ($script:capturedCompile -contains "-p:InformationalVersion=$expectedIdentity") 'Managed test recompilation shares identity'
    Remove-Item (Join-Path $script:fixture 'untracked.txt')
    function dotnet { $global:LASTEXITCODE = 1; 'Controlled compiler failure' }
    Assert-Fails { . "$script:fixture/scripts/build.ps1" -Action publish -Rid linux-x64 -Output (Join-Path $script:testRoot 'failed-build') -ReleaseTag '' } 'dotnet failed' 'Compiler error propagates'
    Assert-Equal $script:uploadCount $before 'Build failure cannot publish'
}

function Initialize-Fixture {
    New-Item -ItemType Directory -Force $script:fixture, $script:remoteAssets | Out-Null
    Invoke-FixtureGit @('init', '--quiet', '--bare', '--initial-branch=main', $script:remote)
    Invoke-FixtureGit @('init', '--quiet', '--initial-branch=main', $script:fixture)
    Invoke-FixtureGit @('-C', $script:fixture, 'config', 'user.name', 'CLI fixture')
    Invoke-FixtureGit @('-C', $script:fixture, 'config', 'user.email', 'fixture@example.invalid')
    New-Item -ItemType Directory (Join-Path $script:fixture 'scripts') | Out-Null
    Copy-Item "$PSScriptRoot/version.ps1", "$PSScriptRoot/build.ps1", "$PSScriptRoot/release.ps1" (Join-Path $script:fixture 'scripts')
    Set-Content (Join-Path $script:fixture '.gitignore') "dist/`nartifacts/"
    Invoke-FixtureGit @('-C', $script:fixture, 'add', '.')
    Invoke-FixtureGit @('-C', $script:fixture, 'commit', '--quiet', '-m', 'base')
    Invoke-FixtureGit @('-C', $script:fixture, 'tag', 'v0.9.5')
    Invoke-FixtureGit @('-C', $script:fixture, 'remote', 'add', 'origin', $script:remote)
    Invoke-FixtureGit @('-C', $script:fixture, 'push', '--quiet', '-u', 'origin', 'main', '--tags')
}

function Add-FixtureCommit {
    param([string]$Text)
    Set-Content (Join-Path $script:fixture 'change.txt') $Text
    Invoke-Git add change.txt | Out-Null
    Invoke-Git commit --quiet -m $Text | Out-Null
    Invoke-Git push --quiet origin main | Out-Null
}

function Write-MergeEvent {
    param([string]$Commit, [string[]]$Labels)
    $event = @{ action = 'closed'; repository = @{ full_name = 'test/forge-mcl' }; pull_request = @{
        merged = $true; merge_commit_sha = $Commit; base = @{ ref = 'main' }; labels = @($Labels | ForEach-Object { @{ name = $_ } }) } }
    $env:GITHUB_EVENT_PATH = Join-Path $script:testRoot 'merge.json'
    $env:GITHUB_EVENT_NAME = 'pull_request_target'
    $env:GITHUB_OUTPUT = Join-Path $script:testRoot 'outputs.txt'
    $event | ConvertTo-Json -Depth 5 | Set-Content $env:GITHUB_EVENT_PATH
}

function Write-FixtureAssets {
    $directory = Join-Path $script:testRoot 'assets'
    New-Item -ItemType Directory $directory | Out-Null
    foreach ($runtime in @('osx-arm64', 'linux-x64', 'linux-arm64', 'win-arm64')) {
        $name = "forge-$runtime.zip"
        Set-Content (Join-Path $directory $name) "verified fixture $runtime"
        $hash = (Get-FileHash (Join-Path $directory $name) -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content (Join-Path $directory "$name.sha256") "$hash  $name"
    }
    return $directory
}

# Intercept only GitHub transport; Git tags, files, checksums and production script logic are real.
function gh {
    $global:LASTEXITCODE = 0
    $arguments = @($args)
    if ($arguments[0] -eq 'api') { Invoke-FakeApi $arguments; return }
    if ($arguments[1] -eq 'create') {
        $script:mockRelease = [pscustomobject]@{ tag_name = $arguments[2]; draft = $true; assets = @() }
        return
    }
    if ($arguments[1] -eq 'upload') { Invoke-FakeUpload $arguments; return }
    if ($arguments[1] -eq 'download') {
        $destination = $arguments[[Array]::IndexOf($arguments, '--dir') + 1]
        Copy-Item (Join-Path $script:remoteAssets '*') $destination -Force
        return
    }
    if ($arguments[1] -eq 'edit') {
        $script:mockRelease.draft = $false
        if ($arguments -contains '--latest=true') { $script:latestTag = $arguments[2] }
        return
    }
    throw "Unexpected GitHub transport in fixture: $($arguments -join ' ')"
}

function Invoke-FakeApi {
    param([object[]]$Arguments)
    if ($Arguments[1] -like '*/git/refs') {
        $ref = ($Arguments | Where-Object { $_ -like 'ref=*' }).Substring(4)
        $sha = ($Arguments | Where-Object { $_ -like 'sha=*' }).Substring(4)
        Invoke-FixtureGit @('--git-dir', $script:remote, 'update-ref', $ref, $sha)
        return '{}'
    }
    if ($Arguments[1] -like '*/releases/latest') { return (@{ tag_name = $script:latestTag } | ConvertTo-Json -Compress) }
    if ($Arguments[1] -like '*/releases?per_page=*') {
        if (-not $script:mockRelease) { return '[[]]' }
        return ConvertTo-Json -InputObject @(,@($script:mockRelease)) -Depth 5 -Compress
    }
    throw "Unexpected API operation: $($Arguments[1])"
}

function Invoke-FakeUpload {
    param([object[]]$Arguments)
    $script:uploadCount++
    $files = @($Arguments | Where-Object { $_ -like '*.zip*' })
    if ($script:failUpload) {
        $script:failUpload = $false
        Copy-Item $files[0] $script:remoteAssets -Force
        $script:mockRelease.assets = @(@{ name = [IO.Path]::GetFileName($files[0]) })
        $global:LASTEXITCODE = 1
        return
    }
    Copy-Item $files $script:remoteAssets -Force
    $script:mockRelease.assets = @($files | ForEach-Object { @{ name = [IO.Path]::GetFileName($_) } })
}

function Invoke-FixtureGit {
    param([string[]]$Arguments)
    & git @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Fixture git failed: $($Arguments -join ' ')" }
}

function Assert-Equal {
    param($Actual, $Expected, [string]$Name)
    if ($Actual -cne $Expected) { throw "$Name`: expected $Expected, got $Actual" }
    $script:passed++
}

function Assert-True {
    param([bool]$Condition, [string]$Name)
    if (-not $Condition) { throw $Name }
    $script:passed++
}

function Assert-Fails {
    param([scriptblock]$Operation, [string]$Message, [string]$Name)
    try { & $Operation | Out-Null } catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw "$Name`: unexpected failure: $($_.Exception.Message)" }
        $script:passed++
        return
    }
    throw "$Name`: expected failure"
}

try { Test-CliScripts } finally { Remove-Item $script:testRoot -Recurse -Force }

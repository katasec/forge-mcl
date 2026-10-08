# Git owns CLI identity. Release tags are exact; other builds describe development since a tag.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:repositoryRoot = Split-Path $PSScriptRoot -Parent

function Get-CliVersion {
    param([string]$Tag)
    Assert-FullHistory
    $source = Invoke-Git rev-parse HEAD
    $dirty = [bool](Invoke-Git status --porcelain)
    if ($Tag) {
        Assert-ReleaseTag $Tag $source
        if ($dirty) { throw 'Official builds require a clean checkout.' }
        return [pscustomobject]@{ Version = $Tag.Substring(1); Source = $source; Identity = "$($Tag.Substring(1))+$source"; Tag = $Tag }
    }
    $tags = @(Get-CliTags)
    if ($tags.Count -eq 0) { throw 'No CLI version tags. Fetch tags and full history: git fetch --tags --unshallow (omit --unshallow for a full clone).' }
    $matchArguments = @($tags | ForEach-Object { '--match'; $_ })
    $base = Invoke-Git describe --tags --abbrev=0 @matchArguments HEAD
    $distance = Invoke-Git rev-list --count "$base..HEAD"
    $version = "$($base.Substring(1))-dev.$distance"
    $metadata = $source
    if ($dirty) { $metadata += '.dirty' }
    return [pscustomobject]@{ Version = $version; Source = $source; Identity = "$version+$metadata"; Tag = $base }
}

function Get-NextCliTag {
    param([ValidateSet('minor', 'patch')][string]$Bump = 'minor')
    Assert-FullHistory
    $tags = @(Get-CliTags | Sort-Object { [version]$_.Substring(1) } -Descending)
    if ($tags.Count -eq 0) { throw 'No CLI version tags; fetch origin tags before reserving a release.' }
    $base = [version]$tags[0].Substring(1)
    if ($Bump -eq 'patch') { return "v$($base.Major).$($base.Minor).$($base.Build + 1)" }
    return "v$($base.Major).$($base.Minor + 1).0"
}

function Assert-ReleaseTag {
    param([string]$Tag, [string]$Source)
    if ($Tag -cnotmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw "Invalid CLI release tag: $Tag" }
    $target = Invoke-Git rev-parse "refs/tags/$Tag^{commit}"
    if ($target -ne $Source) { throw "Release tag $Tag points to $target, not $Source." }
}

function Get-CliTags {
    $all = Invoke-Git tag --list
    return @($all -split '\r?\n' | Where-Object { $_ -cmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' })
}

function Assert-FullHistory {
    if ((Invoke-Git rev-parse --is-shallow-repository) -eq 'true') { throw 'Shallow history cannot determine CLI identity. Run git fetch --unshallow --tags.' }
}

function Invoke-Git {
    $output = & git -C $script:repositoryRoot @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed ($LASTEXITCODE)." }
    return ($output -join "`n").Trim()
}

if ($MyInvocation.InvocationName -ne '.') { Get-CliVersion $env:RELEASE_TAG | ConvertTo-Json -Compress }

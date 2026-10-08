# One portable build path: resolve Git identity, build/verify, then install or package the payload.
[CmdletBinding()]
param(
    [ValidateSet('build', 'test', 'clean', 'install', 'publish', 'verify', 'package')][string]$Action = 'build',
    [string]$Rid = $env:RID,
    [string]$Output = $env:CLI_OUTPUT,
    [string]$ReleaseTag = $env:RELEASE_TAG
)

. "$PSScriptRoot/version.ps1"

function Invoke-CliBuild {
    if ($Action -eq 'clean') { Invoke-Checked dotnet @('clean', 'ForgeMission.slnx'); return }
    $identity = Get-CliVersion $ReleaseTag
    $properties = @(Get-IdentityProperties $identity)
    if ($Action -eq 'build') { Invoke-Checked dotnet (@('build', 'ForgeMission.slnx') + $properties); return }
    if ($Action -eq 'test') { Invoke-Checked dotnet (@('test', 'ForgeMission.slnx') + $properties); return }
    $runtime = Resolve-Runtime $Rid
    $destination = Resolve-Destination
    if ($Action -eq 'verify') { Invoke-ManagedVerification $destination $identity }
    $native = Join-Path $destination 'native'
    Publish-Native $native $runtime $identity
    if ($IsMacOS) { Invoke-Checked codesign @('--force', '--sign', '-', (Join-Path $native 'forge')) }
    Assert-NativeIdentity $native $identity $destination
    if ($Action -eq 'install') { Install-Payload $native; return }
    if ($Action -eq 'package') { Write-Package $native $runtime $destination }
}

function Resolve-Runtime {
    param([string]$Requested)
    if ($Requested) { return $Requested }
    $architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    if ($IsMacOS) { return "osx-$architecture" }
    if ($IsLinux) { return "linux-$architecture" }
    if ($IsWindows) { return "win-$architecture" }
    throw 'Unsupported operating system for Native AOT CLI publishing.'
}

function Resolve-Destination {
    if ($Output) { return [IO.Path]::GetFullPath($Output) }
    if ($Action -eq 'verify') { return Join-Path $script:repositoryRoot 'artifacts/verification' }
    return Join-Path $script:repositoryRoot 'dist/cli'
}

function Invoke-ManagedVerification {
    param([string]$Destination, $Identity)
    New-Item -ItemType Directory -Force $Destination | Out-Null
    Set-Content (Join-Path $Destination 'source.txt') $Identity.Source
    $properties = @(Get-IdentityProperties $Identity)
    Invoke-Checked dotnet (@('build', 'ForgeMission.slnx', '-warnaserror') + $properties) (Join-Path $Destination 'managed-build.log')
    # Existing live-UI exclusions: these tests require physical terminal animation/input.
    $filter = 'FullyQualifiedName!~ForgeMission.Tests.Cli.StartPageTests&FullyQualifiedName!~ForgeMission.Tests.Cli.ChatScreenLiveMotionTests'
    Invoke-Checked dotnet (@('test', 'ForgeMission.slnx', '--filter', $filter, '-warnaserror') + $properties) (Join-Path $Destination 'managed-tests.log')
}

function Publish-Native {
    param([string]$Destination, [string]$Runtime, $Identity)
    if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force }
    New-Item -ItemType Directory -Force $Destination | Out-Null
    $arguments = @('publish', 'src/ForgeMission.Cli', '-c', 'Release', '-r', $Runtime, '-o', $Destination, '-warnaserror') + @(Get-IdentityProperties $Identity)
    # Preserve existing laptop install diagnostics; canonical verify/package retain the raw zero-warning gate.
    Invoke-Checked dotnet $arguments (Join-Path (Split-Path $Destination -Parent) 'native-publish.log') ($Action -ne 'install')
}

function Get-IdentityProperties {
    param($Identity)
    return @("-p:Version=$($Identity.Version)", "-p:InformationalVersion=$($Identity.Identity)",
        '-p:IncludeSourceRevisionInInformationalVersion=false', "-p:SourceRevisionId=$($Identity.Source)")
}

function Assert-NativeIdentity {
    param([string]$Native, $Identity, [string]$Evidence)
    $binaryName = 'forge'
    if ($IsWindows) { $binaryName = 'forge.exe' }
    $binary = Join-Path $Native $binaryName
    Invoke-Checked $binary @('--help') (Join-Path $Evidence 'native-help.txt')
    $version = & $binary --version
    if ($LASTEXITCODE -ne 0 -or ($version -join "`n").Trim() -ne $Identity.Identity) { throw "Native version/source mismatch: $version; expected $($Identity.Identity)." }
    Set-Content (Join-Path $Evidence 'native-version.txt') $Identity.Identity
    Set-Content (Join-Path $Evidence 'native-sha256.txt') (Get-FileHash $binary -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "Verified native CLI: $($Identity.Identity)"
}

function Install-Payload {
    param([string]$Native)
    $destination = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.local/bin'
    New-Item -ItemType Directory -Force $destination | Out-Null
    Copy-Item (Join-Path $Native '*') $destination -Recurse -Force
    Write-Host "Installed complete CLI payload in $destination"
}

function Write-Package {
    param([string]$Native, [string]$Runtime, [string]$Destination)
    $archive = Join-Path $Destination "forge-$Runtime.zip"
    if (Test-Path $archive) { Remove-Item $archive -Force }
    [IO.Compression.ZipFile]::CreateFromDirectory($Native, $archive)
    $hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content "$archive.sha256" "$hash  forge-$Runtime.zip" -Encoding ascii
    Write-Host "Packaged $archive"
}

function Invoke-Checked {
    param([string]$Command, [string[]]$Arguments, [string]$Log, [bool]$RejectRawWarnings = $true)
    $lines = [Collections.Generic.List[string]]::new()
    & $Command @Arguments 2>&1 | ForEach-Object { $line = "$_"; $lines.Add($line); Write-Host $line }
    $code = $LASTEXITCODE
    if ($Log) { Set-Content $Log $lines -Encoding utf8 }
    if ($code -ne 0) { throw "$Command failed ($code)." }
    if ($RejectRawWarnings -and ($lines | Where-Object { $_ -match '(^|\s)warning(\s+[A-Z]+[0-9]+)?\s*:' })) { throw "$Command produced compiler/linker warnings; see $Log." }
}

Push-Location $script:repositoryRoot
try { Invoke-CliBuild } finally { Pop-Location }

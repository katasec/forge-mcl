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
    if ($Action -in @('verify', 'package')) { Test-NativeExec $destination $runtime $identity }
    if ($Action -eq 'package') { Write-Package $native $runtime $destination }
}

function Test-NativeExec {
    param([string]$Destination, [string]$Runtime, $Identity)
    $proof = Join-Path $Destination 'exec-probe'
    $arguments = @('publish', 'tests/ForgeMission.Exec.Probe', '-c', 'Release', '-r', $Runtime,
        '-p:PublishAot=true', '-warnaserror', '-o', $proof) + @(Get-IdentityProperties $Identity)
    Invoke-Checked dotnet $arguments (Join-Path $Destination 'exec-probe-publish.log')
    $name = 'ForgeMission.Exec.Probe'
    if ($IsWindows) { $name += '.exe' }
    $startedAt = [DateTimeOffset]::UtcNow
    $probeFailed = $false
    try { Invoke-Checked (Join-Path $proof $name) @() (Join-Path $Destination 'exec-probe-run.log') }
    catch { $probeFailed = $true; throw }
    finally {
        if ($IsMacOS -and $probeFailed) {
            try { Save-NativeProbeCrashReports $Destination $startedAt }
            catch { Write-Host "Native probe crash-report collection failed: $_" }
        }
    }
    if ($Runtime -eq 'linux-x64' -and $env:GITHUB_ACTIONS -eq 'true') {
        Test-InitHostedExec $Destination $proof
    }
}

function Save-NativeProbeCrashReports {
    param([string]$Destination, [DateTimeOffset]$StartedAt)
    $output = Join-Path $Destination 'exec-probe-crashreports'
    New-Item -ItemType Directory -Force $output -ErrorAction Stop | Out-Null
    $userReports = Join-Path $HOME 'Library/Logs/DiagnosticReports'
    $roots = @($userReports, '/Library/Logs/DiagnosticReports')
    $available = @($roots | Where-Object { Test-Path -LiteralPath $_ -PathType Container })
    $absent = @($roots | Where-Object { $_ -notin $available })
    $copied = [Collections.Generic.List[object]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $errors = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $collectionStartedAt = [DateTimeOffset]::UtcNow
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    while ($elapsed.Elapsed.TotalSeconds -lt 10) {
        $scanErrors = @()
        $files = @()
        if ($available.Count -gt 0) {
            $files = @(Get-ChildItem -LiteralPath $available -Filter 'ForgeMission.Exec.Probe-*.ips' -File -ErrorAction SilentlyContinue -ErrorVariable scanErrors)
        }
        foreach ($issue in $scanErrors) { $errors["$($issue.TargetObject)"] = "$issue" }
        foreach ($file in $files) {
            if ($seen.Contains($file.FullName)) { continue }
            try {
                $report = Read-NativeProbeCrashReport $file.FullName $StartedAt
                if ($null -eq $report) { continue }
                $source = $file.DirectoryName -eq $userReports ? 'user' : 'system'
                $target = Join-Path $output $source
                New-Item -ItemType Directory -Force $target -ErrorAction Stop | Out-Null
                Copy-Item -LiteralPath $file.FullName -Destination $target -ErrorAction Stop
                $seen.Add($file.FullName) | Out-Null
                $copied.Add([pscustomobject]@{ source = $source; file = $file.Name; procLaunch = $report.ProcLaunch })
            }
            catch { $errors[$file.FullName] = "$_" }
        }
        $remaining = 10000 - $elapsed.Elapsed.TotalMilliseconds
        if ($remaining -gt 0) { Start-Sleep -Milliseconds ([Math]::Min(250, [int]$remaining)) }
    }
    $status = [ordered]@{
        probeStartedAt = $StartedAt; collectionStartedAt = $collectionStartedAt; collectionEndedAt = [DateTimeOffset]::UtcNow
        outcome = $errors.Count -gt 0 ? 'diagnostic-failure' : ($copied.Count -gt 0 ? 'copied' : 'no-reports')
        absentDirectories = $absent; copied = $copied.ToArray(); errors = @($errors.Values)
    }
    $status | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output 'collection.json') -Encoding utf8 -ErrorAction Stop
    Write-Host "Native probe crash reports: $($status.outcome); copied $($copied.Count); see $output"
}

function Read-NativeProbeCrashReport {
    param([string]$Path, [DateTimeOffset]$StartedAt)
    $documents = (Get-Content -LiteralPath $Path -Raw -ErrorAction Stop) -split '\r?\n', 2
    if ($documents.Count -ne 2) { throw "Unsupported native probe crash report: $Path" }
    $header = ConvertFrom-Json -InputObject $documents[0] -AsHashtable -ErrorAction Stop
    $body = ConvertFrom-Json -InputObject $documents[1] -AsHashtable -ErrorAction Stop
    if ($header['app_name'] -cne 'ForgeMission.Exec.Probe' -or $body['procName'] -cne 'ForgeMission.Exec.Probe') { return $null }
    $launchedAt = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse($body['procLaunch'], [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AllowWhiteSpaces, [ref]$launchedAt)) { throw "Invalid native probe launch time: $Path" }
    if ($launchedAt -lt $StartedAt) { return $null }
    return [pscustomobject]@{ ProcLaunch = $launchedAt.ToUniversalTime() }
}

function Test-InitHostedExec {
    param([string]$Destination, [string]$Proof)
    $image = 'ghcr.io/katasec/forge-runner@sha256:c0031d451d046d4f28a75ca7f7c7f26169b26e92555de4b601128a005670331c'
    $container = 'forge-core-init-' + [Guid]::NewGuid().ToString('N')
    $mount = "type=bind,source=$([IO.Path]::GetFullPath($Proof)),target=/proof,readonly"
    Invoke-Checked docker @('pull', $image) (Join-Path $Destination 'exec-init-image-pull.log')
    $indexLog = Join-Path $Destination 'exec-init-index.json'
    Invoke-Checked docker @('manifest', 'inspect', $image) $indexLog
    $index = Get-Content $indexLog -Raw | ConvertFrom-Json
    $platform = @($index.manifests | Where-Object { $_.platform.os -eq 'linux' -and $_.platform.architecture -eq 'amd64' })
    if ($platform.Count -ne 1 -or $platform[0].digest -ne 'sha256:cf2a7e14f639519af223dd1efa0a2adf4d40cf1cac5f1b7d2017c5860ca1078b') { throw 'Published Runner platform manifest mismatch.' }
    $format = '{"id":{{json .Id}},"architecture":{{json .Architecture}},"digests":{{json .RepoDigests}},"entrypoint":{{json .Config.Entrypoint}},"revision":{{json (index .Config.Labels "org.opencontainers.image.revision")}}}'
    $imageLog = Join-Path $Destination 'exec-init-image.json'
    Invoke-Checked docker @('image', 'inspect', '--format', $format, $image) $imageLog
    $facts = Get-Content $imageLog -Raw | ConvertFrom-Json
    if ($facts.revision -ne '17080b73a84ad0b0e42a891024090e8f997194ed' -or $facts.architecture -ne 'amd64' -or
        ($facts.entrypoint -join ' ') -ne '/usr/bin/tini -- dotnet ForgeMission.Runner.dll') { throw 'Published Runner image identity mismatch.' }
    try {
        Invoke-Checked docker @('run', '--detach', '--name', $container, '--mount', $mount, $image) (Join-Path $Destination 'exec-init-start.log')
        Invoke-Checked docker @('exec', $container, '/proof/ForgeMission.Exec.Probe', '--verify-init') (Join-Path $Destination 'exec-init-run.log')
        # Deliberately unsupported topology, separate from the unchanged-entrypoint positive proof.
        Invoke-Checked docker @('run', '--rm', '--mount', "type=bind,source=$([IO.Path]::GetFullPath($proof)),target=/proof,readonly",
            '--entrypoint', '/proof/ForgeMission.Exec.Probe', $image, '--verify-pid1-refusal') (Join-Path $Destination 'exec-pid1-refusal.log')
    }
    finally { Invoke-Checked docker @('rm', '--force', $container) (Join-Path $Destination 'exec-init-cleanup.log') }
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

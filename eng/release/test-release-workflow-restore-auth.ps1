[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-ReleaseRestoreAuthentication {
    param([Parameter(Mandatory)] [string]$Step)

    if ($Step -notmatch '(?m)^\s+dotnet restore src/ForgeMission\.Mcl\.slnx --locked-mode\s*$') {
        throw 'Release restore step no longer contains the locked restore command.'
    }
    if ($Step -notmatch '(?m)^\s+NUGET_AUTH_TOKEN:\s+\$\{\{ secrets\.GITHUB_TOKEN \}\}\s*$') {
        throw 'Release restore step must provide NUGET_AUTH_TOKEN from secrets.GITHUB_TOKEN.'
    }
}

$workflowPath = Join-Path $PSScriptRoot '../../.github/workflows/release-packages.yml'
$workflow = Get-Content -Raw -LiteralPath $workflowPath
$match = [regex]::Match($workflow, '(?ms)^\s*- name: Verify provenance and locked build\s*\r?\n.*?(?=^\s*- name:|\z)')
if (-not $match.Success) { throw 'Missing named release restore step.' }
Assert-ReleaseRestoreAuthentication -Step $match.Value

$missingTokenFixture = @'
      - name: Verify provenance and locked build
        shell: pwsh
        run: |
          dotnet restore src/ForgeMission.Mcl.slnx --locked-mode
'@
$negativeRejected = $false
try { Assert-ReleaseRestoreAuthentication -Step $missingTokenFixture }
catch {
    if ($_.Exception.Message -match 'NUGET_AUTH_TOKEN') { $negativeRejected = $true } else { throw }
}
if (-not $negativeRejected) { throw 'Missing release restore token fixture was not rejected.' }

Write-Host 'PASS: release restore authentication is scoped to the locked restore step and missing-token fixtures are rejected.'

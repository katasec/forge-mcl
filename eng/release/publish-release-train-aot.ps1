[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$TrainManifestPath,
    [Parameter(Mandatory)] [string]$RepositoryCommit,
    [Parameter(Mandatory)] [string]$RuntimeIdentifier,
    [Parameter(Mandatory)] [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/release-train.ps1"
$train = Get-ReleaseTrain -ManifestPath $TrainManifestPath
$arguments = @('publish', 'src/ForgeMission.Cli/ForgeMission.Cli.csproj', '-c', 'Release', '-r', $RuntimeIdentifier, '--no-restore', '-o', $OutputDirectory, '/p:ContinuousIntegrationBuild=true', "/p:RepositoryCommit=$RepositoryCommit")
$arguments += Get-ReleaseTrainMsBuildArguments -Train $train
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Release-train Native AOT publish failed.' }

Write-Host "PASS: published Native AOT CLI for release train $($train.tag)."

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$TrainManifestPath,
    [Parameter(Mandatory)] [string]$RepositoryCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/release-train.ps1"
$train = Get-ReleaseTrain -ManifestPath $TrainManifestPath
$arguments = @('build', 'src/ForgeMission.Mcl.slnx', '-c', 'Release', '--no-restore', '/p:ContinuousIntegrationBuild=true', "/p:RepositoryCommit=$RepositoryCommit")
$arguments += Get-ReleaseTrainMsBuildArguments -Train $train
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Release-train build failed.' }

Write-Host "PASS: built release train $($train.tag)."

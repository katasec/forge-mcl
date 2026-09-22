[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$TrainManifestPath,
    [Parameter(Mandatory)] [string]$PackageDirectory,
    [Parameter(Mandatory)] [string]$RepositoryCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/release-train.ps1"
$train = Get-ReleaseTrain -ManifestPath $TrainManifestPath
New-Item -ItemType Directory -Force -Path $PackageDirectory | Out-Null

$arguments = @('pack', 'src/ForgeMission.Mcl.slnx', '-c', 'Release', '--no-build', '--output', $PackageDirectory, "/p:RepositoryCommit=$RepositoryCommit")
$arguments += Get-ReleaseTrainMsBuildArguments -Train $train
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Release-train pack failed.' }

Write-Host "PASS: packed release train $($train.tag)."

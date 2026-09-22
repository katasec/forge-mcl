Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ReleaseTrainCatalog {
    return @(
        [ordered]@{ id = 'Katasec.Forge.Mcl.Parser'; project = 'src/ForgeMission.Parser/ForgeMission.Parser.csproj'; property = 'ForgeMclParserPackageVersion'; assembly = 'ForgeMission.Parser.dll'; dependencies = @() },
        [ordered]@{ id = 'Katasec.Forge.Mcl.Core'; project = 'src/ForgeMission.Core/ForgeMission.Core.csproj'; property = 'ForgeMclCorePackageVersion'; assembly = 'ForgeMission.Core.dll'; dependencies = @('Katasec.Forge.Mcl.Parser') },
        [ordered]@{ id = 'Katasec.Forge.Mcl.ChatClients'; project = 'src/ForgeMission.ChatClients/ForgeMission.ChatClients.csproj'; property = 'ForgeMclChatClientsPackageVersion'; assembly = 'ForgeMission.ChatClients.dll'; dependencies = @('Katasec.Forge.Mcl.Core') },
        [ordered]@{ id = 'Katasec.Forge.Mcl.Scout'; project = 'src/ForgeMission.Scout/ForgeMission.Scout.csproj'; property = 'ForgeMclScoutPackageVersion'; assembly = 'ForgeMission.Scout.dll'; dependencies = @('Katasec.Forge.Mcl.Core') },
        [ordered]@{ id = 'Katasec.Forge.Mcl.MissionRegistry'; project = 'src/ForgeMission.MissionRegistry/ForgeMission.MissionRegistry.csproj'; property = 'ForgeMclMissionRegistryPackageVersion'; assembly = 'ForgeMission.MissionRegistry.dll'; dependencies = @('Katasec.Forge.Mcl.Core') },
        [ordered]@{ id = 'Katasec.Forge.Mcl.Serve'; project = 'src/ForgeMission.Serve/ForgeMission.Serve.csproj'; property = 'ForgeMclServePackageVersion'; assembly = 'ForgeMission.Serve.dll'; dependencies = @() },
        [ordered]@{ id = 'Katasec.Forge.Docker'; project = 'src/ForgeMission.Docker/ForgeMission.Docker.csproj'; property = 'ForgeDockerPackageVersion'; assembly = 'ForgeMission.Docker.dll'; dependencies = @() }
    )
}

function Get-ReleaseTrain {
    param([Parameter(Mandatory)] [string]$ManifestPath)

    if (-not (Test-Path -LiteralPath $ManifestPath)) { throw "Missing release-train manifest: $ManifestPath." }
    $train = Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json -AsHashtable
    if ($train.schema -ne 1 -or $train.tag -notmatch '^mcl-v\d+\.\d+\.\d+$') {
        throw "Invalid release-train schema or tag in $ManifestPath."
    }

    $catalog = Get-ReleaseTrainCatalog
    $catalogById = @{}
    foreach ($entry in $catalog) { $catalogById[$entry.id] = $entry }
    $packages = @($train.packages)
    if ($packages.Count -ne $catalog.Count) { throw "Release train must contain exactly $($catalog.Count) packages." }

    $seen = @{}
    foreach ($package in $packages) {
        if ($null -eq $package.id -or $null -eq $catalogById[$package.id]) { throw "Unexpected package in release train: $($package.id)." }
        if ($seen.ContainsKey($package.id)) { throw "Duplicate package in release train: $($package.id)." }
        if ($package.version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid stable package version for $($package.id)." }
        $expected = $catalogById[$package.id]
        if ($package.project -ne $expected.project -or $package.property -ne $expected.property) {
            throw "Unexpected project or property mapping for $($package.id)."
        }
        $seen[$package.id] = $package
    }

    for ($index = 0; $index -lt $catalog.Count; $index++) {
        if (-not $seen.ContainsKey($catalog[$index].id)) { throw "Missing release-train package: $($catalog[$index].id)." }
        if ($packages[$index].id -ne $catalog[$index].id) { throw 'Release-train package order must follow the dependency-safe catalog.' }
    }

    $train['_manifestPath'] = (Resolve-Path $ManifestPath).Path
    $train['_manifestSha256'] = (Get-FileHash -Algorithm SHA256 -LiteralPath $ManifestPath).Hash.ToLowerInvariant()
    $train['_catalog'] = $catalog
    return $train
}

function Get-ReleaseTrainPackage {
    param(
        [Parameter(Mandatory)] [hashtable]$Train,
        [Parameter(Mandatory)] [string]$PackageId
    )

    return @($Train.packages | Where-Object { $_.id -eq $PackageId } | Select-Object -First 1)[0]
}

function Get-ReleaseTrainDependencies {
    param(
        [Parameter(Mandatory)] [hashtable]$Train,
        [Parameter(Mandatory)] [string]$PackageId
    )

    $catalogEntry = @($Train._catalog | Where-Object { $_.id -eq $PackageId } | Select-Object -First 1)[0]
    $dependencies = @{}
    foreach ($dependencyId in $catalogEntry.dependencies) {
        $dependency = Get-ReleaseTrainPackage -Train $Train -PackageId $dependencyId
        $dependencies[$dependencyId] = "[$($dependency.version)]"
    }
    return $dependencies
}

function Get-ReleaseTrainMsBuildArguments {
    param([Parameter(Mandatory)] [hashtable]$Train)

    $arguments = @()
    foreach ($package in $Train.packages) {
        $arguments += "/p:$($package.property)=$($package.version)"
    }
    return $arguments
}

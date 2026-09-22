# Forge MCL

Private home for the Mission Control Language toolchain: syntax, provider-neutral execution,
provider/retrieval adapters, OCI mission-package client, Docker support, serving wire, and the
Native AOT `forge` CLI.

This repository owns no application, Desktop, Runner, Rooms, conversation, billing, or product
mission content. Those products consume released contracts; they must never use a source reference
into this repository.

## Build

```pwsh
dotnet restore src/ForgeMission.Mcl.slnx --locked-mode
dotnet build src/ForgeMission.Mcl.slnx --no-restore
dotnet test src/ForgeMission.Mcl.slnx --no-restore
dotnet publish src/ForgeMission.Cli -c Release -r osx-arm64 --no-restore
```

Run `pwsh ./eng/import/verify.ps1` to prove the bootstrap source provenance. Package publication
is governed by [the private package release procedure](docs/release-packages.md); consumer access
and product cutover remain separate proof cards.

## Bootstrap provenance and rollback

This repository's documented empty-repository seed is commit `4629e13`. It exists solely to give
the bootstrap branch a reviewable `main` base; the full imported baseline remains the monorepo
commit `d2c0c121bbe4180b60ddd44fa5f18872e2402771`, recorded and verified by
`eng/import/forge-mcl-v1.json`. Rolling back the bootstrap means reverting its merged PR and
continuing to consume the archived monorepo baseline; no consumer cutover, package publication,
or source deletion is part of this bootstrap.

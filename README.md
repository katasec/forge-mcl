# forge-mcl

`forge-mcl` owns the Mission Control Language runtime: parsing, provider-neutral execution,
provider adapters, web search, generic OCI registry pulls, HTTP wire mapping, Docker helpers, and
the Native AOT `forge` CLI.

It deliberately does not own a mission catalog, baked mission fallback, hosted Runner, Desktop,
conversations, Rooms, platform accounts, or billing. Those remain consumer-owned concerns.

## Build

```sh
dotnet build ForgeMission.slnx
dotnet test ForgeMission.slnx
dotnet run --project src/ForgeMission.Cli -- --help
```

Use `make install` to publish the current-platform Native AOT CLI locally.

## CLI releases

The manual [Release CLI workflow](.github/workflows/release.yml) builds the exact dispatched
`main` commit for macOS ARM64, Linux x64 and Windows ARM64. Dispatch with an unused
`major.minor.patch` version:

```sh
gh workflow run release.yml --repo katasec/forge-mcl --ref main -f version=0.9.2
```

The workflow publishes automatically after all three native builds and help/version checks pass.
Each `forge-<rid>.zip` contains
the entire Native AOT publish output, including native sidecars, and has a `.zip.sha256`
checksum. Extract the whole archive and keep those files beside `forge` (`forge.exe` on Windows).
macOS retains the existing Homebrew runtime prerequisites:

```sh
brew install openssl@3 brotli
```

These archives still depend on their supported operating-system libraries; a missing Homebrew
dylib produces a loader error and is recovered by installing those prerequisites. The macOS binary
is ad-hoc signed, not Developer ID signed or notarized.

Authenticated download, for example:

```sh
gh release download v0.9.2 --repo katasec/forge-mcl --pattern 'forge-osx-arm64.zip*' --dir downloads
cd downloads
shasum -a 256 -c forge-osx-arm64.zip.sha256
unzip forge-osx-arm64.zip -d forge-osx-arm64
./forge-osx-arm64/forge --version
```

Existing tags, releases and assets are never overwritten. A failed native build prevents
publication. GitHub CLI uploads all six assets before publishing; create/upload errors fail the
job. Recover through a safe failed-job rerun or a new version, without deleting or forcing existing
release state. The original repository's history and releases are preserved.

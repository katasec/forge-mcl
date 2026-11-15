# forge-mcl

`forge-mcl` owns the Mission Control Language runtime: parsing, provider-neutral execution,
provider adapters, web search, generic OCI registry pulls, and the Native AOT `forge` CLI
(`forge run` against Forge cloud, `forge publish`, and the `forge mcp` wrapper).

It deliberately does not own a mission catalog, baked mission fallback, hosted Runner,
conversations, platform accounts, or billing. Those remain consumer-owned concerns.

## Build

```sh
dotnet build ForgeMission.slnx
dotnet test ForgeMission.slnx
dotnet run --project src/ForgeMission.Cli -- --help
```

Use `make install` to publish and verify the current-platform Native AOT CLI locally. For a faster
local iteration build, `make install-self-contained` publishes a self-contained, single-file, non-NativeAOT CLI, embedding native libraries and content for extraction at startup.
Local and CI builds share the PowerShell scripts behind Make; install PowerShell 7, Make, Git and .NET 10.
Full Git history and CLI version tags are required. Local builds print
`X.Y.Z-dev.N+<commit>` (plus `.dirty` for uncommitted changes), based on the nearest reachable
CLI tag and the number of commits since it. Rebuilding a clean commit keeps the same identity.

## Run a mission

```sh
forge login
forge project create              # writes forge.project.json in this folder
forge init mission.mcl
forge run mission.mcl "<prompt>"  # declare the mission as <Mission>@1 in forge.project.json first
```

The mission reasons in the cloud. Hands is on: agent experts can Read/Write/Edit files inside the
project folder, and nowhere else; terminal tools are not granted. Without a `forge.project.json`
(or `--project <file>`), `forge run` stops with a message. See
[run boundaries](src/ForgeMission.Cli/README.md#important-flows-and-constraints).

## CLI releases

The [Release CLI workflow](.github/workflows/release.yml) runs automatically when a PR merges into
`main`. It reserves one immutable `vX.Y.Z` tag at the exact merge commit and calls the same Make
build path for macOS ARM64, Linux x64, Linux ARM64 and Windows ARM64. By default each merge bumps
minor and resets patch; label a bug-fix PR `release:patch` before merging to bump patch instead.
Major bumps are never automatic: `release:major` stops with an operator-approval requirement;
a deliberate major-release operation requires a separately approved change.

The workflow publishes after all four native builds and help/version checks pass. A released
binary prints `X.Y.Z+<full-commit>`, matching its tag and source. Ordinary CI and laptop builds
retain the development suffix, even when building an exact release tag. The scripts under
[`scripts/`](scripts/README.md) own all CLI version/build/release logic; affected Make targets
and workflow steps only delegate to them.
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
gh release download v0.9.3 --repo katasec/forge-mcl --pattern 'forge-osx-arm64.zip*' --dir downloads
cd downloads
shasum -a 256 -c forge-osx-arm64.zip.sha256
unzip forge-osx-arm64.zip -d forge-osx-arm64
./forge-osx-arm64/forge --version
```

Tags and published releases/assets are never overwritten. Releases run serially without cancelling
queued merges. A failed native build leaves its reserved tag and prevents publication; rerun the
original workflow to reuse that tag/source. Uploads stage in a draft: retries replace the complete
eight-asset draft set, verify downloaded checksums, then publish. A successful-release rerun
verifies the published set and makes no changes. An older source never replaces a newer source as
the latest release. The original history and releases are preserved.

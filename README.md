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

Use `make install` to publish and verify the current-platform Native AOT CLI locally. Local and
CI builds share the PowerShell scripts behind Make; install PowerShell 7, Make, Git and .NET 10.
Full Git history and CLI version tags are required. Local builds print
`X.Y.Z-dev.N+<commit>` (plus `.dirty` for uncommitted changes), based on the nearest reachable
CLI tag and the number of commits since it. Rebuilding a clean commit keeps the same identity.

## Run a local mission

```sh
forge init mission.mcl
forge run mission.mcl
```

Every run includes Hands. Agent experts can Read/Write/Edit files inside the current working
directory; invoking the command grants this file access in interactive and scripted runs.
Selecting a mission in another folder does not change that workspace. Terminal tools are not
granted. See [local run boundaries](src/ForgeMission.Cli/README.md#important-flows-and-constraints).

## Terminal clipboard component

[Forge Terminal Extensions](src/ForgeMission.Terminal.Extensions/README.md) owns truthful clipboard
results and fixed native text context menus. The independently packable
`Katasec.Forge.Terminal.Extensions` package uses exactly XenoAtom.Terminal.UI 3.10.0 and
XenoAtom.Terminal 2.2.0; the CLI owns selection coordination, snippet composition and presentation.
Run `make verify-terminal-extensions-package` before its merged-ref publication workflow.

## Create a chat project

```sh
forge login
forge project create       # initialize the current existing folder
forge chat
```

Use `forge project create <folder>` for another existing folder, then
`forge chat --project <folder>/forge.project.json`. Chat accepts only a project file path;
the file may have any name. Omitting `--project` opens `./forge.project.json`. A directory or a missing
file stops with an error.
On an interactive Mac, the installed native `forge chat` opens a dedicated Ghostty window after
its startup checks. Cmd+A selects all in the focused composer or `/edit` editor; quitting closes
the window. Ordinary terminal tabs keep their shortcuts. Additional tabs in the dedicated
instance also run Forge. A graceful chat error stays visible until you press a key to close it.
The dedicated instance uses normal top-level Ghostty display configuration but excludes recursive
`config-file` includes to keep its command and shortcut scoped. No shared config file is changed.
Piped chat and other operating systems retain their existing behaviour; the dedicated Mac launch
requires the native executable rather than `dotnet run`.

Creation writes `forge.project.json` and creates one hosted plain Chat conversation. Rerunning
the same command safely retries setup with the same Project and conversation identities.

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

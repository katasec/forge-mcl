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

The manual [Release CLI workflow](.github/workflows/release.yml) builds the exact dispatched
`main` commit for macOS ARM64, Linux x64, Linux ARM64 and Windows ARM64. Dispatch with an unused
`major.minor.patch` version:

```sh
gh workflow run release.yml --repo katasec/forge-mcl --ref main -f version=0.9.3
```

The workflow publishes automatically after all four native builds and help/version checks pass.
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

Existing tags, releases and assets are never overwritten. A failed native build prevents
publication. GitHub CLI uploads all eight assets before publishing; create/upload errors fail the
job. Recover through a safe failed-job rerun or a new version, without deleting or forcing existing
release state. The original repository's history and releases are preserved.

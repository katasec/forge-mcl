# CLI build and release scripts

These scripts own the portable CLI build process. Make is the entry point on a laptop and in
GitHub Actions. PowerShell 7, Git, Make and .NET 10 are prerequisites; native OS libraries remain
as documented in the repository README. Release transport additionally uses the existing `gh`.

| Script | Owns |
|---|---|
| `version.ps1` | CLI tag filtering, Git development identity, exact official identity and minor/patch calculation |
| `build.ps1` | Managed/native build, warning and help/version checks, native signing, complete payload install and ZIP/checksum |
| `release.ps1` | Merged-PR validation, immutable tag reservation, draft staging, publication and rerun rules |
| `tests.ps1` | Disposable real-Git fixtures with fake GitHub transport; no remote GitHub mutations |

| Command | Result |
|---|---|
| `make build`, `make test`, `make clean` | Existing solution operations |
| `make install` | Detect host RID, publish/verify, install entire native payload into user-home `.local/bin` |
| `make cli-package` | Detect host RID, publish/verify and create `dist/cli/forge-<rid>.zip` and checksum |
| `make cli-verify` | Existing managed checks and native macOS verification; retains the two physical-UI test exclusions |
| `make cli-script-test` | Focused version/release failure-boundary checks |
| `make release-prepare`, `make release-publish` | Operations used by the merge workflow; never infer release status solely from running in CI |

`RID` optionally selects the current host's target; native payload verification requires running
on that target. `CLI_OUTPUT` selects a dedicated output directory. `RELEASE_TAG` selects official
identity only when it names a valid CLI tag at the current clean commit. Otherwise identity is
development since the nearest reachable CLI tag. These environment values are read by scripts,
without Make or YAML version arithmetic. Ordinary builds require full history and tags; fetch
missing history rather than falling back to the SDK version.

Preparation consumes the frozen `GITHUB_EVENT_PATH` for a merged PR into `main` and emits job
outputs via `GITHUB_OUTPUT`. Publication consumes its `RELEASE_TAG`/`SOURCE_SHA`, all eight
downloaded assets in `CLI_OUTPUT`, and the scoped `GH_TOKEN`. Only unpublished draft assets may be
replaced. Package versions and unrelated component publication targets remain independent.

Native `install` preserves the existing visible host-linker diagnostics on the maintainer's
macOS 27/Homebrew setup; compiler warnings still fail through `-warnaserror`. Native verification
and packaging additionally reject all raw compiler/linker warnings. This temporary install-only
exception is recorded in Phase 75 and is removed when those supported prerequisites link cleanly.

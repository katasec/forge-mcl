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

Normal `cli-verify` and `cli-package` also publish and run the public-Core-API native exec probe
on the current host. It checks literal arguments/cwd, declined stdin, concurrent bounded
stdin/stdout/stderr pressure with validated JSON, actual PipelineRunner workspace/input/runtime
bindings and inherited/authored FORGE environment, early-root-exit descendants, timeout/cancellation
and unrelated .NET child ownership. Logs and the probe publish live in the
verification output beside the CLI logs; the probe is excluded from the shipped CLI ZIP.
It independently cancels a blocked BCL anonymous-pipe read and a filled blocked write while
their opposite endpoints stay open, requiring cancellation completion within five seconds before
peer closure. Failure cleanup closes only probe-owned endpoints and joins pending operations.
The pressure child finishes 1,000,000-byte stdout and 64 KiB stderr before reading 2,000,000-byte
stdin, so an input-first sequential parent cannot pass. Both drains are exercised under their caps.
The normal Linux x64 GitHub Actions package gate additionally starts published Runner0.20.6 by
immutable GHCR index `sha256:c0031d451d046d4f28a75ca7f7c7f26169b26e92555de4b601128a005670331c`,
with its normal entrypoint unchanged and no `--init`. Docker exec runs the readonly mounted probe
after verifying actual tini PID1/dotnet Runner child. Core retains/reaps its direct root; init reaps
orphans. The probe separately observes no late sentinel and bounded process-entry disappearance,
and preserves unrelated .NET child exit ownership. A separate overridden-entrypoint negative
proves bare Linux PID1 refuses exec before child launch. Docker/image/probe failures fail the gate.
Image/source identity and positive logs are in `exec-init-*`, negative proof in
`exec-pid1-refusal.log`; these controlled library facts do not close published-package/installed
default acceptance or later generic cloud execution.

After a failed macOS native probe, `build.ps1` polls the user and system DiagnosticReports
directories for at most ten seconds. It copies only `.ips` reports identifying
`ForgeMission.Exec.Probe` whose recorded process launch is at or after that probe invocation.
Older processes' delayed reports are excluded even if their files are new. Reports and a scoped
collection-status JSON live under `exec-probe-crashreports` in the existing verification output;
the canonical workflow's existing always-upload artifact retains them. The release matrix still
uploads only successful CLI ZIPs. Missing reports and collection errors remain visible, and
collection failure never replaces the original probe failure. The failed test is not retried.
After the macOS crash is diagnosed, review whether this small verification-owned collector should
remain; it must not expand into general report or environment collection.

# Private package release

`Katasec.Forge.Mcl.*` and `Katasec.Forge.Docker` release only from an annotated `mcl-v*` tag
through `Release private packages`. Each tag has one checked-in seven-package release train at
`eng/release/trains/<tag>.json`; that manifest supplies every package ID, project, MSBuild version
property, and immutable version. Pull-request CI can build and pack but has no package-write
permission and cannot invoke this tag-only workflow.

The release workflow requires the tag to peel to the current `origin/main`, runs provenance,
boundary, locked restore, manifest-versioned build, deterministic tests, package metadata and
compiled-assembly-version checks, and Linux Native AOT publication with those same manifest
properties. It validates each package's ID, version, repository URL, and repository commit before
publication. It refuses every existing package version, never uses duplicate suppression, downloads
every remote package after publication or recovery, requires its SHA-256 to equal the validated
local package byte, and only then emits a SHA-256 manifest as the private GitHub Release asset.

## Retained failed train and next candidate

`mcl-v1.0.0` is a retained, burned annotated tag. Its [release workflow run](https://github.com/katasec/forge-mcl/actions/runs/35735356033)
failed in tag resolution before restore, build, pack, AOT, package publication, release creation,
consumer grants, or release evidence. It must never be retagged: retaining it preserves the exact
failed-event audit trail and prevents one immutable label from naming multiple source/workflow
states. No package or consumer contract exists for that tag.

`mcl-v1.0.1` is the next additive candidate: Parser, Core, ChatClients, Scout, MissionRegistry,
and Serve are each `1.0.1`; Docker is `0.1.1`. Its checked-in train supplies exact internal
`[1.0.1]` pins. It changes no default source version and has no consumer impact until the workflow
publishes the complete private set and grants a named consumer access.

## Temporary tag-integrity compensation

On 2026-09-22 GitHub returned HTTP 403 for both rulesets and branch-protection reads on this
private organization repository because the current organization plan does not provide those
features. A protected-tag rule therefore cannot be applied today.

This is a bounded Type-2 compensation, not a claim that the tag is GitHub-protected: release
integrity is currently the annotated tag, its exact peeled `main` commit, private immutable package
versions, per-package SHA-256 manifest, repository-commit nuspec metadata, and release workflow
permissions. No consumer receives access until the packages and manifest are verified. When
GitHub rulesets or tag protection become available, replace this compensation with a protected
`mcl-v*` tag rule before a later package release; existing package or consumer contracts do not
change.

The workflow checks out a neutral default-branch tree, fetches the remote release tag only into
`refs/tags/release-validation/<tag>`, and validates that shadow ref before checking out its peeled
commit. It never fetches tags into or rewrites the checkout's canonical `refs/tags/<tag>` ref. The
fixture covers the checkout-like state that caused the retained failure and rejects lightweight tags.

## Failure and rollback

GitHub Packages cannot atomically publish a set of NuGet packages. The workflow validates the
complete train before its first push and grants no consumer access. An all-absent train publishes,
then requires every downloaded remote byte to SHA-256 match its local package; an all-present train
performs that same local-to-remote byte equality before recreating only missing release evidence; a
partial train fails without another push. Do not delete or overwrite a partial
train. Correct the source if needed and add a new additive train such as `mcl-v1.0.1`, with new
exact internal dependency pins, before any consumer cutover. Consumer rollback stays at its previous
package-pinned commit or deployable image and never recreates a cross-repository source reference.

# Private package release

`Katasec.Forge.Mcl.*` and `Katasec.Forge.Docker` release only from the `mcl-v1.0.0` annotated
tag through `Release private packages`. Pull-request CI can build and pack but has no package-write
permission and cannot invoke this tag-only workflow.

The release workflow requires the tag to peel to the current `origin/main`, runs provenance,
boundary, locked restore, build, deterministic tests, package metadata checks, and Linux Native
AOT publication, then validates each package's ID, version, repository URL, and repository commit
before publication. It refuses every existing package version, never uses duplicate suppression,
and emits a SHA-256 manifest as the private GitHub Release asset.

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

## Failure and rollback

GitHub Packages cannot atomically publish a set of NuGet packages. The workflow validates the
complete set before its first push and grants no consumer access. If a later immutable push fails,
the successful subset remains unused; do not delete or overwrite it. Correct the source and release
a new additive version before any consumer cutover. Consumer rollback stays at its previous
package-pinned commit or deployable image and never recreates a cross-repository source reference.

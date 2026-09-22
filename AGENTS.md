# Forge MCL working rules

Read this file before changing the repository. Forge MCL owns language semantics, provider and
retrieval adapters, OCI mission-package retrieval, Docker support, serving wire mapping, and the
Native AOT CLI. It does not own application state, Desktop supervision, Runner hosting, Rooms,
conversations, billing, product missions, or deployment topology.

Keep the CLI a thin composition surface. Preserve Native AOT safety: source-generated JSON,
explicit reflection preservation only where justified, and no new warning suppression without an
explanation. Private consumers use versioned packages or named contracts; no cross-repository
`ProjectReference`, shared datastore, or credential sharing is permitted.

Start implementation on a `codex/` branch. Run provenance verification, locked restore, build,
test, pack, and affected AOT publish before requesting review. PR workflows do not publish.

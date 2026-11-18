# Phase 26 — Spoke 4: LSP Server

## Status: Todo

## What it delivers

A Language Server Protocol server for MCL — providing completion, hover, go-to-definition, and diagnostics in any LSP-compatible editor (VS Code, Neovim, JetBrains, Zed).

## Workspace awareness

The LSP is workspace-aware, not file-aware. To serve completions for `mission.mcl` it must also read:

- `forge.toml` — OCI expert declarations
- `experts/` directory — local expert markdown files
- `~/.forge/experts/` — globally cached experts

This mirrors the two-file model: the LSP has the same view of experts that `forge run` has.

## Features

### Completion

| Trigger | Completions offered |
|---------|-------------------|
| After `->` or inside `parallel { }` | Expert names from local `experts/` + `forge.toml` + global cache |
| After `with {` | Variable names declared in the expert's `{{key}}` placeholders |
| After `let` | Nothing (free identifier) |
| After `provider =` in `with { }` | Named profiles from `forge.toml` |

### Hover

Hovering over an expert name shows the expert's `input`, `output`, and first paragraph of its system prompt — pulled from `expert.md`.

```
KubernetesArchitect
────────────────────
Input:  Task description
Output: Kubernetes architecture design

You are a senior Kubernetes architect...
```

### Go-to-definition

`F12` on an expert name opens `experts/<Name>/expert.md` (local) or the cached `expert.md` in `~/.forge/experts/`. Cross-file navigation, no compilation step.

### Diagnostics

| Condition | Severity |
|-----------|----------|
| Expert name not found locally or in `forge.toml` | Error |
| Expert in `forge.toml` not in `mcl.lock` (needs `forge init`) | Warning |
| Provider name in `with { provider = "x" }` not in `forge.toml` | Error |
| More than one `mission` declaration in a file | Error |

## Implementation notes

- Build as a standalone .NET executable — LSP servers run as a subprocess, editor communicates via stdin/stdout
- Use `OmniSharp.Extensions.LanguageServer` or `Microsoft.VisualStudio.LanguageServer.Protocol` for the LSP wire protocol
- Use the Tree-sitter parse tree (Spoke 3) for position-aware queries — do not re-parse with ANTLR on every keystroke
- Verify AOT-safety if shipping as a native binary alongside `forge`

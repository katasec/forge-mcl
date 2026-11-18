# Phase 26 — Spoke 3: Tree-sitter Grammar

## Status: Todo

## What it delivers

An incremental, error-tolerant parser for MCL. Tree-sitter parses the file as you type — producing a valid partial AST even when the file has syntax errors. Used by Neovim, GitHub code navigation, and VS Code (via tree-sitter extension).

This is the prerequisite for a high-quality LSP — the LSP can use the Tree-sitter parse tree for position-aware queries rather than re-parsing on every keystroke.

## Why Tree-sitter over just ANTLR

ANTLR is the authoritative grammar for the compiler. Tree-sitter is a separate implementation optimised for editor use:

| | ANTLR | Tree-sitter |
|--|-------|------------|
| Error recovery | Limited | Strong — continues past errors |
| Incremental re-parse | No | Yes — only re-parses changed region |
| Query language | No | Yes — pattern matching over the parse tree |
| Editor integration | No | Native (Neovim, GitHub, VS Code) |

The two grammars must be kept in sync. ANTLR is the source of truth; the Tree-sitter grammar mirrors it.

## Deliverables

- `tree-sitter-mcl/grammar.js` — Tree-sitter grammar definition
- `tree-sitter-mcl/queries/highlights.scm` — highlight queries (replaces TextMate grammar in editors that support Tree-sitter)
- Published as `tree-sitter-mcl` npm package

## Sync discipline

Any grammar change in Phase 25 or later that modifies `FmlGrammar.g4` must also update `grammar.js`. This should be enforced by a CI check that runs `tree-sitter test` against the MCL fixture files.

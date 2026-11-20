---
type: software-component
title: Forge CLI
description: Native-AOT command surface that composes mission authoring, cloud runs, publishing and the MCP wrapper.
resource: src/ForgeMission.Cli
tags: [cli, composition, aot, forge]
---

# Forge CLI

## Purpose

Exposes the `forge` executable and composes existing components into user-facing commands.

## Why this exists

One command surface connects mission setup, cloud runs, publishing and `forge mcp`. The CLI
handles user input and output; the components it calls own language semantics, provider protocols,
and services.

## Owns

- Command registration, arguments, output, and mission-file selection.
- CLI wiring for OCI pulls and pushes, platform sign-in, and the MCP command.
- `forge run` output, including the `--verbose` turn transcript on stderr.

## Does not own

- MCL parsing or execution, provider SDK adaptation, or search.
- Project and mission-authoring rules, durable conversation state, or Bob's capability authority;
  `forge run` and `forge project create` compose those services through `Katasec.Forge.Client`
  from [forge-client](https://github.com/katasec/forge-client).

## Change admission

Change this component for command behavior or composition. Change [Parser](../ForgeMission.Parser/README.md)
for syntax and [Core](../ForgeMission.Core/README.md) for resolution and execution. For provider
construction or search, change the corresponding component. Keep client-service behavior in
forge-client.

## Use these pieces

| Piece | Responsibility |
|---|---|
| [Program](Program.cs) | Entry point: builds the host (settings, HTTP clients, options), registers every command, and owns the one top-level exit-code handler. |
| [CommandActions](Commands/CommandActions.cs) | Sets each command's async action and maps Ctrl+C/SIGTERM to exit code 130. |
| [MissionDevCommands](Commands/MissionDevCommands.cs) | `forge init`, `forge validate`, `forge list experts` and `forge expert init`. |
| [HostedMissionCommands](Commands/HostedMissionCommands.cs) | `forge run` wiring and project-file choice; `forge publish` packs a mission and pushes it to an OCI registry. |
| [ForgeDurableRun](Commands/ForgeDurableRun.cs) | `forge run`: one turn of a mission as a cloud conversation in the project, with Hands. |
| [RunInputs](Commands/RunInputs.cs) | `forge run --input`: parses and checks named inputs and builds the turn's `MissionNamedInputs`. |
| [RunTranscript](Commands/RunTranscript.cs) | `forge run --verbose`: one stderr line per expert step, Hands use, error or unfinished run. |
| [ForgeProject](Commands/ForgeProject.cs) | `forge project create`: writes `forge.project.json` through the shared Client. |
| [AuthCommands](Commands/AuthCommands.cs) | `forge registry login`, `forge login`, `forge whoami` and `forge logout`. |
| [PlatformLogin](Commands/PlatformLogin.cs) | Platform sign-in (loopback + PKCE), key exchange, `whoami` and logout; its wire types are in [PlatformLoginContracts](Contracts/PlatformLoginContracts.cs). |
| [ProviderCommand](Commands/ProviderCommand.cs) | `forge provider list` and `forge provider scaffold` (prints or appends a `forge.toml` provider block). |
| [ProviderCatalog](Commands/ProviderCatalog.cs) | The providers those commands list and scaffold, one per provider `ChatClients` builds. |
| [CleanCommand](Commands/CleanCommand.cs) | `forge clean`: removes cached OCI experts and missions through `ForgeCache.Clear`. |
| [McpCommand](Commands/McpCommand.cs) | `forge mcp`: serves one validated mission over stdio, runners built by [MissionServerBuilder](Services/MissionServerBuilder.cs). |
| [CliHelpers](Services/CliHelpers.cs) | Shared mission loading, parsing, expert loading and validation, the `mcl.lock` path and the expert template. |
| [CliDiagnostics](Services/CliDiagnostics.cs) | Error rendering and `Die`, which throws [ForgeExitException](Services/ForgeExitException.cs) for Program's handler. |
| [CliHttpClients](Services/CliHttpClients.cs) | The named outbound HTTP clients registered on the host. |
| [ForgeSettings](Services/ForgeSettings.cs) | The `~/.forge/config.json` settings (API endpoint, OCI endpoint for short mission selectors) bound by the CLI host. |
| [WebSearchBuilder](Services/WebSearchBuilder.cs) | Optional live-search wiring from xAI/Grok environment keys. |
| [ImmediateProgress](Services/ImmediateProgress.cs) | Shows the shared Client's Hands reports at once, in order. |
| [FreePort](Services/FreePort.cs) | Picks a free loopback port for the sign-in listener. |

## Communicates with

```mermaid
flowchart LR
  User[User or MCP client] -->|forge command| CLI[Forge CLI]
  CLI -->|parse, resolve, run| Core[Mission Core]
  CLI -->|provider profile| Clients[Chat Clients]
  CLI -->|optional search| Scout[Scout]
  CLI -->|OCI pulls and pushes| Registry[Mission Registry]
  CLI -->|forge run, project create| Client[Katasec.Forge.Client]
  Client -->|mission-conversation messages| ForgeApi[ForgeAPI]
```

## Important flows and constraints

- **Run:** `forge run <mission.mcl|oci-ref> "<prompt>"` reads `./forge.project.json`, or the file
  given by `--project <file>`, and starts a new conversation each launch through the Client
  (`StartAsync`, `ProjectConversation.RunTurnAsync`). The run is a cloud conversation in the
  Conversation Host, readable after the CLI exits. A missing project stops with the Client's one
  message naming `forge project create`. The final answer goes to stdout; diagnostics, the
  `--verbose` transcript and the conversation ID go to stderr.
- **Named inputs:** `--input name=value` (text) and `--input name=@file` (one file; a folder is
  refused) are repeatable. A text file's contents become the input, so `{{name}}` is the file's text
  (at most 4 MiB). A binary file (not UTF-8, or containing a NUL byte) is uploaded instead (at most
  100 MiB) for an `exec` step to read. The prompt fills the root mission's own input, so
  `--input` cannot name it. With any `--input`, the turn is submitted as named inputs through the
  Client (`UploadInputAsync`, then `RunTurnAsync` with `MissionNamedInputs`). Every value is checked
  before any network call.
- **Hands:** Always attached by the Client when the conversation opens; there is no flag or
  prompt. Agent experts may Read/Write/Edit inside the project's folders and nowhere else; terminal
  tools are not granted. Hands owns path and symlink containment. Hands reports print on stderr
  as `error: …`.
- **Composition:** `Program` registers the commands; each command file wires existing owners. Provider and search behavior belong in
  [Chat Clients](../ForgeMission.ChatClients/README.md) and [Scout](../ForgeMission.Scout/README.md);
  OCI retrieval belongs in [Mission Registry](../ForgeMission.MissionRegistry/README.md).
- **Project creation:** `forge project create [folder]` writes `forge.project.json` (`folders: ["."]` and a new `projectId`) unless one is already there. The folder defaults to cwd and must exist. It makes no server call and needs no sign-in; forge-client's Projects owner holds the file rules. A rerun keeps the existing Project.
- **Portable files:** `forge.project.json` carries the stable `projectId`, mission/version
  references and relative repository folders.
- **MCP:** `forge mcp <mission.mcl>` runs the mission locally in-process for each tool call; its
  runners come from `forge.toml` profiles, built lazily so startup needs no API key.
- **AOT:** Preserve source-generated JSON and reflection safety.

## Tests

| Area | Start here |
|---|---|
| Run | [ForgeDurableRunTests](../../tests/ForgeMission.Mcl.Tests/Cli/ForgeDurableRunTests.cs), [RunTranscriptTests](../../tests/ForgeMission.Mcl.Tests/Cli/RunTranscriptTests.cs) |
| Commands and config | [CLI test directory](../../tests/ForgeMission.Mcl.Tests/Cli) |

## Related documentation

- [Architecture](https://github.com/katasec/mission-control-language/blob/main/docs/design/architecture.md)
- [MCL language](https://github.com/katasec/mission-control-language/blob/main/docs/design/language.md)
- [Conversation flow](https://github.com/katasec/mission-control-language/blob/main/docs/design/how-conversations-work.md)
- [Build and install](../../README.md#build)

---
type: software-component
title: Forge CLI
description: Native-AOT command surface that composes existing mission, provider, Docker, and serving components.
resource: src/ForgeMission.Cli
tags: [cli, composition, aot, forge]
---

# Forge CLI

## Purpose

Exposes the `forge` executable and composes existing components into user-facing commands.

## Why this exists

People need one command surface for mission lifecycle, execution, serving, and supported local helpers. Keeping that surface thin prevents command handling from becoming a second owner of language, provider, or runtime behavior.

## Owns

- The executable entry point and command registration in [`Program`](Program.cs).
- Command input/output, mission-file selection, and composition of Core, Chat Clients, Docker, Scout, and Serve.
- CLI-specific OCI pulls, platform sign-in, built-in mission references, and MCP command wiring.
- The `forge chat` loop: the order of existing `Katasec.Forge.Client` calls and what is printed.

## Does not own

- MCL syntax or execution semantics, provider SDK adaptation, Docker process protocol, web-search implementation, or HTTP wire mapping.

## Change admission

A change belongs here only if it advances the `forge` command surface or composes existing owners. For mission behavior change `ForgeMission.Core`; for provider construction, Docker control, search, or HTTP serving change the corresponding component rather than duplicating it in a command.

## Use these pieces

- [`Program`](Program.cs) registers every command and is the executable entry point.
- [`ForgeChat`](ForgeChat.cs) is `forge chat`: it opens the default Project (`~/Forge/Projects/chat`), publishes the naked `Chat` mission (`StarterMissions.ChatDefinition`: one expert `using anthropic`) on first use, reopens the latest mission conversation only when it is on Chat (otherwise creates one on Chat; Janus conversations stay stored), and runs a plain type-and-print loop, all through `ApplicationComposition` from `Katasec.Forge.Client`. It adds no ForgeAPI client of its own.
- [`ForgeExec`](ForgeExec.cs) is the shared CLI execution helper; [`ProviderClientBuilder`](ProviderClientBuilder.cs) wires optional live search.
- [`ChatClients`](../ForgeMission.ChatClients/ChatClients.cs), [`ForgeServe`](../ForgeMission.Serve/ForgeServe.cs), and [`DockerCli`](../ForgeMission.Docker/DockerCli.cs) are composed owners.
- [`MissionFileResolutionTests`](../../tests/ForgeMission.Mcl.Tests/Cli/MissionFileResolutionTests.cs) covers CLI mission-file defaulting.

## Communicates with

```mermaid
flowchart LR
  User[User or MCP client] -->|forge command| CLI[Forge CLI]
  CLI -->|parse, resolve, run| Core[Mission Core]
  CLI -->|provider profile| Clients[Chat Clients]
  CLI -->|serve composition| Serve[Forge Serve]
  CLI -->|local container commands| Docker[Docker support]
  CLI -->|optional search| Scout[Scout]
  CLI -->|forge chat| Client[Katasec.Forge.Client]
  Client -->|mission-conversation messages| ForgeApi[ForgeAPI]
```

## Important flows and constraints

- The published executable is Native AOT; preserve source-generated and reflection-safety requirements in its dependency graph.
- `Program` is composition code, not a home for provider or pipeline business logic.
- CLI defaults and command semantics are part of the public product surface; keep errors and argument handling explicit.

## Related documentation

- [CLI architecture](../../docs/design/architecture.md)
- [MCL language](../../docs/design/language.md)
- [Release workflow](../../AGENTS.md#release-workflow)

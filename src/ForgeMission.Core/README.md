---
type: software-component
title: Mission Core
description: Provider-neutral MCL parsing, resolution, pipeline execution, and reusable capability contracts.
resource: src/ForgeMission.Core
tags: [mcl, execution, contracts, capabilities]
---

# Mission Core

## Purpose

Supplies the provider-neutral implementation of MCL mission loading, resolution, and execution, plus reusable contracts and primitives shared by hosts.

## Why this exists

Mission semantics must be executable without coupling the language runtime to a particular provider, executable host, or Desktop domain. This project keeps those durable semantics and shared primitives in one AOT-compatible library.

## Owns

- Expert definitions and loading, mission/lock-file resolution, and `forge.toml` manifest reading.
- Pipeline execution, trace/result contracts, and the provider-neutral [`IExpertRunner`](Runtime/IExpertRunner.cs) seam.
- Reusable workspace and capability contracts, dispatch primitives, and tool executors.

## Does not own

- Provider SDK selection or credentials, command-line UX, HTTP serving, Project/conversation state, or Desktop process supervision.
- The scoped local-capability authority: Client Runtime (Bob) composes these capability primitives and owns its policy, admission, confirmation, audit, and lifetime.

## Change admission

A change belongs here only if it advances provider-neutral MCL execution, resolution, or a reusable contract/primitives boundary. For provider construction change `ForgeMission.ChatClients`; for scoped local capability authority compose with `ForgeMission.ClientRuntime`.

## Use these pieces

- [`PipelineRunner`](Runtime/PipelineRunner.cs) executes a parsed mission with resolved experts and named runners; its root-scoped continuation seam pauses and resumes declared nested tool requests without holding capability authority.
- [`PipelineRunOptions.StreamLlmDeltas`](Runtime/PipelineRunOptions.cs) (set only by a durable executor) streams each tool-free, non-judge llm step and emits its chunks as `PipelineStepDelta` trace facts; the step completes with its plain text as a pass. Judges, steps with tools attached, and parallel steps keep `RunAsync`. One helper in `PipelineRunner` decides streaming for the recursive and root-scoped paths.
- [`PipelineRunOptions.ChatHistory`](Runtime/ChatHistory.cs) carries a durable chat's earlier turns as user/assistant messages; every llm step of the root mission (both pipeline paths) sends `system + history + user(step input)`; step 1's input is the root mission's first declared parameter (the new message), not "Begin.". Child missions get none, it is never checkpointed (pass it again on resume), and a tool step with a client `conversation` as well is refused. `context["conversation"]` (forge serve) is unchanged.
- [`IExpertRunner`](Runtime/IExpertRunner.cs) is the only runner abstraction used by the pipeline.
- [`ExpertResolver`](Resolution/ExpertResolver.cs), [`ExpertLoader`](Experts/ExpertLoader.cs), and [`ForgeTomlReader`](Manifest/ForgeTomlReader.cs) provide the resolution inputs.
- [`DurableMissionPackageValidator`](Runtime/DurableMissionPackageValidator.cs) is the single in-memory parser/validator for bounded durable package content; it never reads a runner image directory or TOML provider profile. A durable step may select only a name in its fixed `ProviderProfiles` set (the deployment binds each name), and a package runs every llm step on one profile, reported as `ProviderProfile`.
- [`PipelineRunnerTests`](https://github.com/katasec/forge-mcl/blob/main/tests/ForgeMission.Mcl.Tests/Runtime/PipelineRunnerTests.cs) and [`CapabilityDispatcherTests`](https://github.com/katasec/forge-mcl/blob/main/tests/ForgeMission.Mcl.Tests/Tools/CapabilityDispatcherTests.cs) cover execution and capability contracts.

## Communicates with

```mermaid
flowchart LR
  Source[.mcl source] --> Parser[ForgeMission.Parser]
  Parser -->|AST| Core[Mission Core]
  Clients[ForgeMission.ChatClients] -->|builds IExpertRunner| Core
  Core -->|IWebSearch| Scout[ForgeMission.Scout]
  Core -->|MissionResult and trace| Host[CLI / forge-runner]
```

## Important flows and constraints

- The parser only establishes syntax; expert resolution is eager and fails before execution.
- The pipeline selects a named runner per `using` profile and never takes provider-specific types.
- AOT callers must retain source-generated JSON and the existing reflection-preservation rules; do not introduce runtime reflection as a convenience.

## Related documentation

- [Architecture](https://github.com/katasec/mission-control-language/blob/main/docs/design/architecture.md)
- [Language design](https://github.com/katasec/mission-control-language/blob/main/docs/design/language.md)
- [Phase 43.23 ownership end state](https://github.com/katasec/mission-control-language/blob/main/docs/retrospectives/phase-43-domain-ownership/end-state.md)

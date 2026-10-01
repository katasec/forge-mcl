---
type: software-component
title: Chat Clients
description: Provider-SDK boundary that builds Microsoft.Extensions.AI clients and Core runners from provider profiles.
resource: src/ForgeMission.ChatClients
tags: [providers, llm, adapters, aot]
---

# Chat Clients

## Purpose

Builds provider-backed `IChatClient` and `IExpertRunner` instances from Core provider profiles.

## Why this exists

Provider SDKs and their protocol differences must stay below MCL execution. This boundary lets Core depend on provider-neutral contracts while one focused project translates supported provider configuration.

## Owns

- [`ChatClients.Build`](ChatClients.cs) and [`ChatClients.BuildChatClient`](ChatClients.cs) for supported profile values.
- OpenAI-compatible OpenAI/Azure, Ollama, and xAI client construction.
- Anthropic response-format adaptation in [`AnthropicResponseFormatChatClient`](ChatClients.cs).
- Anthropic plain-text streaming with token usage in [`AnthropicTextStream`](AnthropicTextStream.cs).

## Does not own

- MCL parsing, profile discovery, command-line configuration, mission execution policy, or web-search configuration.

## Change admission

A change belongs here only if it advances construction or protocol adaptation for a supported chat provider behind Core contracts. For profile discovery change `ForgeMission.Core`; for CLI wiring change `ForgeMission.Cli`.

## Use these pieces

- [`ChatClients`](ChatClients.cs) is the public factory used by CLI and mission-serving hosts.
- [`ProviderProfile`](../ForgeMission.Core/Manifest/ForgeManifest.cs) is the incoming profile shape.
- [`DirectExpertRunner`](../ForgeMission.Core/Adapters/DirectExpertRunner.cs) is the Core adapter returned by `Build`.
- [`DirectExpertRunnerTests`](https://github.com/katasec/forge-mcl/blob/main/tests/ForgeMission.Mcl.Tests/Adapters/DirectExpertRunnerTests.cs) cover the provider-neutral runner boundary.

## Communicates with

```mermaid
flowchart LR
  Profile[Core ProviderProfile] --> Factory[ChatClients]
  Factory -->|IChatClient| Runner[Core DirectExpertRunner]
  Factory -->|provider SDK calls| Providers[OpenAI / Anthropic-compatible endpoints]
  Runner -->|IExpertRunner| Host[CLI / forge-runner]
```

## Important flows and constraints

- Provider names are normalized by the factory; unknown values fail explicitly.
- Ollama and xAI use the OpenAI-compatible client with their own default endpoints.
- Anthropic structured output is translated at this boundary; do not leak native provider types into Core.
- Every Anthropic call sends `MaxOutputTokens` 4096 unless the caller sets one; the SDK otherwise sends 250, which cut streamed and tool-mode replies short.
- A plain-text Anthropic stream (no tools, text-only messages) is read from the native event stream: the SDK's streaming adapter drops usage, so it would bill 0 tokens. Tool-mode streaming still uses the SDK adapter and reports no usage or tool calls.

## Related documentation

- [Provider boundary and supported clients](https://github.com/katasec/mission-control-language/blob/main/docs/design/architecture.md)
- [Code style (zero warnings)](https://github.com/katasec/mission-control-language/blob/main/docs/design/code-style.md#rules)

using ForgeMission.Core.Adapters;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Runtime;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace ForgeMission.Tests.Adapters;

/// <summary>
/// Unit tests for DirectExpertRunner role-based pass enforcement.
/// Uses a stub IChatClient — no LLM required.
/// </summary>
public class DirectExpertRunnerTests
{
    // Stub that returns a predetermined StepEnvelope JSON response.
    private sealed class StubChatClient(string status, string text = "stub output") : IChatClient
    {
        public ChatClientMetadata Metadata => new("stub", null, null);
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());
            var json = status == "fail"
                ? $$$"""{"text":"{{{text}}}","status":"fail","reason":"stub reason"}"""
                : $$$"""{"text":"{{{text}}}","status":"pass"}""";

            var msg = new ChatMessage(ChatRole.Assistant, json);
            return Task.FromResult(new ChatResponse([msg]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Stream(messages);

        private async IAsyncEnumerable<ChatResponseUpdate> Stream(IEnumerable<ChatMessage> messages)
        {
            Requests.Add(messages.ToList());
            yield return new ChatResponseUpdate(ChatRole.Assistant, "{\"text\":\"stub output\",\"status\":\"pass\"}");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static ExpertDefinition CriticExpert() =>
        new("PitchCritic", "draft", "critique", "You are a critic.");

    private static ExpertDefinition JudgeExpert() =>
        new("QualityJudge", "explanation", "verdict", "You are a judge.", Role: "judge");

    private static Dictionary<string, object> EmptyContext() =>
        new Dictionary<string, object> { ["output"] = "some input" };

    [Fact]
    public async Task NonJudge_LlmReturnsFail_RunnerForcesPass()
    {
        var runner   = new DirectExpertRunner(new StubChatClient("fail"));
        var envelope = await runner.RunAsync(CriticExpert(), EmptyContext());

        Assert.Equal("pass", envelope.Status);
        Assert.Null(envelope.Reason);
    }

    [Fact]
    public async Task NonJudge_LlmReturnsPass_RunnerKeepsPass()
    {
        var runner   = new DirectExpertRunner(new StubChatClient("pass"));
        var envelope = await runner.RunAsync(CriticExpert(), EmptyContext());

        Assert.Equal("pass", envelope.Status);
    }

    [Fact]
    public async Task Judge_LlmReturnsFail_RunnerPreservesFail()
    {
        var runner   = new DirectExpertRunner(new StubChatClient("fail"));
        var envelope = await runner.RunAsync(JudgeExpert(), EmptyContext());

        Assert.Equal("fail", envelope.Status);
        Assert.Equal("stub reason", envelope.Reason);
    }

    [Fact]
    public async Task Judge_LlmReturnsPass_RunnerKeepsPass()
    {
        var runner   = new DirectExpertRunner(new StubChatClient("pass"));
        var envelope = await runner.RunAsync(JudgeExpert(), EmptyContext());

        Assert.Equal("pass", envelope.Status);
    }

    [Fact]
    public async Task History_ReplacesSingleUserMessage_WithRecipientRelativeTranscript()
    {
        var transcript = new SpeakerTranscript();
        transcript.Add("PitchCritic", "initial draft");
        transcript.Add("QualityJudge", "add concrete verification");
        var context = EmptyContext();
        context["history"] = transcript;
        var client = new StubChatClient("pass");

        await new DirectExpertRunner(client).RunAsync(CriticExpert(), context);

        var messages = Assert.Single(client.Requests);
        Assert.Equal([ChatRole.System, ChatRole.Assistant, ChatRole.User], messages.Select(m => m.Role));
        Assert.Equal("initial draft", messages[1].Text);
        Assert.Equal("add concrete verification", messages[2].Text);
        Assert.DoesNotContain(messages, m => m.Text == "some input");
    }

    [Fact]
    public async Task EmptyHistory_PreservesSingleTurnMessageShape()
    {
        var context = EmptyContext();
        context["history"] = new SpeakerTranscript();
        var client = new StubChatClient("pass");

        await new DirectExpertRunner(client).RunAsync(CriticExpert(), context);

        var messages = Assert.Single(client.Requests);
        Assert.Equal([ChatRole.System, ChatRole.User], messages.Select(m => m.Role));
        Assert.Equal("some input", messages[1].Text);
    }

    [Fact]
    public async Task StreamingHistory_UsesRecipientRelativeTranscript()
    {
        var transcript = new SpeakerTranscript();
        transcript.Add("PitchCritic", "initial draft");
        transcript.Add("QualityJudge", "add concrete verification");
        var context = EmptyContext();
        context["history"] = transcript;
        var client = new StubChatClient("pass");

        await foreach (var _ in new DirectExpertRunner(client).StreamAsync(CriticExpert(), context))
        {
        }

        var messages = Assert.Single(client.Requests);
        Assert.Equal([ChatRole.System, ChatRole.Assistant, ChatRole.User], messages.Select(m => m.Role));
    }

    // Phase 53.8: a streamed non-judge step answers in plain text, so its system prompt carries no
    // JSON envelope instruction; a streamed judge keeps its pass/fail envelope instruction.
    [Fact]
    public async Task StreamingNonJudge_SendsNoEnvelopeInstruction()
    {
        var client = new StubChatClient("pass");

        await foreach (var _ in new DirectExpertRunner(client).StreamAsync(CriticExpert(), EmptyContext()))
        {
        }

        var system = Assert.Single(client.Requests)[0];
        Assert.Equal("You are a critic.", system.Text);
    }

    [Fact]
    public async Task StreamingJudge_KeepsEnvelopeInstruction()
    {
        var client = new StubChatClient("pass");

        await foreach (var _ in new DirectExpertRunner(client).StreamAsync(JudgeExpert(), EmptyContext()))
        {
        }

        var system = Assert.Single(client.Requests)[0];
        Assert.StartsWith("You are a judge.", system.Text);
        Assert.Contains("\"status\": \"fail\"", system.Text);
    }

    // ── Phase 58: durable chat history ───────────────────────────────────────────────────────

    private static ChatHistory TwoEarlierTurns() => new(
    [
        new ChatMessage(ChatRole.User, "first question"),
        new ChatMessage(ChatRole.Assistant, "first answer"),
        new ChatMessage(ChatRole.User, "second question"),
        new ChatMessage(ChatRole.Assistant, "(no reply: run failed)"),
    ]);

    private static readonly ChatRole[] HistoryShape =
        [ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.Assistant, ChatRole.User];

    private static Dictionary<string, object> HistoryContext()
    {
        var context = EmptyContext();
        context[ChatHistory.ContextKey] = TwoEarlierTurns();
        return context;
    }

    [Fact]
    public async Task ChatHistory_PrecedesStepInput()
    {
        var client = new StubChatClient("pass");

        await new DirectExpertRunner(client).RunAsync(CriticExpert(), HistoryContext());

        var messages = Assert.Single(client.Requests);
        Assert.Equal(HistoryShape, messages.Select(m => m.Role));
        Assert.Equal("You are a critic.", messages[0].Text);
        Assert.Equal("(no reply: run failed)", messages[4].Text);
        Assert.Equal("some input", messages[^1].Text);
    }

    [Fact]
    public async Task StreamingChatHistory_PrecedesStepInput()
    {
        var client = new StubChatClient("pass");

        await foreach (var _ in new DirectExpertRunner(client).StreamAsync(CriticExpert(), HistoryContext()))
        {
        }

        var messages = Assert.Single(client.Requests);
        Assert.Equal(HistoryShape, messages.Select(m => m.Role));
        Assert.Equal("some input", messages[^1].Text);
    }

    [Fact]
    public async Task ChatHistory_ToolStepWithoutConversation_UsesSameShape()
    {
        var context = HistoryContext();
        context["tools"] = new List<AITool> { AIFunctionFactory.Create(() => "x", "Read") };
        var client = new StubChatClient("pass");

        await new DirectExpertRunner(client).RunAsync(CriticExpert(), context);

        Assert.Equal(HistoryShape, Assert.Single(client.Requests).Select(m => m.Role));
    }

    [Fact]
    public async Task StreamingJudge_WithChatHistory_KeepsInstructionInSystemMessage()
    {
        var client = new StubChatClient("pass");

        await foreach (var _ in new DirectExpertRunner(client).StreamAsync(JudgeExpert(), HistoryContext()))
        {
        }

        var messages = Assert.Single(client.Requests);
        Assert.Equal(HistoryShape, messages.Select(m => m.Role));
        Assert.Contains("\"status\": \"fail\"", messages[0].Text);
    }

    [Fact]
    public async Task ChatHistory_WithSpeakerTranscript_PrecedesTranscript()
    {
        var transcript = new SpeakerTranscript();
        transcript.Add("QualityJudge", "add concrete verification");
        var context = HistoryContext();
        context["history"] = transcript;
        var client = new StubChatClient("pass");

        await new DirectExpertRunner(client).RunAsync(CriticExpert(), context);

        var messages = Assert.Single(client.Requests);
        Assert.Equal(HistoryShape, messages.Select(m => m.Role));
        Assert.Equal("add concrete verification", messages[^1].Text);
    }

    [Fact]
    public async Task ToolStep_WithConversationAndChatHistory_Throws()
    {
        var context = HistoryContext();
        context["tools"] = new List<AITool> { AIFunctionFactory.Create(() => "x", "Read") };
        context["conversation"] = new Conversation([new ChatMessage(ChatRole.User, "client turn")]);
        var client = new StubChatClient("pass");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DirectExpertRunner(client).RunAsync(CriticExpert(), context));
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task StreamingToolStep_WithConversationAndChatHistory_Throws()
    {
        var context = HistoryContext();
        context["tools"] = new List<AITool> { AIFunctionFactory.Create(() => "x", "Read") };
        context["conversation"] = new Conversation([new ChatMessage(ChatRole.User, "client turn")]);
        var client = new StubChatClient("pass");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in new DirectExpertRunner(client).StreamAsync(CriticExpert(), context))
            {
            }
        });
    }

    [Fact]
    public async Task ToolStep_WithConversationOnly_SendsSystemPlusConversationUnchanged()
    {
        var context = EmptyContext();
        context["tools"] = new List<AITool> { AIFunctionFactory.Create(() => "x", "Read") };
        context["conversation"] = new Conversation(
        [
            new ChatMessage(ChatRole.System, "client system"),
            new ChatMessage(ChatRole.User, "client turn"),
        ]);
        var client = new StubChatClient("pass");

        await new DirectExpertRunner(client).RunAsync(CriticExpert(), context);

        var messages = Assert.Single(client.Requests);
        Assert.Equal([ChatRole.System, ChatRole.User], messages.Select(m => m.Role));
        Assert.Equal("You are a critic.", messages[0].Text);
        Assert.Equal("client turn", messages[1].Text);
    }

    [Fact]
    public void ChatHistory_RejectsSystemOrToolMessages()
        => Assert.Throws<ArgumentException>(() => new ChatHistory([new ChatMessage(ChatRole.System, "x")]));
}

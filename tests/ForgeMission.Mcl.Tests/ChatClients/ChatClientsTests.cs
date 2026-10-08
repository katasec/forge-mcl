using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ForgeMission.Core.Adapters;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Runtime;
using ForgeMission.Parser;
using ForgeMission.Core.Manifest;
using Microsoft.Extensions.AI;
using ForgeChatClients = ForgeMission.ChatClients.ChatClients;

namespace ForgeMission.Tests.ChatClients;

public sealed class ChatClientsTests
{
    [Fact]
    public async Task OpenAi_SerializedToolPauses_PreserveCompleteResponses()
    {
        var replies = new[] { ResponsesToolReply("one"), ResponsesToolReply("two"), ResponsesReply("completed", [ResponseText("done")]) };
        var requests = await ServeOpenAiAsync(replies.Select(body => (200, "application/json", body)).ToArray(), async client =>
        {
            var ast = MclParser.Parse("mission Root = { Respond }");
            var experts = new Dictionary<string, ExpertDefinition>
            { ["Respond"] = new("Respond", "any", "text", "Read.", Role: "agent") };
            var runner = new PipelineRunner(new DirectExpertRunner(client));
            var result = await runner.RunAsync(ast, experts, new PipelineRunOptions("Root", RootTools: [ReadTool()]));
            foreach (var id in new[] { "one", "two" })
            {
                var pause = Assert.IsType<PipelineToolPause>(result.Pause);
                Assert.Equal(id, pause.ToolCall.CallId);
                var restored = new PipelineContinuation(pause.Continuation.FormatVersion, pause.Continuation.Payload);
                result = await runner.ResumeAsync(ast, experts, new PipelineResumeRequest(restored,
                    new PipelineToolResult(id, PipelineToolResultStatus.Succeeded, $"result-{id}")), new PipelineRunOptions("ignored"));
            }
            Assert.Null(result.Pause);
            Assert.Equal("done", result.Text);
        });
        Assert.Equal(3, requests.Count); // All exchanges completed before checking the replay regression.
        foreach (var request in requests) AssertResponsesOptions(request);
        var input = requests[^1].GetProperty("input").EnumerateArray().ToList();
        Assert.Equal(new[] { "protected-one", "protected-two" }, input.Where(item => item.GetProperty("type").GetString() == "reasoning")
            .Select(item => item.GetProperty("encrypted_content").GetString()));
        Assert.Equal(new[] { "reason-one", "reason-two" }, input.Where(item => item.GetProperty("type").GetString() == "reasoning")
            .Select(item => item.GetProperty("summary")[0].GetProperty("text").GetString()));
        Assert.Equal(new[] { "text-one", "text-two" }, input.Skip(2).Where(item => item.GetProperty("type").GetString() == "message")
            .Select(item => item.GetProperty("content")[0].GetProperty("text").GetString()));
        Assert.Equal(new[] { "one", "two" }, input.Where(item => item.GetProperty("type").GetString() == "function_call")
            .Select(item => item.GetProperty("call_id").GetString()));
        // The older result is rehydrated as JsonElement and JSON-encoded by the SDK; the newest is a raw string.
        Assert.Equal(new[] { "\"result-one\"", "result-two" }, input.Where(item => item.GetProperty("type").GetString() == "function_call_output")
            .Select(item => item.GetProperty("output").GetString()));
        Assert.Equal(new[] { "one", "two" }, input.Where(item => item.GetProperty("type").GetString() == "function_call_output")
            .Select(item => item.GetProperty("call_id").GetString()));
        Assert.Equal(new[] { "reasoning", "message", "function_call", "function_call_output", "reasoning", "message", "function_call", "function_call_output" },
            input.Skip(2).Select(item => item.GetProperty("type").GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAi_FailedResponse_ThrowsThroughGenericRunner(bool stream)
    {
        var failed = ResponsesReply("failed", [], new { code = "server_error", message = "fixture provider failed" });
        var body = stream ? ResponsesEvent("response.failed", new { type = "response.failed", sequence_number = 0, response = JsonSerializer.Deserialize<JsonElement>(failed) }) : failed;
        var requests = await ServeOpenAiAsync([(200, stream ? "text/event-stream" : "application/json", body)], async client =>
        {
            var runner = new DirectExpertRunner(client);
            var expert = new ExpertDefinition("Respond", "any", "text", "Respond.");
            var context = new Dictionary<string, object>();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                if (!stream) { await runner.RunAsync(expert, context); return; }
                await foreach (var chunk in runner.StreamAsync(expert, context)) Assert.Fail($"Unexpected success output: {chunk}");
            });
            Assert.Equal("fixture provider failed", error.Message);
        });
        Assert.Single(requests);
    }

    [Theory]
    [InlineData("azure")]
    [InlineData("ollama")]
    [InlineData("xai")]
    public async Task ExistingCompatibleProviders_KeepChatCompletions(string provider)
    {
        var request = await CaptureRequestAsync(provider, client =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));
        Assert.True(request.TryGetProperty("messages", out _));
        Assert.False(request.TryGetProperty("input", out _));
    }

    [Fact]
    public async Task OpenAi_ToolFreeStep_UsesClosedStructuredSchema()
    {
        var reply = ResponsesReply("completed", [ResponseText("{\"text\":\"answer\",\"status\":\"pass\",\"reason\":null}")]);
        var requests = await ServeOpenAiAsync([(200, "application/json", reply)], async client =>
        {
            var result = await new DirectExpertRunner(client).RunAsync(
                new ExpertDefinition("Respond", "any", "text", "Respond."), new Dictionary<string, object>());
            Assert.Equal("answer", result.Text);
        });
        var request = Assert.Single(requests);
        var format = request.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("step_envelope", format.GetProperty("name").GetString());
        Assert.False(format.GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
        Assert.False(request.GetProperty("store").GetBoolean());
    }

    [Fact]
    public async Task OpenAi_Stream_PreservesReasoningCallsAndUsage()
    {
        var completed = JsonSerializer.Deserialize<JsonElement>(ResponsesReply("completed", []));
        var body = ResponsesEvent("response.created", new { type = "response.created", sequence_number = 0, response = completed })
            + ResponsesEvent("response.output_text.delta", new { type = "response.output_text.delta", sequence_number = 1, item_id = "msg-stream", output_index = 0, content_index = 0, delta = "hello" })
            + ResponsesEvent("response.output_item.done", new { type = "response.output_item.done", sequence_number = 2, output_index = 1,
                item = new { type = "reasoning", id = "rs-stream", summary = Array.Empty<object>(), encrypted_content = "protected-stream" } })
            + ResponsesEvent("response.output_item.done", new { type = "response.output_item.done", sequence_number = 3, output_index = 2,
                item = new { type = "function_call", id = "fc-stream", call_id = "call-stream", name = "Read", arguments = "{\"path\":\"fixture.txt\"}", status = "completed" } })
            + ResponsesEvent("response.completed", new { type = "response.completed", sequence_number = 4, response = completed });
        var usage = new UsageAccumulator();
        await ServeOpenAiAsync([(200, "text/event-stream", body)], async client =>
        {
            using var tracked = new UsageTrackingChatClient(client, usage);
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in tracked.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Read")], OneToolCallOptions()))
                updates.Add(update);
            var response = updates.ToChatResponse();
            var contents = response.Messages.SelectMany(message => message.Contents).ToList();
            Assert.Equal("hello", response.Text);
            Assert.Equal("protected-stream", Assert.Single(contents.OfType<TextReasoningContent>()).ProtectedData);
            Assert.Equal("call-stream", Assert.Single(contents.OfType<FunctionCallContent>()).CallId);
        });
        Assert.Equal(2, usage.InputTokens);
        Assert.Equal(3, usage.OutputTokens);
    }

    [Fact]
    public async Task OpenAi_FailedStatusWithoutMessage_UsesSummary()
    {
        await ServeOpenAiAsync([(200, "application/json", ResponsesReply("failed", []))], async client =>
        {
            var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
            Assert.Equal("The model provider returned a failed response.",
                Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<ErrorContent>()).Message);
        });
    }

    [Fact]
    public async Task OpenAi_HttpFailure_PropagatesWithoutRetry()
    {
        var requests = await ServeOpenAiAsync([(400, "application/json", "{\"error\":{\"message\":\"wire rejected\",\"type\":\"invalid_request_error\"}}")], async client =>
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => new DirectExpertRunner(client).RunAsync(
                new ExpertDefinition("Respond", "any", "text", "Respond."), new Dictionary<string, object>()));
            Assert.Contains("wire rejected", error.Message);
        });
        Assert.Single(requests);
    }

    [Fact]
    public async Task OpenAi_StreamErrorContent_IsNotDuplicated()
    {
        var body = ResponsesEvent("error", new { type = "error", sequence_number = 0, code = "server_error", message = "existing stream error", param = (string?)null });
        await ServeOpenAiAsync([(200, "text/event-stream", body)], async client =>
        {
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")])) updates.Add(update);
            Assert.Equal("existing stream error", Assert.Single(updates.SelectMany(update => update.Contents).OfType<ErrorContent>()).Message);
        });
    }

    [Fact]
    public async Task OpenAi_FailedStreamWithoutResponseStatus_UsesSummary()
    {
        var body = ResponsesEvent("response.failed", new
        {
            type = "response.failed", sequence_number = 0,
            response = new { id = "response-failed", @object = "response", created_at = 1, model = "test-model", output = Array.Empty<object>() },
        });
        await ServeOpenAiAsync([(200, "text/event-stream", body)], async client =>
        {
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")])) updates.Add(update);
            Assert.Equal("The model provider returned a failed response.",
                Assert.Single(updates.SelectMany(update => update.Contents).OfType<ErrorContent>()).Message);
        });
    }

    [Fact]
    public async Task OpenAi_ExistingGenericError_IsNotDuplicated()
    {
        var reply = ResponsesReply("failed", [ResponseText("partial")], new { code = "server_error", message = "already projected" });
        await ServeOpenAiAsync([(200, "application/json", reply)], async client =>
        {
            var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
            Assert.Equal("already projected", Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<ErrorContent>()).Message);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAi_Cancellation_Propagates(bool stream)
    {
        await ServeOpenAiAsync([], async client =>
        {
            var ct = new CancellationToken(canceled: true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                if (!stream) { await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: ct); return; }
                await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: ct)) { }
            });
        });
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("azure")]
    [InlineData("ollama")]
    [InlineData("anthropic")]
    [InlineData("xai")]
    public void BuildChatClient_SupportedProvider_CreatesClientWithoutNetworkCall(string provider)
    {
        using var client = ForgeChatClients.BuildChatClient(new ProviderProfile
        {
            Provider = provider,
            Model = "test-model",
            ApiKey = "test-key"
        });

        Assert.NotNull(client);
    }

    [Fact]
    public void BuildChatClient_UnknownProvider_ThrowsExplicitError()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ForgeChatClients.BuildChatClient(new ProviderProfile
        {
            Provider = "unsupported",
            Model = "test-model"
        }));

        Assert.Equal("Unknown provider 'unsupported'", exception.Message);
    }

    // Phase 53.8: the Anthropic SDK sends a 250-token limit unless one is set, which cut streamed and
    // tool-mode replies short. Read off the wire: both calls ask for the 4096-token default.
    [Fact]
    public async Task Anthropic_StreamingCall_SendsTheDefaultMaxTokens()
    {
        var body = await CaptureAnthropicRequestAsync(async client =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            {
            }
        });

        Assert.Equal(4096, body.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Anthropic_ToolModeCall_SendsTheDefaultMaxTokens()
    {
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create((string path) => "", "Read", "Reads a file")] };

        var body = await CaptureAnthropicRequestAsync(client =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options));

        Assert.Equal(4096, body.GetProperty("max_tokens").GetInt32());
    }

    // Phase 61: tryAGI.Anthropic ignores AllowMultipleToolCalls; read off the wire, a one-call
    // request carries tool_choice.disable_parallel_tool_use on both call paths.
    [Fact]
    public async Task Anthropic_OneToolCall_SendsDisableParallelToolUse()
    {
        var body = await CaptureAnthropicRequestAsync(client =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], OneToolCallOptions()));

        Assert.True(body.GetProperty("tool_choice").GetProperty("disable_parallel_tool_use").GetBoolean());
    }

    [Fact]
    public async Task Anthropic_OneToolCall_Streaming_SendsDisableParallelToolUse()
    {
        var body = await CaptureAnthropicRequestAsync(async client =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], OneToolCallOptions()))
            {
            }
        });

        Assert.True(body.GetProperty("tool_choice").GetProperty("disable_parallel_tool_use").GetBoolean());
    }

    private static ChatOptions OneToolCallOptions() => new() { Tools = [ReadTool()], AllowMultipleToolCalls = false };

    private static AITool ReadTool() => AIFunctionFactory.Create((string path) => "", "Read", "Reads a file");

    // Phase 58 (D6 layer 2): a step with durable chat history reaches the provider as real roles —
    // system in its own slot, alternating user/assistant turns, the step input last, placeholder
    // replies kept, and no role-labelled transcript text inside any message.
    [Fact]
    public async Task Anthropic_ChatHistoryStep_SendsStructuredMessages()
    {
        var body = await CaptureAnthropicRequestAsync(StreamHistoryStepAsync);

        Assert.Contains("You are a critic.", body.GetProperty("system").ToString());
        AssertStructuredTurns(body.GetProperty("messages").EnumerateArray().ToList());
    }

    [Fact]
    public async Task OpenAi_ChatHistoryStep_SendsStructuredMessages()
    {
        var body = await CaptureRequestAsync("openai", StreamHistoryStepAsync);

        var messages = body.GetProperty("input").EnumerateArray().ToList();
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("You are a critic.", ContentText(messages[0]));
        AssertStructuredTurns(messages.Skip(1).ToList());
    }

    // The durable chat path: `mission Chat(message) = { Answerer }`, streamed as the runner does,
    // with earlier turns as ChatHistory and the new message as the mission's root input.
    private static async Task StreamHistoryStepAsync(IChatClient client)
    {
        var ast = MclParser.Parse("mission Chat(message) = { Answerer }");
        var experts = new Dictionary<string, ExpertDefinition>(StringComparer.Ordinal)
        {
            ["Answerer"] = new("Answerer", "message", "reply", "You are a critic."),
        };
        var history = new ChatHistory(
        [
            new ChatMessage(ChatRole.User, "first question"),
            new ChatMessage(ChatRole.Assistant, "first answer"),
            new ChatMessage(ChatRole.User, "second question"),
            new ChatMessage(ChatRole.Assistant, "(no reply: run failed)"),
        ]);
        await new PipelineRunner(new DirectExpertRunner(client)).RunAsync(ast, experts,
            new PipelineRunOptions("Chat", new Dictionary<string, string> { ["message"] = "the new message" })
            { ChatHistory = history, StreamLlmDeltas = true });
    }

    private static void AssertStructuredTurns(IReadOnlyList<JsonElement> messages)
    {
        Assert.Equal(["user", "assistant", "user", "assistant", "user"],
            messages.Select(message => message.GetProperty("role").GetString()));
        Assert.Equal("(no reply: run failed)", ContentText(messages[3]));
        Assert.Equal("the new message", ContentText(messages[^1]));
        Assert.DoesNotContain(messages, message => ContentText(message).Contains("Begin."));
        Assert.DoesNotContain(messages, message =>
            ContentText(message).Contains("user:", StringComparison.OrdinalIgnoreCase)
            || ContentText(message).Contains("assistant:", StringComparison.OrdinalIgnoreCase));
    }

    // A message's text whether the wire carries content as a string or as an array of text parts.
    private static string ContentText(JsonElement message)
    {
        var content = message.GetProperty("content");
        if (content.ValueKind == JsonValueKind.String) return content.GetString()!;
        return string.Concat(content.EnumerateArray()
            .Where(part => part.TryGetProperty("text", out _))
            .Select(part => part.GetProperty("text").GetString()));
    }

    // Phase 53.8: the SDK's streaming client keeps only text deltas, so a streamed turn billed 0+0
    // tokens. Usage from message_start (input) and message_delta (cumulative output) must reach the
    // usage tracker exactly once.
    [Fact]
    public async Task Anthropic_StreamingCall_ReportsUsageAndFinishReason()
    {
        var accumulator = new UsageAccumulator();
        var text = new StringBuilder();
        ChatFinishReason? finishReason = null;

        await ServeAnthropicAsync(200, "text/event-stream", AnthropicTextStream, async client =>
        {
            using var tracked = new UsageTrackingChatClient(client, accumulator);
            await foreach (var update in tracked.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            {
                text.Append(update.Text);
                finishReason ??= update.FinishReason;
            }
        });

        Assert.Equal("hello", text.ToString());
        Assert.Equal(25, accumulator.InputTokens);
        Assert.Equal(7, accumulator.OutputTokens);
        Assert.Equal(ChatFinishReason.Stop, finishReason);
    }

    // Phase 69: the SDK keeps Anthropic's explanation in the response body, so a failed streamed turn
    // read just "Bad Request". Every call path now carries the provider's error.message.
    private const string ProviderErrorSummary =
        "The model provider (Anthropic) returned an error. Check your provider account.";

    [Theory]
    [InlineData("complete")]
    [InlineData("native-stream")]
    [InlineData("tool-stream")]
    public async Task Anthropic_ProviderError_ShowsProviderMessage(string callShape)
    {
        const string body = """{"type":"error","error":{"type":"invalid_request_error","message":"prompt is too long: 250000 tokens > 200000 maximum"}}""";

        var error = await ProviderErrorAsync(400, body, callShape);

        Assert.Equal($"{ProviderErrorSummary} Details: prompt is too long: 250000 tokens > 200000 maximum", error.Message);
        Assert.IsAssignableFrom<Anthropic.ApiException>(error.InnerException);
    }

    // A streamed 5xx keeps no body in the SDK; a whole-response one keeps a body with no message.
    [Theory]
    [InlineData("complete")]
    [InlineData("native-stream")]
    [InlineData("tool-stream")]
    public async Task Anthropic_ProviderErrorWithoutMessage_OmitsDetails(string callShape)
    {
        var error = await ProviderErrorAsync(500, """{"type":"error","error":{"type":"api_error"}}""", callShape);

        Assert.Equal(ProviderErrorSummary, error.Message);
        Assert.IsAssignableFrom<Anthropic.ApiException>(error.InnerException);
    }

    private static void AssertResponsesOptions(JsonElement request)
    {
        Assert.False(request.GetProperty("store").GetBoolean());
        Assert.Contains("reasoning.encrypted_content", request.GetProperty("include").EnumerateArray().Select(item => item.GetString()));
        Assert.False(request.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.Equal("Read", Assert.Single(request.GetProperty("tools").EnumerateArray()).GetProperty("name").GetString());
    }

    private static string ResponsesToolReply(string id) => ResponsesReply("completed",
    [
        new { type = "reasoning", id = $"rs-{id}", summary = new[] { new { type = "summary_text", text = $"reason-{id}" } }, encrypted_content = $"protected-{id}" },
        ResponseText($"text-{id}"),
        new { type = "function_call", id = $"fc-{id}", call_id = id, name = "Read", arguments = "{\"path\":\"fixture.txt\"}", status = "completed" },
    ]);

    private static object ResponseText(string text) => new
    {
        type = "message", id = $"msg-{text}", role = "assistant", status = "completed",
        content = new[] { new { type = "output_text", text, annotations = Array.Empty<object>() } },
    };

    private static string ResponsesReply(string status, object[] output, object? error = null) => JsonSerializer.Serialize(new
    {
        id = "response-fixture", @object = "response", created_at = 1, model = "test-model", status, output, error,
        usage = new { input_tokens = 2, output_tokens = 3, total_tokens = 5 },
    });

    private static string ResponsesEvent(string name, object body) => $"event: {name}\ndata: {JsonSerializer.Serialize(body)}\n\n";

    private static async Task<List<JsonElement>> ServeOpenAiAsync(
        (int Status, string ContentType, string Body)[] replies, Func<IChatClient, Task> call)
    {
        var port = FreePort();
        using var listener = new HttpListener();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var served = ServeResponsesAsync(listener, replies, deadline.Token);
        using var client = ForgeChatClients.BuildChatClient(new ProviderProfile
        { Provider = "openai", Model = "test-model", ApiKey = "test-key", Endpoint = $"http://127.0.0.1:{port}/" });
        try
        {
            await call(client).WaitAsync(deadline.Token);
            return await served.WaitAsync(deadline.Token);
        }
        finally
        {
            deadline.Cancel();
            listener.Close();
            try { await served; }
            catch (Exception error) when (deadline.IsCancellationRequested &&
                error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
        }
    }

    private static async Task<List<JsonElement>> ServeResponsesAsync(HttpListener listener,
        (int Status, string ContentType, string Body)[] replies, CancellationToken ct)
    {
        var requests = new List<JsonElement>();
        foreach (var reply in replies)
        {
            var context = await listener.GetContextAsync().WaitAsync(ct);
            try
            {
                Assert.Equal("/responses", context.Request.Url!.AbsolutePath);
                using var body = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: ct);
                requests.Add(body.RootElement.Clone());
                context.Response.StatusCode = reply.Status;
                context.Response.ContentType = reply.ContentType;
                await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(reply.Body), ct);
            }
            finally { context.Response.Close(); }
        }
        return requests;
    }

    private static async Task<InvalidOperationException> ProviderErrorAsync(int statusCode, string body, string callShape)
    {
        InvalidOperationException? error = null;
        await ServeAnthropicAsync(statusCode, "application/json", body, async client =>
            error = await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync(client, callShape)));
        return error!;
    }

    private static async Task CallAsync(IChatClient client, string callShape)
    {
        List<ChatMessage> messages = [new(ChatRole.User, "hi")];
        if (callShape == "complete")
        {
            await client.GetResponseAsync(messages);
            return;
        }

        var options = callShape == "tool-stream" ? new ChatOptions { Tools = [ReadTool()] } : null;
        await foreach (var _ in client.GetStreamingResponseAsync(messages, options))
        {
        }
    }

    private const string AnthropicTextStream = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"test-model","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":25,"output_tokens":1}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hello"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":7}}

        event: message_stop
        data: {"type":"message_stop"}


        """;

    /// <summary>Points an Anthropic client at a local listener that answers the first request with
    /// the given status and body.</summary>
    private static async Task ServeAnthropicAsync(int statusCode, string contentType, string responseBody, Func<IChatClient, Task> call)
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var served = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = contentType;
            var body = Encoding.UTF8.GetBytes(responseBody);
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        });

        using var client = ForgeChatClients.BuildChatClient(new ProviderProfile
        {
            Provider = "anthropic",
            Model = "test-model",
            ApiKey = "test-key",
            Endpoint = $"http://127.0.0.1:{port}/",
        });
        await call(client);
        await served.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static Task<JsonElement> CaptureAnthropicRequestAsync(Func<IChatClient, Task> call)
        => CaptureRequestAsync("anthropic", call);

    /// <summary>Points a provider client at a local listener, records the first request body, and
    /// answers 400 (not retried by the SDKs) so the call fails without a provider.</summary>
    private static async Task<JsonElement> CaptureRequestAsync(string provider, Func<IChatClient, Task> call)
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var captured = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            using var reader = new StreamReader(context.Request.InputStream);
            var json = await reader.ReadToEndAsync();
            context.Response.StatusCode = 400;
            context.Response.Close();
            if (provider != "anthropic")
                Assert.Equal(provider == "openai" ? "/responses" : "/chat/completions", context.Request.Url!.AbsolutePath);
            return json;
        });

        using var client = ForgeChatClients.BuildChatClient(new ProviderProfile
        {
            Provider = provider,
            Model = "test-model",
            ApiKey = "test-key",
            Endpoint = $"http://127.0.0.1:{port}/",
        });
        await Assert.ThrowsAnyAsync<Exception>(() => call(client));

        using var document = JsonDocument.Parse(await captured.WaitAsync(TimeSpan.FromSeconds(10)));
        return document.RootElement.Clone();
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }
}

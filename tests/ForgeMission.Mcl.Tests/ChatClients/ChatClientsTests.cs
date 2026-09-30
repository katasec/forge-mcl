using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ForgeMission.Core.Adapters;
using ForgeMission.Core.Manifest;
using Microsoft.Extensions.AI;
using ForgeChatClients = ForgeMission.ChatClients.ChatClients;

namespace ForgeMission.Tests.ChatClients;

public sealed class ChatClientsTests
{
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

    // Phase 53.8: the SDK's streaming client keeps only text deltas, so a streamed turn billed 0+0
    // tokens. Usage from message_start (input) and message_delta (cumulative output) must reach the
    // usage tracker exactly once.
    [Fact]
    public async Task Anthropic_StreamingCall_ReportsUsageAndFinishReason()
    {
        var accumulator = new UsageAccumulator();
        var text = new StringBuilder();
        ChatFinishReason? finishReason = null;

        await ServeAnthropicStreamAsync(AnthropicTextStream, async client =>
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
    /// the given server-sent-event body.</summary>
    private static async Task ServeAnthropicStreamAsync(string sse, Func<IChatClient, Task> call)
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var served = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            context.Response.ContentType = "text/event-stream";
            var body = Encoding.UTF8.GetBytes(sse);
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

    /// <summary>Points an Anthropic client at a local listener, records the first request body, and
    /// answers 500 so the call fails without a provider.</summary>
    private static async Task<JsonElement> CaptureAnthropicRequestAsync(Func<IChatClient, Task> call)
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
            context.Response.StatusCode = 500;
            context.Response.Close();
            return json;
        });

        using var client = ForgeChatClients.BuildChatClient(new ProviderProfile
        {
            Provider = "anthropic",
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

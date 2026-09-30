using System.Net;
using System.Net.Sockets;
using System.Text.Json;
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

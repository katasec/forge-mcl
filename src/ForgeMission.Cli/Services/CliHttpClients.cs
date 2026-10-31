using System.Net.Http.Headers;
using ForgeMission.Core.Resolution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ForgeMission.Cli.Services;

// The CLI's named outbound HttpClients, registered once on the CLI host; commands create them from
// the host's IHttpClientFactory. The login and web-search clients set only a timeout — every
// request carries its own absolute URL and Authorization header. conversation-host is the client the
// packaged Forge client application creates for itself: it is based at the configured api.endpoint
// and carries the saved platform key, read from the credential store when the client is created.
internal static class CliHttpClients
{
    internal const string Login = "forge-login";
    internal const string WebSearch = "forge-websearch";
    internal const string ConversationHost = "conversation-host";
    internal const string Health = "forge-health";

    internal static IServiceCollection AddCliHttpClients(this IServiceCollection services)
    {
        services.AddHttpClient(Login, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(WebSearch, client => client.Timeout = TimeSpan.FromMinutes(3)) // live-retrieval backend
            .UseSocketsHttpHandler((handler, _) => handler.PooledConnectionLifetime = TimeSpan.FromMinutes(2))
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan); // held for a whole mission run: rotate connections, not handlers
        services.AddHttpClient(Health); // forge claude's readiness probe of the mission endpoint
        services.AddHttpClient(ConversationHost, (provider, client) =>
        {
            client.BaseAddress = new Uri(provider.GetRequiredService<IOptions<ForgeApiSettings>>().Value.Endpoint + "/");
            if (CredentialStore.GetPlatform()?.Key is { Length: > 0 } key)
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        });
        return services;
    }
}

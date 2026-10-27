using Microsoft.Extensions.DependencyInjection;

namespace ForgeMission.Cli;

// The CLI's named outbound HttpClients, registered once on the CLI host; commands create them from
// the host's IHttpClientFactory. Each client's only configuration is its timeout — every request
// carries its own absolute URL and Authorization header.
internal static class CliHttpClients
{
    internal const string Exec = "forge-exec";
    internal const string Login = "forge-login";
    internal const string WebSearch = "forge-websearch";

    internal static IServiceCollection AddCliHttpClients(this IServiceCollection services)
    {
        services.AddHttpClient(Exec, client => client.Timeout = Timeout.InfiniteTimeSpan); // streams a long-running mission run
        services.AddHttpClient(Login, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(WebSearch, client => client.Timeout = TimeSpan.FromMinutes(3)); // live-retrieval backend
        return services;
    }
}

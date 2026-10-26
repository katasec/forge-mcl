using Microsoft.Extensions.DependencyInjection;

namespace ForgeMission.Cli;

// One owner for the CLI's pooled outbound HttpClients (the same IHttpClientFactory seam forge chat
// already uses). Each command's client is a named client whose only configuration is its timeout —
// every request carries its own absolute URL and Authorization header — so the factory's rotating
// handler pool replaces the former process-lifetime `static readonly HttpClient` fields without
// changing any per-request behaviour.
internal static class CliHttpClients
{
    internal const string Exec = "forge-exec";
    internal const string Login = "forge-login";
    internal const string WebSearch = "forge-websearch";

    private static readonly IHttpClientFactory Factory = BuildFactory();

    internal static HttpClient Create(string name) => Factory.CreateClient(name);

    private static IHttpClientFactory BuildFactory()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(Exec, client => client.Timeout = Timeout.InfiniteTimeSpan); // streams a long-running mission run
        services.AddHttpClient(Login, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(WebSearch, client => client.Timeout = TimeSpan.FromMinutes(3)); // live-retrieval backend
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }
}

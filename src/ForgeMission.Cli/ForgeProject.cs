using System.CommandLine;
using System.Net.Http.Headers;
using ForgeMission.Application;
using ForgeMission.Application.Transport;
using ForgeMission.Core.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeMission.Cli;

// Projects and Missions own creation and recovery. This surface selects the folder, supplies
// the saved platform login, and reports their result; it writes no Project or package files.
public static class ForgeProject
{
    internal static Command BuildCommand()
    {
        var project = new Command("project", "Manage portable Forge projects");
        var create = new Command("create", "Create a portable project with a hosted Chat conversation");
        var folder = new Argument<string?>("folder")
        {
            Description = "Existing project folder (default: current directory)",
            Arity = ArgumentArity.ZeroOrOne,
        };
        create.Add(folder);
        create.SetAction(async result => await RunAsync(result.GetValue(folder)));
        project.Add(create);
        return project;
    }

    public static Task<int> RunAsync(string? folder) =>
        RunCoreAsync(folder, CredentialStore.GetPlatform(), CreateAsync, Console.Out, Console.Error);

    // The seam keeps startup/output checks independent of actual saved credentials and network.
    internal static async Task<int> RunCoreAsync(string? folder, PlatformCredential? platform,
        Func<CreateChatProjectRequest, PlatformCredential, Task<CreateChatProjectResponse>> create,
        TextWriter output, TextWriter error)
    {
        if (platform is null || string.IsNullOrWhiteSpace(platform.Key))
        {
            error.WriteLine("Not signed in. Run `forge login`.");
            return 1;
        }

        string home;
        try { home = Path.GetFullPath(folder ?? Directory.GetCurrentDirectory()); }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException)
        {
            error.WriteLine($"project creation failed: {failure.Message}");
            return 1;
        }

        var result = await create(new CreateChatProjectRequest(home), platform);
        if (result.Error is { } failureReason)
        {
            error.WriteLine(failureReason.Message);
            return 1;
        }
        if (result.Created is not { } created)
        {
            error.WriteLine("Forge returned no project creation result.");
            return 1;
        }

        var projectFile = Path.Combine(created.HomePath, "forge.project.json");
        output.WriteLine($"Project: {projectFile}");
        output.WriteLine(folder is null ? "Run `forge chat`." :
            $"Run `forge chat --project '{projectFile.Replace("'", "''")}'`.");
        return 0;
    }

    private static async Task<CreateChatProjectResponse> CreateAsync(
        CreateChatProjectRequest request, PlatformCredential platform)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("conversation-host", client =>
        {
            client.BaseAddress = new Uri(ForgeExec.ApiEndpoint + "/");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", platform.Key);
        });
        await using var provider = services.BuildServiceProvider();
        await using var app = ApplicationComposition.Create(provider.GetRequiredService<IHttpClientFactory>(),
            null, new CapabilityAuthorizationPolicy([], null), _ => { }, CancellationToken.None);
        return await app.MissionConversations.CreateChatProjectAsync(request, CancellationToken.None);
    }
}

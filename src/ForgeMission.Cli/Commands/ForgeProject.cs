using System.CommandLine;
using ForgeMission.Application;
using ForgeMission.Application.Transport;
using ForgeMission.Core.Resolution;
using ForgeMission.Core.Tools;

namespace ForgeMission.Cli;

// Projects and Missions own creation and recovery. This surface selects the folder, supplies
// the saved platform login, and reports their result; it writes no Project or package files.
public static class ForgeProject
{
    internal static Command BuildCommand(IHttpClientFactory clients)
    {
        var project = new Command("project", "Manage portable Forge projects");
        var create = new Command("create", "Create a portable project with a hosted Chat conversation");
        var folder = new Argument<string?>("folder")
        {
            Description = "Existing project folder (default: current directory)",
            Arity = ArgumentArity.ZeroOrOne,
        };
        create.Add(folder);
        create.SetAction(async result => await RunAsync(result.GetValue(folder), clients));
        project.Add(create);
        return project;
    }

    // The conversation-host client carries the saved platform key itself (CliHttpClients).
    internal static Task<int> RunAsync(string? folder, IHttpClientFactory clients) =>
        RunCoreAsync(folder, CredentialStore.GetPlatform(),
            (request, _) => CreateAsync(request, clients), Console.Out, Console.Error);

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
        CreateChatProjectRequest request, IHttpClientFactory clients)
    {
        await using var app = ApplicationComposition.Create(clients,
            null, new CapabilityAuthorizationPolicy([], null), _ => { }, CancellationToken.None);
        return await app.MissionConversations.CreateChatProjectAsync(request, CancellationToken.None);
    }
}

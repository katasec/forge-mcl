using System.CommandLine;
using ForgeMission.Application;
using ForgeMission.Application.Transport;

namespace ForgeMission.Cli.Commands;

// forge-client's Projects owner writes forge.project.json; this surface selects the folder and
// reports the result. It makes no server call, so it needs no sign-in.
public static class ForgeProject
{
    internal static Command BuildCommand(IHttpClientFactory clients)
    {
        var project = new Command("project", "Manage portable Forge projects");
        var create = new Command("create", "Write forge.project.json in a folder");
        var folder = new Argument<string?>("folder")
        {
            Description = "Existing project folder (default: current directory)",
            Arity = ArgumentArity.ZeroOrOne,
        };
        create.Add(folder);
        create.SetCancellableAction((result, _) => RunAsync(result.GetValue(folder), clients));
        project.Add(create);
        return project;
    }

    internal static Task<int> RunAsync(string? folder, IHttpClientFactory clients) =>
        RunCoreAsync(folder, request => CreateAsync(request, clients), Console.Out, Console.Error);

    // The seam keeps output checks independent of the file system owner.
    internal static async Task<int> RunCoreAsync(string? folder,
        Func<CreateProjectRequest, Task<CreateProjectResponse>> create, TextWriter output, TextWriter error)
    {
        string home;
        try { home = Path.GetFullPath(folder ?? Directory.GetCurrentDirectory()); }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException)
        {
            error.WriteLine($"project creation failed: {failure.Message}");
            return 1;
        }

        var result = await create(new CreateProjectRequest(home));
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
        output.WriteLine(folder is null ? "Run `forge run <mission> \"<prompt>\"`." :
            $"Run `forge run --project '{projectFile.Replace("'", "''")}' <mission> \"<prompt>\"`.");
        return 0;
    }

    private static async Task<CreateProjectResponse> CreateAsync(CreateProjectRequest request, IHttpClientFactory clients)
    {
        await using var app = ApplicationComposition.Create(clients, CancellationToken.None);
        return await app.MissionConversations.CreateProjectAsync(request, CancellationToken.None);
    }
}

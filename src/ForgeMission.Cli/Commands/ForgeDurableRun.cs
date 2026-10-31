using ForgeMission.Application;
using ForgeMission.Application.Transport;
using ForgeMission.Conversations.Contracts;
using ForgeMission.Core.Resolution;

namespace ForgeMission.Cli.Commands;

// forge run: a mission run kept as a cloud conversation in the project. The
// Conversation Host owns the history; this surface prepares the package, admits it as a new
// conversation, submits one turn and prints the final reply. Hands are on by default: the run is
// admitted ProjectWorkspace rooted at the project folder, and Hands decides each file request.
internal static class ForgeDurableRun
{
    private const string OciBase = "ghcr.io/katasec";

    internal static async Task<int> RunAsync(IHttpClientFactory clients, string projectPath, string selector,
        string prompt, CancellationToken ct)
    {
        var platform = CredentialStore.GetPlatform();
        if (platform is null || string.IsNullOrEmpty(platform.Key))
        {
            Console.Error.WriteLine("Not signed in. Run `forge login`.");
            return 1;
        }

        await using var app = ApplicationComposition.Create(clients, null, ForgeChat.PolicyFor(hands: true), _ => { }, ct);
        try
        {
            return await RunTurnAsync(app, Path.GetFullPath(projectPath), selector, prompt, ct);
        }
        // The caller's own cancellation propagates; every other failure is one error line.
        catch (Exception failure) when (!ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"run failed: {failure.Message}");
            return 1;
        }
    }

    private static async Task<int> RunTurnAsync(ApplicationComposition app, string projectFile, string selector,
        string prompt, CancellationToken ct)
    {
        var package = await app.MissionConversations.PrepareSelectedPackageAsync(
            selector, Directory.GetCurrentDirectory(), OciBase, ct);

        var opened = await app.Projects.OpenChatAsync(new ProjectOpenRequest(projectFile, Mission: package.RootMissionName), ct);
        // A run pins version 1, so an undeclared mission is fixed by declaring exactly that.
        var session = opened.Session ?? throw new InvalidOperationException(opened.Error?.Code == ProjectOperationErrorCode.UnknownMission
            ? $"{opened.Error.Message} Declare {package.RootMissionName}@1 in {projectFile}."
            : opened.Error?.Message ?? "The project could not be opened.");

        var workspace = new MissionWorkspaceDescription([session.Project.Home]);
        var created = await app.MissionConversations.CreateSelectedAsync(
            session.Project.ProjectId, Guid.NewGuid(), Guid.NewGuid(), package, workspace, ct);
        var launch = created.Launch;
        await using var hands = await ForgeChat.AttachHandsAsync(app.MissionHands, session.SessionId, created.ConversationId,
            new MissionAccessApproval(launch.MissionVersionId, launch.VersionNumber, launch.DefinitionHash, launch.Profile), ct);
        hands.Begin(message => Console.Error.WriteLine($"error: {message}"));

        var inputs = new MissionNamedInputs(new() { [package.RootInputName] = prompt }, []);
        // The edge admits a turn only without a workspace body; Hands carries the files instead.
        var turn = await app.MissionConversations.SubmitAsync(created.ConversationId, Guid.NewGuid(), inputs, null!, ct);

        string? reply = null;
        await ForgeChat.FollowTurnAsync(app.MissionConversations, created.ConversationId, created.AcceptedSequence,
            turn.TurnAttemptId, includeDeltas: false, item =>
            {
                hands.OnEvent(item);
                if (item.Kind == ConversationEventKind.ParticipantMessage) reply = item.Text;
                if (item.Kind == ConversationEventKind.Error) Console.Error.WriteLine($"error: {item.Reason ?? item.Text}");
            }, ct);

        Console.Error.WriteLine($"conversation: {created.ConversationId}");
        if (reply is null) return 1;
        Console.WriteLine(reply);
        return 0;
    }
}

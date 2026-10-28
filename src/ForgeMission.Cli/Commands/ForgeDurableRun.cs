using ForgeMission.Application;
using ForgeMission.Conversations.Contracts;
using ForgeMission.Core.Resolution;
using ForgeMission.Core.Tools;

namespace ForgeMission.Cli.Commands;

// forge run --project: the same one-shot run, kept as a cloud conversation in the project. The
// Conversation Host owns the history; this surface prepares the package, admits it as a new
// conversation, submits one turn and prints the final reply. Hands are not attached yet, so the
// run is admitted with no workspace roots (NoHands).
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

        await using var app = ApplicationComposition.Create(clients, null, new CapabilityAuthorizationPolicy([], null), _ => { }, ct);
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
        var (projectId, _) = app.Projects.ReadRunProject(projectFile);
        var package = await app.MissionConversations.PrepareSelectedPackageAsync(
            selector, Directory.GetCurrentDirectory(), OciBase, ct);

        var noHands = new MissionWorkspaceDescription([]);
        var created = await app.MissionConversations.CreateSelectedAsync(
            projectId, Guid.NewGuid(), Guid.NewGuid(), package, noHands, ct);
        var inputs = new MissionNamedInputs(new() { [package.RootInputName] = prompt }, []);
        // The edge admits a turn only without a workspace body; a NoHands conversation needs none.
        var turn = await app.MissionConversations.SubmitAsync(created.ConversationId, Guid.NewGuid(), inputs, null!, ct);

        string? reply = null;
        await ForgeChat.FollowTurnAsync(app.MissionConversations, created.ConversationId, created.AcceptedSequence,
            turn.TurnAttemptId, includeDeltas: false, item =>
            {
                if (item.Kind == ConversationEventKind.ParticipantMessage) reply = item.Text;
                if (item.Kind == ConversationEventKind.Error) Console.Error.WriteLine($"error: {item.Reason ?? item.Text}");
            }, ct);

        Console.Error.WriteLine($"conversation: {created.ConversationId}");
        if (reply is null) return 1;
        Console.WriteLine(reply);
        return 0;
    }
}

using ForgeMission.Application;
using ForgeMission.Conversations.Contracts;
using ForgeMission.Core.Resolution;
using ForgeMission.Cli.Services;

namespace ForgeMission.Cli.Commands;

// forge run: a mission run kept as a cloud conversation in the project. The
// Conversation Host owns the history; this surface prepares the package and prints. Starting the
// new conversation (with Hands attached), the one turn and its follow are forge-client's shared
// path, the same one forge chat uses (Phase 76 decision 2).
internal static class ForgeDurableRun
{
    internal static async Task<int> RunAsync(IHttpClientFactory clients, string projectPath, string selector,
        string prompt, string ociBase, bool verbose, CancellationToken ct)
    {
        var platform = CredentialStore.GetPlatform();
        if (platform is null || string.IsNullOrEmpty(platform.Key))
        {
            Console.Error.WriteLine("Not signed in. Run `forge login`.");
            return 1;
        }

        await using var app = ApplicationComposition.Create(clients, _ => { }, ct);
        try
        {
            return await RunTurnAsync(app, Path.GetFullPath(projectPath), selector, prompt, ociBase, verbose, ct);
        }
        // The caller's own cancellation propagates; every other failure is one error line.
        catch (Exception failure) when (!ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"run failed: {failure.Message}");
            return 1;
        }
    }

    private static async Task<int> RunTurnAsync(ApplicationComposition app, string projectFile, string selector,
        string prompt, string ociBase, bool verbose, CancellationToken ct)
    {
        var package = await app.MissionConversations.PrepareSelectedPackageAsync(
            selector, Directory.GetCurrentDirectory(), ociBase, ct);
        var report = new ImmediateProgress(message => Console.Error.WriteLine($"error: {message}"));
        var started = await app.MissionConversations.StartAsync(projectFile, package, report, ct);
        await using var conversation = started.Conversation
            ?? throw new InvalidOperationException(started.Error?.Message ?? "The project could not be opened.");

        var turn = await conversation.RunTurnAsync(Guid.NewGuid(), prompt, Observer(verbose), ct);

        Console.Error.WriteLine($"conversation: {conversation.ConversationId}");
        if (turn.FinalAnswer is null) return 1;
        Console.WriteLine(turn.FinalAnswer);
        return 0;
    }

    /// <summary>Verbose renders every event with chat's line-mode formatter, errors included;
    /// otherwise only errors are printed. Both write to stderr.</summary>
    private static Action<ConversationEvent> Observer(bool verbose)
    {
        if (!verbose)
            return item => { if (item.Kind == ConversationEventKind.Error) Console.Error.WriteLine($"error: {item.Reason ?? item.Text}"); };
        string? lastReply = null;
        return item => lastReply = ForgeChat.Print(item, lastReply, Console.Error);
    }
}

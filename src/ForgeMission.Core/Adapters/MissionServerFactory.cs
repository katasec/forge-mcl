using ForgeMission.Core.Experts;
using ForgeMission.Core.Runtime;
using Scout;
using MclProgram = ForgeMission.Parser.Program;

namespace ForgeMission.Core.Adapters;

// Builds the per-wire MissionChatClient doors over a single mission core (Phase 42.4). One mission,
// one or both doors: the Anthropic wire is handed the FULL conversation; the OpenAI wire keeps the
// legacy last-turn behaviour. The ASP.NET app that hosts these doors is built by the serving layer
// (ForgeServe); this factory owns only the mission-side wiring, so Core stays free of any web-host
// dependency.
public static class MissionServerFactory
{
    // Returns a door per enabled wire; a disabled wire's door is null (leave that wire unmapped).
    public static (MissionChatClient? Anthropic, MissionChatClient? OpenAi) BuildDoors(
        MclProgram ast,
        Dictionary<string, ExpertDefinition> experts,
        IExpertRunner defaultRunner,
        bool anthropicEnabled,
        bool openAiEnabled,
        IWebSearch? webSearch = null)
    {
        var anthropic = anthropicEnabled
            ? new MissionChatClient(ast, experts, defaultRunner, fullConversation: true, webSearch: webSearch)
            : null;
        var openAi = openAiEnabled
            ? new MissionChatClient(ast, experts, defaultRunner, fullConversation: false, webSearch: webSearch)
            : null;
        return (anthropic, openAi);
    }
}

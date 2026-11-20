using ForgeMission.Core.Adapters;
using ForgeMission.Core.Experts;
using ForgeMission.Parser;
using ForgeMission.Core.Runtime;
using Microsoft.Extensions.AI;
using OpenAI;

namespace ForgeMission.Tests.Adapters;

/// <summary>
/// Integration tests against a real LLM. Skipped unless FORGE_RUN_INTEGRATION=1 and OPENAI_API_KEY are set.
/// Run with: FORGE_RUN_INTEGRATION=1 OPENAI_API_KEY=sk-... dotnet test --filter Category=Integration
/// </summary>
public class DirectExpertRunnerIntegrationTests
{
    private static IChatClient BuildChatClient()
    {
        var openAiClient = new OpenAIClient(IntegrationGate.SkipUnlessOptedIn("OPENAI_API_KEY"));
        return openAiClient.GetChatClient("gpt-4o-mini").AsIChatClient();
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task SingleExpert_RealLlm_ReturnsNonEmptyResponse()
    {
        var chatClient = BuildChatClient();

        var expert = new ExpertDefinition(
            "Summariser",
            "Text",
            "Summary",
            "You are a concise summariser. Summarise the input in one sentence.");

        var runner = new DirectExpertRunner(chatClient);
        var context = new Dictionary<string, object> { ["output"] = "The sky is blue because of Rayleigh scattering of sunlight." };
        var result = await runner.RunAsync(expert, context);

        Assert.False(string.IsNullOrWhiteSpace(result.Text));
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task TwoStepPipeline_RealLlm_ContextFlowsBetweenSteps()
    {
        var chatClient = BuildChatClient();

        var ast = MclParser.Parse("""
            mission TestMission = {
                Summariser
                -> Reviewer
            }
            """);

        var experts = new Dictionary<string, ExpertDefinition>
        {
            ["Summariser"] = new("Summariser", "Text", "Summary",
                "You are a concise summariser. Summarise the input in exactly one sentence."),
            ["Reviewer"]   = new("Reviewer", "Summary", "Review",
                "You are a reviewer. Say whether the summary you received is concise. Reply with only 'Yes' or 'No'.")
        };

        var started    = new List<string>();
        var runner     = new PipelineRunner(new DirectExpertRunner(chatClient));
        var options    = new PipelineRunOptions(
            "TestMission",
            Vars: new Dictionary<string, string> { ["output"] = "The sky is blue because of Rayleigh scattering of sunlight." },
            OnTrace: (trace, _) =>
            {
                if (trace is PipelineStepStarted step) started.Add(step.ExpertName);
                return Task.CompletedTask;
            });

        var result = await runner.RunAsync(ast, experts, options);

        Assert.False(string.IsNullOrWhiteSpace(result.Text));
        Assert.Equal(["Summariser", "Reviewer"], started);
    }
}

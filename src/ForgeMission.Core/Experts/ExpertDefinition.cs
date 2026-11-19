namespace ForgeMission.Core.Experts;

public record ExpertDefinition(
    string Name,
    string Input,
    string Output,
    string SystemPrompt,
    string Role                        = "",
    string Kind                        = "llm",
    string Endpoint                    = "",
    string Check                       = "",
    string OnFail                      = "",
    string Model                       = "",
    IReadOnlyList<string>? Inputs      = null,
    string OutputKey                   = "",
    string Threshold                   = "",
    string Command                     = "",
    IReadOnlyList<string>? Args        = null,
    string Timeout                     = "",
    string ExpertDirectory             = "",
    IReadOnlyDictionary<string, string>? OutputKeys = null,
    IReadOnlyDictionary<string, string>? InputKeys  = null)
{
    public bool IsJudge       => Role.Equals("judge",       StringComparison.OrdinalIgnoreCase);
    // The tool-capable terminal expert: client tools attach to THIS expert's
    // provider call only; a tool call from it returns to the client (the agent segment).
    public bool IsAgent       => Role.Equals("agent",       StringComparison.OrdinalIgnoreCase);
}

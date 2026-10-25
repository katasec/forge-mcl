namespace ForgeMission.Serve;

// agent.yaml `wire:` is a DISABLE switch, not a mode (Phase 42.4): one app always serves every
// enabled door. "both" (the default, including blank) serves anthropic + openai; "anthropic" or
// "openai" limits serving to that one door. Any other value is a configuration error the caller
// reports — this type owns which doors a wire string enables, nothing more.
public static class WirePolicy
{
    // Resolves a wire string to its enabled doors. Returns false with a ready-to-report error when
    // the value is not one of the three accepted forms; both enablement flags are false in that case.
    public static bool TryParse(string? wire, out bool anthropicEnabled, out bool openAiEnabled, out string? error)
    {
        var normalized = string.IsNullOrWhiteSpace(wire) ? "both" : wire.Trim().ToLowerInvariant();
        anthropicEnabled = normalized is "both" or "anthropic";
        openAiEnabled    = normalized is "both" or "openai";
        error = normalized is "both" or "anthropic" or "openai"
            ? null
            : $"Unknown wire '{wire}' in agent.yaml — use \"both\", \"anthropic\", or \"openai\".";
        return error is null;
    }
}

namespace ForgeMission.ChatClients;

// The provider catalog surfaced by `forge provider list` / `forge provider scaffold`: the known
// provider names, the fields each needs, a one-line note, and a ready-to-paste forge.toml block.
// ChatClients owns provider knowledge, so the catalog lives beside ChatClients.BuildChatClient.
public sealed record ProviderCatalogEntry(
    string Name,
    string RequiredFields,
    string Notes,
    string ScaffoldBlock);

public static class ProviderCatalog
{
    // Listing order is the order `forge provider list` prints.
    public static IReadOnlyList<ProviderCatalogEntry> Entries { get; } =
    [
        new ProviderCatalogEntry(
            "openai",
            "apiKey, model",
            "Default OpenAI endpoint",
            """
            [providers.default]
            provider = "openai"
            model    = "gpt-4o-mini"         # or: gpt-4o, gpt-4-turbo
            apiKey   = env("MCL_API_KEY")     # set MCL_API_KEY before running
            # endpoint = "..."               # optional — omit for default OpenAI endpoint
            """),
        new ProviderCatalogEntry(
            "anthropic",
            "apiKey, model",
            "Anthropic (Claude models)",
            """
            [providers.default]
            provider = "anthropic"
            model    = "claude-sonnet-4-6"   # or: claude-opus-4-8, claude-haiku-4-5-20251001
            apiKey   = env("ANTHROPIC_API_KEY")
            """),
        new ProviderCatalogEntry(
            "azure",
            "apiKey, model, endpoint",
            "Azure OpenAI Service",
            """
            [providers.default]
            provider = "azure"
            model    = "gpt-4o"
            apiKey   = env("AZURE_OPENAI_API_KEY")
            endpoint = "https://<your-resource>.openai.azure.com/openai/deployments/<deployment>/chat/completions?api-version=2024-02-01"
            """),
        new ProviderCatalogEntry(
            "ollama",
            "model",
            "Local Ollama (no apiKey required)",
            """
            [providers.default]
            provider = "ollama"
            model    = "llama3"              # any model pulled with 'ollama pull <name>'
            endpoint = "http://localhost:11434/v1"  # omit to use this default
            # no apiKey required for local Ollama
            """),
    ];

    public static ProviderCatalogEntry? Find(string name) =>
        Entries.FirstOrDefault(e => e.Name == name);
}

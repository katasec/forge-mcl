using Microsoft.Extensions.AI;

namespace ForgeMission.Core.Runtime;

/// <summary>The earlier turns of a durable chat (Phase 58), as user/assistant messages. When the
/// root mission's step context carries it, <see cref="Adapters.DirectExpertRunner"/> sends every
/// llm step <c>system + these messages + user(step input)</c>. It never renders as text: there is
/// deliberately no transcript <c>ToString</c>, so earlier turns cannot reach a prompt as
/// role-labelled text. Distinct from <see cref="Conversation"/> (the full client conversation of
/// <c>forge serve</c>) and <see cref="SpeakerTranscript"/> (loop(N) dialogue).</summary>
public sealed class ChatHistory
{
    /// <summary>Core-owned context-bag key; never a mission variable.</summary>
    public const string ContextKey = "__chat_history";

    public ChatHistory(IReadOnlyList<ChatMessage> messages)
    {
        var invalid = messages.FirstOrDefault(m => m.Role != ChatRole.User && m.Role != ChatRole.Assistant);
        if (invalid is not null)
            throw new ArgumentException(
                $"Chat history holds only user and assistant messages; got role '{invalid.Role.Value}'.",
                nameof(messages));
        Messages = messages;
    }

    public IReadOnlyList<ChatMessage> Messages { get; }
}

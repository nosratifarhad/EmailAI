namespace EmailAI.Application.AI;

/// <summary>
/// Single source of truth for AI input-size budgets. Email content is untrusted and
/// potentially huge, so message bodies and whole threads are bounded before they are
/// sent to the model. These are constants (not configuration) on purpose: they are
/// guardrails, not tunables, and the prompt builder accounts for them exactly.
/// </summary>
public static class AiLimits
{
    /// <summary>Longest single message body sent to the model (UTF-16 chars).</summary>
    public const int MaxMessageBodyCharacters = 8_000;

    /// <summary>Most thread messages loaded (and sent) for a thread/history operation.</summary>
    public const int MaxThreadMessages = 20;

    /// <summary>
    /// Total character budget for a rendered thread (headers + separators + bodies).
    /// The prompt builder distributes this across all messages so every message keeps
    /// a slice instead of dropping the newest or oldest arbitrarily.
    /// </summary>
    public const int MaxThreadTotalCharacters = 20_000;
}

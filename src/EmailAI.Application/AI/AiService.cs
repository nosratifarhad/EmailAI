using EmailAI.Domain.Exchange;
using EmailAI.Domain.Mail;

namespace EmailAI.Application.AI;

/// <summary>
/// Stateless implementation of <see cref="IAiService"/>: picks the right prompt,
/// sends it through the low-level <see cref="IAiClient"/> and returns the assistant
/// text. The provider, model name, timeout and HTTP handling all stay below this
/// service; business prompts stay in <see cref="AiPrompts"/>.
///
/// The current-user identity is passed straight into the prompt: this service never
/// derives an identity from the email content itself.
/// </summary>
public sealed class AiService(IAiClient client) : IAiService
{
    public Task<AiContent> SummarizeMessageAsync(
        EmailMessage message,
        AiLanguage language,
        MailboxIdentity? currentUser,
        CancellationToken cancellationToken)
        => RunAsync(AiPrompts.MessageSummaryPrompt(message, language, currentUser), cancellationToken);

    public Task<AiContent> SummarizeThreadAsync(
        IReadOnlyList<EmailMessage> messages,
        AiLanguage language,
        MailboxIdentity? currentUser,
        CancellationToken cancellationToken)
        => RunAsync(AiPrompts.ThreadSummaryPrompt(messages, language, currentUser), cancellationToken);

    public Task<AiContent> SuggestReplyAsync(
        EmailMessage target,
        IReadOnlyList<EmailMessage> history,
        AiLanguage language,
        MailboxIdentity? currentUser,
        CancellationToken cancellationToken)
        => RunAsync(AiPrompts.ReplySuggestionPrompt(target, history, language, currentUser), cancellationToken);

    public Task<AiContent> GenerateReplyAsync(
        EmailMessage target,
        IReadOnlyList<EmailMessage> history,
        AiLanguage language,
        MailboxIdentity? currentUser,
        CancellationToken cancellationToken)
        => RunAsync(AiPrompts.ReplyGenerationPrompt(target, history, language, currentUser), cancellationToken);

    private async Task<AiContent> RunAsync(AiChatRequest request, CancellationToken cancellationToken)
    {
        var completion = await client.CompleteAsync(request, cancellationToken);
        return new AiContent(completion.Content);
    }
}

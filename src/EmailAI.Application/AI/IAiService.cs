using EmailAI.Domain.Exchange;
using EmailAI.Domain.Mail;

namespace EmailAI.Application.AI;

/// <summary>
/// Output language preference for an AI operation. Auto follows the source email
/// language (English/Persian support); English/Persian force a specific language.
/// </summary>
public enum AiLanguage
{
    Auto = 0,
    English = 1,
    Persian = 2,
}

/// <summary>Text produced by an AI operation.</summary>
public sealed record AiContent(string Content);

/// <summary>
/// Business-level AI operations used by the API endpoints. Implementations build
/// prompts (see <see cref="AiPrompts"/>) and delegate the HTTP/chat work to an
/// <see cref="IAiClient"/>; no provider-specific HTTP details live here.
///
/// Every operation takes the CURRENT USER explicitly (<see cref="MailboxIdentity"/>,
/// resolved from the Exchange context by <see cref="Exchange.IMailboxIdentityProvider"/>)
/// so the model knows who it is helping instead of inferring an identity from the email
/// content. The parameter is required - passing "no identity" is an explicit
/// <c>null</c>/<see cref="MailboxIdentity.None"/>, never an accident.
/// </summary>
public interface IAiService
{
    /// <summary>Concise summary of one message.</summary>
    Task<AiContent> SummarizeMessageAsync(
        EmailMessage message,
        AiLanguage language,
        MailboxIdentity? currentUser,
        CancellationToken cancellationToken);

    /// <summary>
    /// Summary of a whole conversation. <paramref name="messages"/> must be in
    /// chronological order (oldest first); bodies must already be loaded.
    /// </summary>
    Task<AiContent> SummarizeThreadAsync(
        IReadOnlyList<EmailMessage> messages,
        AiLanguage language,
        MailboxIdentity? currentUser,
        CancellationToken cancellationToken);

    /// <summary>
    /// Short "how should I answer?" suggestion (points + sample), shown to the user in
    /// the AI panel. Never sent automatically.
    /// </summary>
    Task<AiContent> SuggestReplyAsync(
        EmailMessage target,
        IReadOnlyList<EmailMessage> history,
        AiLanguage language,
        MailboxIdentity? currentUser,
        CancellationToken cancellationToken);

    /// <summary>
    /// Complete editable draft reply for the target message. Returns text only - the
    /// caller (UI/API) decides whether the user ever sends it via the existing reply API.
    /// </summary>
    Task<AiContent> GenerateReplyAsync(
        EmailMessage target,
        IReadOnlyList<EmailMessage> history,
        AiLanguage language,
        MailboxIdentity? currentUser,
        CancellationToken cancellationToken);
}

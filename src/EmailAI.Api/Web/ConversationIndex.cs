using EmailAI.Domain.Mail;

namespace EmailAI.Api.Web;


/// <summary>
/// The conversation state the message surfaces share: which conversations the UI already knows (the
/// message count and participants exactly as Exchange reported them) and which ones still have to be
/// looked up.
///
/// This is the ONE lookup behind the conversation badge in the message list, the reading pane's
/// conversation view and the availability of "Summarize thread", so those three can never disagree
/// about what a thread is. Nothing here is invented: a conversation Exchange has not answered for
/// reports as <see cref="ConversationSummary.NotLoaded"/> and is never treated as a thread.
///
/// It owns only the state; the component owns the asynchronous part (when to ask Exchange and how to
/// recover from a failure), exactly like <see cref="MailFolderSidebar"/>.
/// </summary>
public sealed class ConversationIndex
{
    private readonly Dictionary<string, ConversationSummary> _known = new(StringComparer.Ordinal);

    /// <summary>How many Exchange answers were remembered (repeat loads are cache hits, not calls).</summary>
    public int KnownCount => _known.Count;

    /// <summary>How many conversations are known to be threads (used by tests and diagnostics).</summary>
    public int ThreadCount => _known.Values.Count(summary => summary.IsThread);

    /// <summary>The state Exchange reported for a conversation, or null when it was never read.</summary>
    public ConversationSummary? TryGet(string? conversationId) =>
        !string.IsNullOrWhiteSpace(conversationId) && _known.TryGetValue(conversationId, out var summary)
            ? summary
            : null;

    /// <summary>
    /// The state of a conversation: what Exchange reported, or the not-yet-read state that still
    /// carries the conversation's identity.
    /// </summary>
    public ConversationSummary Get(string? conversationId, string? topic)
        => TryGet(conversationId) ?? ConversationSummary.NotLoaded(conversationId, topic);

    /// <summary>
    /// Remembers what Exchange reported. A summary without a conversation id cannot be addressed and
    /// is ignored; a repeated answer replaces the previous one (Exchange stays the source of truth).
    /// </summary>
    public void Apply(IEnumerable<ConversationSummary> summaries)
    {
        foreach (var summary in summaries)
        {
            if (!summary.HasConversation)
            {
                continue;
            }

            _known[summary.ConversationId!] = summary;
        }
    }

    /// <summary>
    /// The conversations of the given rows that Exchange has not answered for yet - in row order,
    /// deduplicated, and bounded to what one lookup may ask for. Rows without a conversation id are
    /// skipped: there is nothing to ask about.
    /// </summary>
    public IReadOnlyList<string> MissingIds(IEnumerable<MessageSummary> rows)
    {
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var id = row.ConversationId;
            if (string.IsNullOrWhiteSpace(id) || TryGet(id) is not null || !seen.Add(id))
            {
                continue;
            }

            missing.Add(id);
            if (missing.Count == ConversationSummary.MaxLookupBatch)
            {
                break;
            }
        }

        return missing;
    }

    /// <summary>Forgets every conversation (used when the configured mailbox changed).</summary>
    public void Reset() => _known.Clear();
}

using EmailAI.Api.Web;
using EmailAI.Application.Exchange;
using EmailAI.Domain.Mail;

namespace EmailAI.Api.Endpoints;

/// <summary>
/// REST surface of the mail vertical slice:
///   GET  /api/folders/{folderKey}/messages?offset=0&pageSize=20
///   GET  /api/folders/{folderKey}/children
///   GET  /api/messages/{itemId}
///   GET  /api/messages/{itemId}/thread
///   POST /api/conversations/summary
///   POST /api/messages/{itemId}/reply
/// A folder key is a well-known folder ("inbox", "sent", "drafts", "deleted", "junk", "archive") or a
/// custom (user-created) folder key reported by the children endpoint. Exchange item ids are
/// URL-safe base64; clients should EscapeDataString them.
/// </summary>
public static class MailEndpoints
{
    public static void MapMailEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api").WithTags("mail");

        group.MapGet("/folders/{folderKey}/messages", GetMessagesAsync)
            .WithName("GetFolderMessages");

        group.MapGet("/folders/{folderKey}/children", GetChildFoldersAsync)
            .WithName("GetChildFolders");

        group.MapGet("/messages/{itemId}", GetMessageAsync)
            .WithName("GetMessage");

        group.MapGet("/messages/{itemId}/thread", GetThreadAsync)
            .WithName("GetMessageThread");

        group.MapPost("/conversations/summary", GetConversationSummariesAsync)
            .WithName("GetConversationSummaries");

        group.MapPost("/messages/{itemId}/reply", ReplyAsync)
            .WithName("ReplyToMessage");
    }

    private static async Task<IResult> GetMessagesAsync(
        IExchangeMailService mail,
        string folderKey = "inbox",
        int offset = 0,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        offset = Math.Max(0, offset);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var page = await mail.GetMessagesAsync(folderKey, offset, pageSize, ct);
        return Results.Ok(page);
    }

    private static async Task<IResult> GetChildFoldersAsync(
        IExchangeMailService mail,
        string folderKey = "inbox",
        CancellationToken ct = default)
    {
        var children = await mail.GetChildFoldersAsync(folderKey, ct);
        return Results.Ok(children);
    }

    /// <summary>
    /// Reports what Exchange holds for the given conversations (message count + participants) in one
    /// Exchange round trip. This is what makes "is this message part of a thread?" an answer from the
    /// mailbox instead of a guess: the ids are checked before any Exchange call, and an empty request
    /// is a caller error rather than an empty mailbox query.
    /// </summary>
    private static async Task<IResult> GetConversationSummariesAsync(
        IExchangeMailService mail,
        ConversationLookupRequest? request,
        CancellationToken ct)
    {
        var conversationIds = (request?.ConversationIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (conversationIds.Length == 0)
        {
            return Results.BadRequest(new { error = "conversationIds is required." });
        }

        if (conversationIds.Length > ConversationSummary.MaxLookupBatch)
        {
            return Results.BadRequest(new
            {
                error = $"At most {ConversationSummary.MaxLookupBatch} conversations can be looked up at once.",
            });
        }

        var summaries = await mail.GetConversationSummariesAsync(conversationIds, ct);
        return Results.Ok(summaries);
    }

    private static async Task<IResult> GetMessageAsync(
        IExchangeMailService mail,
        string itemId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return Results.BadRequest(new { error = "itemId is required." });
        }

        var message = await mail.GetMessageAsync(itemId, ct);
        return Results.Ok(message);
    }

    private static async Task<IResult> GetThreadAsync(
        IExchangeMailService mail,
        string itemId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return Results.BadRequest(new { error = "itemId is required." });
        }

        var thread = await mail.GetThreadAsync(itemId, ct);
        return Results.Ok(thread);
    }

    private static async Task<IResult> ReplyAsync(
        IExchangeMailService mail,
        string itemId,
        ReplyDraft draft,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return Results.BadRequest(new { error = "itemId is required." });
        }

        if (string.IsNullOrWhiteSpace(draft?.Body))
        {
            return Results.BadRequest(new { error = "body is required." });
        }

        var result = await mail.ReplyAsync(itemId, draft, ct);
        return Results.Ok(result);
    }
}

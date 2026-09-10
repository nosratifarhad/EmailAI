using EmailAI.Api.Web;
using EmailAI.Application.AI;
using EmailAI.Application.Exchange;
using EmailAI.Domain.Exchange;
using EmailAI.Domain.Mail;

namespace EmailAI.Api.Endpoints;

/// <summary>
/// Server-side AI surface used by the Blazor UI (the browser never talks to the AI provider
/// directly - it only calls this same origin):
///   GET  /api/ai/readiness                          can the assistant be used right now?
///   POST /api/ai/messages/{itemId}/summarize        single-message summary
///   POST /api/ai/messages/{itemId}/thread-summarize conversation summary
///   POST /api/ai/messages/{itemId}/suggest-reply    short answer suggestion
///   POST /api/ai/messages/{itemId}/generate-reply   complete editable draft
///
/// Every endpoint loads the message/thread through the existing
/// <see cref="IExchangeMailService"/>, resolves the CURRENT USER from the Exchange context
/// through <see cref="IMailboxIdentityProvider"/> and passes that identity explicitly to
/// <see cref="IAiService"/>. The AI therefore knows who it is helping instead of inferring an
/// identity from the email content. Only the produced text is returned - AI configuration
/// values, credentials and the resolved identity's provenance never leave the server as
/// secrets.
/// </summary>
public static class AiEndpoints
{
    public static void MapAiEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/ai").WithTags("ai");

        group.MapGet("/readiness", GetReadinessAsync)
            .WithName("AiReadiness");

        group.MapPost("/messages/{itemId}/summarize", SummarizeMessageAsync)
            .WithName("AiSummarizeMessage");

        group.MapPost("/messages/{itemId}/thread-summarize", SummarizeThreadAsync)
            .WithName("AiSummarizeThread");

        group.MapPost("/messages/{itemId}/suggest-reply", SuggestReplyAsync)
            .WithName("AiSuggestReply");

        group.MapPost("/messages/{itemId}/generate-reply", GenerateReplyAsync)
            .WithName("AiGenerateReply");
    }

    /// <summary>
    /// Reports whether the AI assistant can be used, and why not when it cannot. Always 200 -
    /// this is a state report, not a failure. <c>probe=true</c> additionally contacts the
    /// configured provider; the default is a cheap, local check.
    /// </summary>
    private static async Task<IResult> GetReadinessAsync(
        IAiReadinessService readiness,
        bool probe = false,
        CancellationToken cancellationToken = default)
    {
        var status = await readiness.GetReadinessAsync(probe, cancellationToken);
        return Results.Ok(new AiReadinessResponse
        {
            Ready = status.IsReady,
            Status = status.Status.ToString(),
            ErrorCode = status.ErrorCode,
            Message = status.Message,
            Action = status.Action,
            CanOpenSettings = status.CanOpenSettings,
        });
    }

    private static async Task<IResult> SummarizeMessageAsync(
        IExchangeMailService mail,
        IAiService ai,
        IMailboxIdentityProvider identity,
        string itemId,
        AiOperationRequest? request,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return Results.BadRequest(new { error = "itemId is required." });
        }

        itemId = DecodeRouteItemId(itemId);

        // An unusable language is a caller error (400 with a message), never a silent fallback
        // to Auto and never an unexplained empty 400 from the JSON binder.
        if (!AiLanguageRequest.TryResolve(request, out var language, out var languageError))
        {
            return Results.BadRequest(new { error = languageError });
        }

        var logger = loggerFactory.CreateLogger("EmailAI.Api.AiEndpoints");
        var message = await mail.GetMessageAsync(itemId, cancellationToken);
        var currentUser = await identity.GetCurrentUserAsync(cancellationToken);
        var content = await ai.SummarizeMessageAsync(
            message, language, currentUser, cancellationToken);
        logger.LogInformation("AI summary produced for message {ItemId}.", itemId);
        return Results.Ok(new AiContentResult { Content = content.Content });
    }

    private static async Task<IResult> SummarizeThreadAsync(
        IExchangeMailService mail,
        IAiService ai,
        IMailboxIdentityProvider identity,
        string itemId,
        AiOperationRequest? request,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return Results.BadRequest(new { error = "itemId is required." });
        }

        itemId = DecodeRouteItemId(itemId);

        if (!AiLanguageRequest.TryResolve(request, out var language, out var languageError))
        {
            return Results.BadRequest(new { error = languageError });
        }

        var logger = loggerFactory.CreateLogger("EmailAI.Api.AiEndpoints");
        var thread = await mail.GetThreadAsync(itemId, cancellationToken);
        if (thread.Messages.Count == 0)
        {
            return Results.BadRequest(new { error = "The conversation is empty." });
        }

        var messages = await LoadChronologicalMessagesAsync(mail, thread, cancellationToken, logger);
        if (messages.Count == 0)
        {
            return Results.BadRequest(new { error = "None of the conversation messages could be loaded." });
        }

        var content = await ai.SummarizeThreadAsync(
            messages, language, await identity.GetCurrentUserAsync(cancellationToken), cancellationToken);
        logger.LogInformation(
            "AI thread summary produced for conversation {ConversationId} ({Loaded}/{Total} messages).",
            thread.ConversationId ?? "(none)", messages.Count, thread.TotalCount);
        return Results.Ok(new AiContentResult { Content = content.Content });
    }


    private static async Task<IResult> SuggestReplyAsync(
        IExchangeMailService mail,
        IAiService ai,
        IMailboxIdentityProvider identity,
        string itemId,
        AiOperationRequest? request,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("EmailAI.Api.AiEndpoints");
        return await ReplyDraftAsync(
            mail, ai, identity, itemId, request, logger, cancellationToken, generate: false);
    }

    private static async Task<IResult> GenerateReplyAsync(
        IExchangeMailService mail,
        IAiService ai,
        IMailboxIdentityProvider identity,
        string itemId,
        AiOperationRequest? request,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("EmailAI.Api.AiEndpoints");
        return await ReplyDraftAsync(
            mail, ai, identity, itemId, request, logger, cancellationToken, generate: true);
    }

    private static async Task<IResult> ReplyDraftAsync(
        IExchangeMailService mail,
        IAiService ai,
        IMailboxIdentityProvider identity,
        string itemId,
        AiOperationRequest? request,
        ILogger logger,
        CancellationToken cancellationToken,
        bool generate)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return Results.BadRequest(new { error = "itemId is required." });
        }

        itemId = DecodeRouteItemId(itemId);

        // Resolved before any Exchange round trip: an unusable language costs nothing but a
        // descriptive 400 and can never leave a half-done operation behind.
        if (!AiLanguageRequest.TryResolve(request, out var language, out var languageError))
        {
            return Results.BadRequest(new { error = languageError });
        }

        // The email being replied to is the item the user is looking at.
        var target = await mail.GetMessageAsync(itemId, cancellationToken);

        // The identity of the person the reply is FOR is resolved from the authenticated
        // Exchange mailbox, never guessed from the email content.
        var currentUser = await identity.GetCurrentUserAsync(cancellationToken);

        // Optional context: earlier messages of the same conversation, oldest first.
        // A failed context load must not block the operation - the target alone is
        // enough to draft a reply, so thread errors degrade to an empty history.
        IReadOnlyList<EmailMessage> history = [];
        try
        {
            var thread = await mail.GetThreadAsync(itemId, cancellationToken);
            history = await LoadHistoryBeforeAsync(mail, thread, itemId, cancellationToken, logger);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "AI reply context could not be loaded for message {ItemId}; continuing with the target message only. reason={Reason}",
                new object?[] { itemId, exception.GetType().Name });
        }

        var content = generate
            ? await ai.GenerateReplyAsync(target, history, language, currentUser, cancellationToken)
            : await ai.SuggestReplyAsync(target, history, language, currentUser, cancellationToken);

        logger.LogInformation(
            "AI {Operation} produced for message {ItemId} ({HistoryCount} context messages).",
            new object?[] { generate ? "draft" : "suggestion", itemId, history.Count });
        return Results.Ok(new AiContentResult { Content = content.Content });
    }

    /// <summary>
    /// Loads the full bodies of a conversation in chronological order (oldest first)
    /// up to <see cref="AiLimits.MaxThreadMessages"/> items. A single unreadable item
    /// (moved/deleted race) is skipped rather than failing the whole operation.
    /// </summary>
    private static async Task<IReadOnlyList<EmailMessage>> LoadChronologicalMessagesAsync(
        IExchangeMailService mail,
        MessageThread thread,
        CancellationToken cancellationToken,
        ILogger logger)
    {
        var loaded = new List<EmailMessage>(Math.Min(thread.Messages.Count, AiLimits.MaxThreadMessages));

        foreach (var threadItem in thread.Messages.Take(AiLimits.MaxThreadMessages))
        {
            var message = await TryLoadMessageAsync(mail, threadItem, cancellationToken, logger);
            if (message is not null)
            {
                loaded.Add(message);
            }
        }

        return loaded;
    }

    /// <summary>
    /// Loads the messages that precede the target inside its conversation (oldest
    /// first, up to <see cref="AiLimits.MaxThreadMessages"/>), so the reply has
    /// context. Messages after the target are not needed to reply to it.
    /// </summary>
    private static async Task<IReadOnlyList<EmailMessage>> LoadHistoryBeforeAsync(
        IExchangeMailService mail,
        MessageThread thread,
        string targetItemId,
        CancellationToken cancellationToken,
        ILogger logger)
    {
        var loaded = new List<EmailMessage>(AiLimits.MaxThreadMessages);

        foreach (var threadItem in thread.Messages)
        {
            if (loaded.Count >= AiLimits.MaxThreadMessages)
            {
                break;
            }

            if (string.Equals(threadItem.Id, targetItemId, StringComparison.Ordinal))
            {
                break; // reached the message being replied to - history ends here
            }

            var message = await TryLoadMessageAsync(mail, threadItem, cancellationToken, logger);
            if (message is not null)
            {
                loaded.Add(message);
            }
        }

        return loaded;
    }

    private static async Task<EmailMessage?> TryLoadMessageAsync(
        IExchangeMailService mail,
        ThreadMessage threadItem,
        CancellationToken cancellationToken,
        ILogger logger)
    {
        try
        {
            return await mail.GetMessageAsync(threadItem.Id, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Skipping thread message {ItemId} that could not be loaded for the AI context. reason={Reason}",
                new object?[] { threadItem.Id, exception.GetType().Name });
            return null;
        }
    }

    /// <summary>
    /// ASP.NET Core routing keeps "%2F" literal inside a {itemId} route value (encoded
    /// slashes are never unescaped there), so a base64 EWS id that contains '/' arrives
    /// still carrying "%2F". Decode that one transport-escaping layer so the id matches
    /// the ids returned by the mail service and is accepted by EWS. Safe for ids that
    /// were never escaped: EWS base64 ids never contain '%'.
    /// </summary>
    private static string DecodeRouteItemId(string itemId)
    {
        if (itemId.IndexOf('%') < 0)
        {
            return itemId;
        }

        return Uri.UnescapeDataString(itemId);
    }
}


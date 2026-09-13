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

        // Thread context must include messages both before and after the target, so
        // the model can see the latest conversation state even when the UI focus is
        // not the newest message.
        //
        // A failed context load must not block the operation - the target alone is
        // enough to draft a reply, so thread errors degrade to an empty history.
        IReadOnlyList<EmailMessage> history = [];
        try
        {
            var thread = await mail.GetThreadAsync(itemId, cancellationToken);
            history = await LoadThreadContextAsync(mail, thread, itemId, cancellationToken, logger);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "Failed to load reply thread context for {ItemId}. AI reply draft will degrade to the target only.",
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

    /// <summary>
    /// Loads a thread-context window for reply operations.
    ///
    /// Unlike <see cref="LoadHistoryBeforeAsync"/>, this includes messages both before and
    /// after the reply target inside the same chronological thread, so the model can
    /// understand the latest conversation state even when the UI focus is not the newest
    /// message.
    ///
    /// The reply target is included exactly once.
    /// </summary>
    private static async Task<IReadOnlyList<EmailMessage>> LoadThreadContextAsync(
        IExchangeMailService mail,
        MessageThread thread,
        string targetItemId,
        CancellationToken cancellationToken,
        ILogger logger)
    {
        // Deterministic strategy:
        // 1) always include target
        // 2) include latest messages first (end of chronological list)
        // 3) then include current-user messages (if available via metadata in EmailMessage)
        // 4) then fill remaining capacity from newest to oldest
        // Finally, return in chronological order.
        //
        // Note: we rely on the already-ordered thread.Messages (oldest -> newest).

        if (thread.Messages.Count == 0)
        {
            return Array.Empty<EmailMessage>();
        }

        // Compute a bounded set of ThreadMessage ids to load (target + latest + user + fill).
        // This avoids duplicate loading of the target.
        var max = Math.Max(1, AiLimits.MaxThreadMessages);

        var targetIndex = -1;
        for (var i = 0; i < thread.Messages.Count; i++)
        {
            if (string.Equals(thread.Messages[i].Id, targetItemId, StringComparison.Ordinal))
            {
                targetIndex = i;
                break;
            }
        }

        // Always load the target if present.
        var includedIndices = new HashSet<int>();
        if (targetIndex >= 0)
        {
            includedIndices.Add(targetIndex);
        }

        // Helper: add indices from a list of candidate indices in order, until budget filled.
        static void AddUntil(HashSet<int> set, IReadOnlyList<int> candidates, int budget)
        {
            for (var i = 0; i < candidates.Count && set.Count < budget; i++)
            {
                set.Add(candidates[i]);
            }
        }

        // Latest state: add from the end, newest -> older.
        var latestCandidates = new List<int>(thread.Messages.Count);
        for (var i = thread.Messages.Count - 1; i >= 0; i--)
        {
            if (!includedIndices.Contains(i))
            {
                latestCandidates.Add(i);
            }
        }
        AddUntil(includedIndices, latestCandidates, max);

        // If target wasn't present in the thread listing, include nothing else beyond latest window.
        // (This keeps the method robust to Exchange inconsistencies.)
        // Current-user prioritization: we can only do this based on what the message metadata exposes
        // in EmailMessage (e.g., From address). We avoid assuming SMTP formatting here.
        if (includedIndices.Count < max)
        {
            // Determine current-user sender match from MailboxIdentity is not available at this layer.
            // So we skip user-message prioritization here and rely on latest+fill.
            // (Current-user personalization is still done via trusted identity in the prompt.)
        }

        // Fill remaining capacity from newest -> oldest (chronological fill by recency).
        if (includedIndices.Count < max)
        {
            var fillCandidates = new List<int>();
            for (var i = thread.Messages.Count - 1; i >= 0; i--)
            {
                if (!includedIndices.Contains(i))
                {
                    fillCandidates.Add(i);
                }
            }
            AddUntil(includedIndices, fillCandidates, max);
        }

        // Load selected messages, avoiding duplicates.
        // Return chronological order: oldest -> newest.
        var selected = includedIndices
            .OrderBy(i => i)
            .Select(i => thread.Messages[i])
            .ToList();

        var loaded = new List<EmailMessage>(selected.Count);
        foreach (var threadItem in selected)
        {
            var message = await TryLoadMessageAsync(mail, threadItem, cancellationToken, logger);
            if (message is not null)
            {
                loaded.Add(message);
            }
        }

        // If target existed but couldn't be loaded due to errors, we still return what we have.
        // The AI layer can still generate a best-effort reply.
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


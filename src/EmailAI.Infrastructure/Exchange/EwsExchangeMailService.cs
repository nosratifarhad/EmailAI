using System.Diagnostics;
using EmailAI.Application.Exceptions;
using EmailAI.Application.Exchange;
using EmailAI.Application.Settings;
using EmailAI.Domain.Exchange;
using EmailAI.Domain.Mail;
using Microsoft.Extensions.Logging;
using Ews = Microsoft.Exchange.WebServices.Data;

namespace EmailAI.Infrastructure.Exchange;

/// <summary>
/// EWS-backed implementation of <see cref="IExchangeMailService"/>. Every
/// operation runs one SOAP round-trip through <see cref="ExchangeAuthRunner"/>
/// using exactly one explicitly configured authentication mode: Windows (current
/// process identity) or UsernamePassword (the account saved in Settings, or the
/// deployment's EXCHANGE_USERNAME / EXCHANGE_PASSWORD). There is no automatic
/// fallback between the two mechanisms. Results are mapped
/// to domain models and failures are translated to
/// <see cref="ExchangeMailException"/>.
/// </summary>
public sealed class EwsExchangeMailService : IExchangeMailService
{
    // List views must never load bodies or attachments; keep them cheap. Recipients are
    // deliberately absent: EWS rejects ToRecipients in FindItem requests, so recipient
    // data is loaded through GetItem/Bind (MessageDetailPropertySet) for the detail view.
    private static readonly Ews.PropertySet FolderHeaderPropertySet = new(Ews.BasePropertySet.IdOnly,
        Ews.ItemSchema.Subject,
        Ews.EmailMessageSchema.From,
        Ews.EmailMessageSchema.Sender,
        Ews.ItemSchema.DateTimeReceived,
        Ews.EmailMessageSchema.IsRead,
        Ews.ItemSchema.HasAttachments,
        Ews.ItemSchema.Importance,
        Ews.ItemSchema.ConversationId,
        Ews.EmailMessageSchema.ConversationTopic);

    private static readonly Ews.PropertySet MessageDetailPropertySet = new(Ews.BasePropertySet.FirstClassProperties,
        Ews.ItemSchema.Subject,
        Ews.ItemSchema.Body,
        Ews.ItemSchema.TextBody,
        Ews.ItemSchema.DateTimeSent,
        Ews.ItemSchema.DateTimeReceived,
        Ews.ItemSchema.Attachments,
        Ews.ItemSchema.HasAttachments,
        Ews.ItemSchema.Importance,
        Ews.ItemSchema.ConversationId,
        Ews.EmailMessageSchema.ConversationTopic,
        Ews.EmailMessageSchema.From,
        Ews.EmailMessageSchema.Sender,
        Ews.EmailMessageSchema.ToRecipients,
        Ews.EmailMessageSchema.CcRecipients,
        Ews.EmailMessageSchema.BccRecipients,
        Ews.EmailMessageSchema.ReplyTo,
        Ews.EmailMessageSchema.InternetMessageId,
        Ews.EmailMessageSchema.References,
        Ews.ItemSchema.InReplyTo);

    private static readonly Ews.PropertySet ThreadItemPropertySet = new(Ews.BasePropertySet.IdOnly,
        Ews.ItemSchema.Subject,
        Ews.EmailMessageSchema.From,
        Ews.EmailMessageSchema.Sender,
        Ews.EmailMessageSchema.ToRecipients,
        Ews.ItemSchema.DateTimeReceived,
        Ews.EmailMessageSchema.IsRead,
        Ews.ItemSchema.HasAttachments,
        Ews.ItemSchema.Importance,
        Ews.ItemSchema.ConversationId,
        Ews.EmailMessageSchema.ConversationTopic,
        Ews.ItemSchema.Preview,
        Ews.EmailMessageSchema.InternetMessageId);

    /// <summary>
    /// What one conversation lookup reads: the identity, the topic, who sent each message and the
    /// preview Exchange already computed. Deliberately no bodies - the point is the conversation's
    /// size and participants, not its content.
    /// </summary>
    private static readonly Ews.PropertySet ConversationItemPropertySet = new(Ews.BasePropertySet.IdOnly,
        Ews.EmailMessageSchema.From,
        Ews.EmailMessageSchema.Sender,
        Ews.ItemSchema.ConversationId,
        Ews.EmailMessageSchema.ConversationTopic);

    private static readonly Ews.PropertySet SentScanPropertySet = new(Ews.BasePropertySet.IdOnly,
        Ews.ItemSchema.Subject,
        Ews.ItemSchema.DateTimeReceived,
        Ews.EmailMessageSchema.InternetMessageId,
        Ews.EmailMessageSchema.References);

    /// <summary>
    /// One folder listing carries the id (to address the folder later), the name (to display it) and
    /// the child count (so "has children" needs no extra query). Nothing else is loaded.
    /// </summary>
    private static readonly Ews.PropertySet FolderDiscoveryPropertySet = new(Ews.BasePropertySet.IdOnly,
        Ews.FolderSchema.DisplayName,
        Ews.FolderSchema.ChildFolderCount);

    /// <summary>Folders fetched per Exchange round trip during child-folder discovery.</summary>
    private const int FolderDiscoveryPageSize = 100;

    /// <summary>Upper bound on discovered child folders (a hostile/broken mailbox cannot loop us).</summary>
    private const int MaxDiscoveredChildFolders = 500;

    /// <summary>Upper bound on discovery round trips (100 folders each).</summary>
    private const int MaxFolderDiscoveryPages = 5;

    private readonly IExchangeConfigurationProvider _configuration;
    private readonly ILogger<EwsExchangeMailService> _logger;

    public EwsExchangeMailService(
        IExchangeConfigurationProvider configuration,
        ILogger<EwsExchangeMailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Runs one Exchange operation with a fresh service instance built for the single
    /// explicitly configured authentication mode (Windows or UsernamePassword),
    /// translating any EWS failure into a typed
    /// <see cref="ExchangeMailException"/> that the API middleware understands.
    /// The configured mode is used exactly once: a failed authentication attempt is
    /// reported as a clear authentication error and the operation is never retried
    /// with another credential mechanism. Cancellations always propagate untouched.
    /// <para>
    /// The settings are resolved per operation, so a configuration saved in Settings takes
    /// effect immediately: the per-user configuration is authoritative when present, and the
    /// deployment's environment configuration is used otherwise (never a mix of the two).
    /// </para>
    /// </summary>
    private async Task<T> RunExchangeAsync<T>(
        string operation,
        Func<Ews.ExchangeService, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var options = await _configuration.GetEffectiveOptionsAsync(cancellationToken);
        return await ExchangeAuthRunner.ExecuteAsync(
            options, operation, action, _logger, cancellationToken);
    }

    private static ExchangeMailException InvalidFolderKey(string folderKey) =>
        new(
            ExchangeMailErrorKind.BadRequest,
            $"Unsupported folder '{folderKey}'. Expected a well-known folder key " +
            $"({string.Join(", ", FolderMapping.SupportedKeys)}) or a custom folder key returned by " +
            "GET /api/folders/{folderKey}/children.");

    /// <summary>
    /// Item ids are base64 and frequently contain '/', so HTTP clients percent-encode
    /// them. ASP.NET Core routing keeps "%2F" literal inside a route value (it never
    /// unescapes encoded slashes there), so an id coming from a URL may still contain
    /// "%2F" sequences. Undo that one transport-escaping layer before building the EWS
    /// ItemId. This is safe to run on any id: EWS base64 ids never contain '%'.
    /// </summary>
    private static string NormalizeItemId(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId) || itemId.IndexOf('%') < 0)
        {
            return itemId;
        }

        return Uri.UnescapeDataString(itemId);
    }

    public Task<ExchangeHealthStatus> CheckHealthAsync(CancellationToken cancellationToken = default)
        => ProbeExchangeHealthAsync(ProbeRootFoldersAsync, cancellationToken);

    public Task<MessagePage> GetMessagesAsync(
        string folderKey,
        int offset,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (!FolderMapping.IsSupported(folderKey))
        {
            throw InvalidFolderKey(folderKey);
        }

        var safeOffset = Math.Max(0, offset);
        var safePageSize = Math.Clamp(pageSize, 1, 200);
        var view = new Ews.ItemView(safePageSize, safeOffset)
        {
            PropertySet = FolderHeaderPropertySet,
        };

        return RunExchangeAsync("GetMessages", async (service, ct) =>
        {
            var results = await FindItemsInFolderAsync(service, folderKey, view, ct);
            var messages = results.Items
                .OfType<Ews.EmailMessage>()
                .Select(EwsMapper.ToSummary)
                .ToList();

            return new MessagePage
            {
                Items = messages,
                Offset = safeOffset,
                PageSize = safePageSize,
                TotalCount = results.TotalCount,
                HasMore = results.MoreAvailable || safeOffset + messages.Count < results.TotalCount,
            };
        }, cancellationToken);
    }

    public Task<IReadOnlyList<MailFolder>> GetChildFoldersAsync(
        string parentKey,
        CancellationToken cancellationToken = default)
    {
        if (!FolderMapping.IsSupported(parentKey))
        {
            throw InvalidFolderKey(parentKey);
        }

        return RunExchangeAsync("GetChildFolders", async (service, ct) =>
        {
            var children = new List<MailFolder>();
            var offset = 0;

            for (var page = 0; page < MaxFolderDiscoveryPages; page++)
            {
                // Traversal is explicitly SHALLOW: only the folders nested directly below the parent
                // are children of it, so a folder from elsewhere in the mailbox can never be
                // reported as one.
                var view = new Ews.FolderView(FolderDiscoveryPageSize, offset)
                {
                    PropertySet = FolderDiscoveryPropertySet,
                    Traversal = Ews.FolderTraversal.Shallow,
                };

                var results = await FindChildFoldersAsync(service, parentKey, view, ct);
                CollectChildFolders(children, results, parentKey);

                offset += results.Folders.Count;
                if (children.Count >= MaxDiscoveredChildFolders
                    || !results.MoreAvailable
                    || results.Folders.Count == 0)
                {
                    break;
                }
            }

            children.Sort(static (left, right) =>
                string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase));

            return (IReadOnlyList<MailFolder>)children;
        }, cancellationToken);
    }

    /// <summary>
    /// Runs FindItems against the one folder the key names. A well-known folder is used directly -
    /// exactly one round trip, unchanged for Inbox/Sent/Drafts/... - while a custom key is bound
    /// once first, so a folder that was deleted or moved away reports a typed not-found instead of
    /// an empty list.
    /// </summary>
    private static async Task<Ews.FindItemsResults<Ews.Item>> FindItemsInFolderAsync(
        Ews.ExchangeService service,
        string folderKey,
        Ews.ItemView view,
        CancellationToken cancellationToken)
    {
        if (FolderMapping.TryResolve(folderKey, out var wellKnown))
        {
            return await service.FindItems(wellKnown, view, cancellationToken).ConfigureAwait(false);
        }

        var folderId = await ResolveCustomFolderIdAsync(service, folderKey, cancellationToken).ConfigureAwait(false);
        return await service.FindItems(folderId, view, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs FindFolders (direct children only) against the folder the key names.</summary>
    private static async Task<Ews.FindFoldersResults> FindChildFoldersAsync(
        Ews.ExchangeService service,
        string parentKey,
        Ews.FolderView view,
        CancellationToken cancellationToken)
    {
        if (FolderMapping.TryResolve(parentKey, out var wellKnown))
        {
            return await service.FindFolders(wellKnown, view, cancellationToken).ConfigureAwait(false);
        }

        var folderId = await ResolveCustomFolderIdAsync(service, parentKey, cancellationToken).ConfigureAwait(false);
        return await service.FindFolders(folderId, view, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a custom folder key to a live EWS folder id (one Bind). Binding is what proves the
    /// folder exists for this account; the id is never guessed or constructed locally.
    /// </summary>
    private static async Task<Ews.FolderId> ResolveCustomFolderIdAsync(
        Ews.ExchangeService service,
        string folderKey,
        CancellationToken cancellationToken)
    {
        if (!CustomFolderKey.TryGetUniqueId(folderKey, out var uniqueId))
        {
            throw InvalidFolderKey(folderKey);
        }

        var folder = await Ews.Folder.Bind(
            service,
            new Ews.FolderId(uniqueId),
            new Ews.PropertySet(Ews.BasePropertySet.IdOnly),
            cancellationToken).ConfigureAwait(false);

        return folder.Id ?? new Ews.FolderId(uniqueId);
    }

    /// <summary>Keeps the mail folders of one discovery page (bounded, identity-stable).</summary>
    private static void CollectChildFolders(
        List<MailFolder> children,
        Ews.FindFoldersResults results,
        string parentKey)
    {
        foreach (var folder in results.Folders)
        {
            if (children.Count >= MaxDiscoveredChildFolders)
            {
                return;
            }

            var uniqueId = folder.Id?.UniqueId;
            if (!IsMailFolder(folder) || string.IsNullOrWhiteSpace(uniqueId))
            {
                continue;
            }

            children.Add(EwsMapper.ToChildFolder(folder, parentKey, uniqueId));
        }
    }

    /// <summary>
    /// True for a plain mail folder. A calendar, contacts, tasks or search folder can never hold the
    /// messages this application lists, so it is never offered as a child folder.
    /// </summary>
    private static bool IsMailFolder(Ews.Folder folder)
        => folder is not (Ews.SearchFolder or Ews.ContactsFolder or Ews.CalendarFolder or Ews.TasksFolder);

    /// <summary>
    /// Reads the state of several conversations in ONE Exchange round trip. Only what Exchange
    /// reports comes back: a conversation it no longer holds (deleted, or moved out of the mailbox)
    /// is absent from the result rather than invented as a conversation of one. An empty request
    /// makes no Exchange call at all.
    /// </summary>
    public Task<IReadOnlyList<ConversationSummary>> GetConversationSummariesAsync(
        IReadOnlyList<string> conversationIds,
        CancellationToken cancellationToken = default)
    {
        var requested = (conversationIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(ConversationSummary.MaxLookupBatch)
            .ToArray();

        if (requested.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<ConversationSummary>>([]);
        }

        return RunExchangeAsync("GetConversations", async (service, ct) =>
        {
            var requests = requested
                .Select(id => new Ews.ConversationRequest(new Ews.ConversationId(id), string.Empty))
                .ToArray();

            var responses = await service.GetConversationItems(
                requests,
                ConversationItemPropertySet,
                foldersToIgnore: Array.Empty<Ews.FolderId>(),
                sortOrder: Ews.ConversationSortOrder.DateOrderAscending,
                mailboxScope: Ews.MailboxSearchLocation.PrimaryOnly,
                ct).ConfigureAwait(false);

            var summaries = new List<ConversationSummary>(requested.Length);
            for (var i = 0; i < responses.Count; i++)
            {
                var response = responses[i];

                // A conversation Exchange refused or no longer holds is left out: a caller is never
                // handed a fabricated message count.
                if (response.Result != Ews.ServiceResult.Success || response.Conversation is null)
                {
                    continue;
                }

                summaries.Add(EwsMapper.ToConversationSummary(
                    response.Conversation,
                    i < requested.Length ? requested[i] : string.Empty));
            }

            return (IReadOnlyList<ConversationSummary>)summaries;
        }, cancellationToken);
    }

    public Task<EmailMessage> GetMessageAsync(string itemId, CancellationToken cancellationToken = default)
    {
        var normalizedId = NormalizeItemId(itemId);
        return RunExchangeAsync("GetMessage", async (service, ct) =>
            EwsMapper.ToDetail(await Ews.EmailMessage.Bind(
                service, new Ews.ItemId(normalizedId), MessageDetailPropertySet, ct)),
            cancellationToken);
    }

    public Task<MessageThread> GetThreadAsync(string itemId, CancellationToken cancellationToken = default) =>
        RunExchangeAsync("GetThread", async (service, ct) =>
        {
            var source = await Ews.EmailMessage.Bind(
                service, new Ews.ItemId(NormalizeItemId(itemId)), ThreadItemPropertySet, ct);

            var conversationId = source.ConversationId;
            if (conversationId is null || string.IsNullOrWhiteSpace(conversationId.UniqueId))
            {
                // Message has no conversation history: a thread of one.
                return new MessageThread
                {
                    ConversationId = null,
                    Topic = source.ConversationTopic,
                    TotalCount = 1,
                    FocusMessageId = source.Id?.UniqueId ?? itemId,
                    Messages = [EwsMapper.ToThreadMessage(source, 0, null)],
                };
            }

            var conversation = await service.GetConversationItems(
                conversationId,
                ThreadItemPropertySet,
                syncState: string.Empty,
                foldersToIgnore: Array.Empty<Ews.FolderId>(),
                sortOrder: Ews.ConversationSortOrder.DateOrderAscending,
                cancellationToken);

            var flat = EwsMapper.FlattenConversationNodes(conversation.ConversationNodes);
            if (flat.Count == 0)
            {
                flat.Add((null, source));
            }

            var (depths, parentIds) = ComputeThreadShape(flat);
            var messages = new List<ThreadMessage>(flat.Count);
            for (var i = 0; i < flat.Count; i++)
            {
                messages.Add(EwsMapper.ToThreadMessage(flat[i].Item, depths[i], parentIds[i]));
            }

            return new MessageThread
            {
                ConversationId = conversationId.UniqueId,
                Topic = source.ConversationTopic ?? conversationId.UniqueId,
                TotalCount = messages.Count,
                FocusMessageId = source.Id?.UniqueId ?? itemId,
                Messages = messages,
            };
        }, cancellationToken);

    /// <summary>
    /// The parent of a node is the message whose InternetMessageId equals the
    /// node's ParentInternetMessageId. Computes depth and parent for every item
    /// in one pass, relying on parents being processed before their replies.
    /// </summary>
    private static (int[] Depths, string?[] ParentIds) ComputeThreadShape(
        List<(Ews.ConversationNode? Node, Ews.EmailMessage Item)> ordered)
    {
        var depths = new int[ordered.Count];
        var parentIds = new string?[ordered.Count];

        // Maps each item's InternetMessageId to its position (first wins).
        var indexByInternetId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < ordered.Count; i++)
        {
            var (node, item) = ordered[i];

            if (!string.IsNullOrWhiteSpace(item.InternetMessageId) &&
                !indexByInternetId.ContainsKey(item.InternetMessageId))
            {
                indexByInternetId[item.InternetMessageId] = i;
            }

            var parentInternetId = node?.ParentInternetMessageId;
            if (string.IsNullOrWhiteSpace(parentInternetId) ||
                !indexByInternetId.TryGetValue(parentInternetId, out var parentIndex) ||
                parentIndex >= i)
            {
                depths[i] = 0;
                parentIds[i] = null;
                continue;
            }

            depths[i] = depths[parentIndex] + 1;
            parentIds[i] = ordered[parentIndex].Item.Id?.UniqueId;
        }

        return (depths, parentIds);
    }

    public Task<ReplyResult> ReplyAsync(
        string itemId,
        ReplyDraft draft,
        CancellationToken cancellationToken = default)
    {
        if (draft is null || string.IsNullOrWhiteSpace(draft.Body))
        {
            throw new ExchangeMailException(
                ExchangeMailErrorKind.BadRequest,
                "A non-empty reply body is required.");
        }

        return RunExchangeAsync("Reply", async (service, ct) =>
        {
            var original = await Ews.EmailMessage.Bind(
                service, new Ews.ItemId(NormalizeItemId(itemId)), SentScanPropertySet, ct);

            // Pin the subject so the Sent Items verification below is exact and
            // the server does not decide its own RE:/AW: prefix rules.
            var replySubject = BuildReplySubject(original.Subject);

            var reply = original.CreateReply(draft.ReplyAll);
            reply.Subject = replySubject;
            reply.BodyPrefix = new Ews.MessageBody(Ews.BodyType.HTML, draft.Body);

            // SendAndSaveCopy delivers and copies into the Sent Items folder.
            await reply.SendAndSaveCopy(ct);

            var verification = await VerifyInSentItemsAsync(
                service, replySubject, original.InternetMessageId, ct);

            return new ReplyResult
            {
                Sent = true,
                SentItemId = verification.Found ? verification.ItemId : null,
                Subject = replySubject,
                SentAt = DateTimeOffset.UtcNow,
                Verification = verification,
            };
        }, cancellationToken);
    }

    /// <summary>"Thread" becomes "RE: Thread" exactly once.</summary>
    private static string BuildReplySubject(string? originalSubject)
    {
        var clean = string.IsNullOrWhiteSpace(originalSubject)
            ? "Message"
            : originalSubject.Trim();

        return clean.StartsWith("RE:", StringComparison.OrdinalIgnoreCase)
            ? clean
            : "RE: " + clean;
    }

    /// <summary>
    /// Confirms the sent copy actually landed in Sent Items. The copy is linked
    /// to the original through its References header (which must contain the
    /// original's InternetMessageId); the expected subject is the fallback. A
    /// couple of short re-probes absorb save-to-search lag.
    /// </summary>
    private async Task<SentVerification> VerifyInSentItemsAsync(
        Ews.ExchangeService service,
        string expectedSubject,
        string? originalInternetMessageId,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        var scanSinceUtc = DateTime.UtcNow.AddSeconds(-45);
        var filter = new Ews.SearchFilter.IsGreaterThanOrEqualTo(
            Ews.ItemSchema.DateTimeReceived, scanSinceUtc);
        var view = new Ews.ItemView(25)
        {
            PropertySet = SentScanPropertySet,
        };

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(400 * attempt, cancellationToken);
            }

            var results = await service.FindItems(
                Ews.WellKnownFolderName.SentItems, filter, view, cancellationToken);

            var candidates = results.Items
                .OfType<Ews.EmailMessage>()
                .ToList();

            var linkMatch = candidates.FirstOrDefault(c =>
                !string.IsNullOrWhiteSpace(originalInternetMessageId) &&
                ContainsReference(c.References, originalInternetMessageId!));

            if (linkMatch is not null)
            {
                return new SentVerification
                {
                    Found = true,
                    ItemId = linkMatch.Id?.UniqueId,
                    Subject = linkMatch.Subject,
                    ReceivedAt = EwsMapper.ToUtc(linkMatch.DateTimeReceived),
                    Note = "Linked via References header to the replied-to message.",
                };
            }

            var subjectMatch = candidates.FirstOrDefault(c =>
                string.Equals(c.Subject, expectedSubject, StringComparison.OrdinalIgnoreCase));

            if (subjectMatch is not null)
            {
                return new SentVerification
                {
                    Found = true,
                    ItemId = subjectMatch.Id?.UniqueId,
                    Subject = subjectMatch.Subject,
                    ReceivedAt = EwsMapper.ToUtc(subjectMatch.DateTimeReceived),
                    Note = originalInternetMessageId is null
                        ? "Matched by expected subject (original had no InternetMessageId)."
                        : "Matched by expected subject; References link not found.",
                };
            }
        }

        return new SentVerification
        {
            Found = false,
            Note = $"No matching item in Sent Items after {maxAttempts} probe(s).",
        };
    }

    private static bool ContainsReference(string? referencesHeader, string target)
    {
        if (string.IsNullOrWhiteSpace(referencesHeader))
        {
            return false;
        }

        return referencesHeader
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Contains(target, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One lightweight EWS round trip used by the health check: resolve Root and
    /// page one folder.
    /// </summary>
    private static Task ProbeRootFoldersAsync(Ews.ExchangeService service, CancellationToken cancellationToken)
        => service.FindFolders(Ews.WellKnownFolderName.Root, new Ews.FolderView(1), cancellationToken);

    /// <summary>
    /// Runs a connectivity probe against Exchange through the same single explicit
    /// authentication mode as the mail operations (Windows or UsernamePassword -
    /// never a fallback chain). The probe reports healthy exactly when that one
    /// configured mode succeeds.
    /// </summary>
    internal async Task<ExchangeHealthStatus> ProbeExchangeHealthAsync(
        Func<Ews.ExchangeService, CancellationToken, Task> probe,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        ExchangeOptions? effective = null;
        try
        {
            effective = await _configuration.GetEffectiveOptionsAsync(cancellationToken);
            await ExchangeAuthRunner.ExecuteAsync(
                effective,
                "HealthProbe",
                async (service, ct) =>
                {
                    await probe(service, ct);
                    return true;
                },
                _logger,
                cancellationToken);

            stopwatch.Stop();
            _logger.LogInformation(
                "Exchange health probe succeeded in {LatencyMs} ms.",
                stopwatch.ElapsedMilliseconds);

            return new ExchangeHealthStatus(true, stopwatch.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new ExchangeHealthStatus(false, stopwatch.ElapsedMilliseconds, "Probe cancelled.");
        }
        catch (ExchangeMailException exception)
        {
            stopwatch.Stop();
            var error = BuildHealthError(exception, effective);
            _logger.LogError(
                "Exchange health probe failed. kind={Kind} error={Error}",
                exception.Kind, error);
            return new ExchangeHealthStatus(false, stopwatch.ElapsedMilliseconds, error);
        }
    }

    /// <summary>
    /// Health text is intentionally more specific than the generic safe text when it
    /// can be, so the desktop Settings/health surface says WHY the probe failed. It
    /// explains the single configured mode and that no alternate credential
    /// mechanism is ever tried. Never includes any credential value.
    /// </summary>
    private string BuildHealthError(ExchangeMailException exception, ExchangeOptions? options)
    {
        if (exception.Kind == ExchangeMailErrorKind.Authentication)
        {
            if (options?.UsesUsernamePasswordAuthentication == true)
            {
                return "Exchange authentication failed: the configured username or password " +
                       "was rejected by the Exchange server. Verify the account saved in Settings " +
                       "(and the domain when needed). The Windows " +
                       "identity is not used in UsernamePassword mode and there is no automatic " +
                       "fallback to Windows authentication.";
            }

            return "Exchange authentication failed: the current Windows identity was rejected by " +
                   "the Exchange server. EmailAI never falls back to a username/password or NTLM " +
                   "credential mechanism; check that the account running EmailAI is allowed, or " +
                   "switch to UsernamePassword mode with an explicit account in Settings.";
        }

        return EwsErrorClassifier.SafeText(exception.Kind);
    }
}

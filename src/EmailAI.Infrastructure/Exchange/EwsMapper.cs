using EmailAI.Domain.Mail;
using Ews = Microsoft.Exchange.WebServices.Data;

namespace EmailAI.Infrastructure.Exchange;

/// <summary>
/// Pure converters from EWS objects to the transport-agnostic domain models.
/// No EWS type ever crosses the IExchangeMailService boundary.
/// </summary>
internal static class EwsMapper
{
    public static EmailAddress? ToDomainAddress(Ews.EmailAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        var name = string.IsNullOrWhiteSpace(address.Name) ? null : address.Name.Trim();
        var value = string.IsNullOrWhiteSpace(address.Address) ? null : address.Address.Trim();
        return name is null && value is null ? null : new EmailAddress(name, value);
    }

    public static IReadOnlyList<EmailAddress> ToDomainAddresses(IEnumerable<Ews.EmailAddress>? addresses) =>
        (addresses ?? [])
        .Select(ToDomainAddress)
        .Where(a => a is not null)
        .Cast<EmailAddress>()
        .ToList();

    public static EmailImportance ToDomainImportance(Ews.Importance importance) => importance switch
    {
        Ews.Importance.Low => EmailImportance.Low,
        Ews.Importance.High => EmailImportance.High,
        _ => EmailImportance.Normal,
    };

    /// <summary>
    /// EWS hands back <see cref="DateTime"/> values in the time zone configured
    /// on the service; the domain models are UTC. Unspecified kind is treated as
    /// server-local and normalised via the local zone's offset.
    /// </summary>
    public static DateTimeOffset ToUtc(DateTime? value)
    {
        if (value is null)
        {
            return default;
        }

        return value.Value.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)),
            DateTimeKind.Local => new DateTimeOffset(value.Value.ToUniversalTime()),
            _ => new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Local)).ToUniversalTime(),
        };
    }

    /// <summary>MessageSummary lives off-body; keep list queries cheap.</summary>
    public static MessageSummary ToSummary(Ews.EmailMessage message) => new()
    {
        Id = message.Id?.UniqueId ?? string.Empty,
        Subject = message.Subject,
        From = ToDomainAddress(SenderOf(message)),
        To = ToDomainAddresses(message.ToRecipients),
        ReceivedAt = ToNullableUtc(message.DateTimeReceived),
        IsRead = message.IsRead,
        HasAttachments = message.HasAttachments,
        Importance = ToDomainImportance(message.Importance),
        ConversationId = message.ConversationId?.UniqueId,
        ConversationTopic = string.IsNullOrWhiteSpace(message.ConversationTopic)
            ? null
            : message.ConversationTopic.Trim(),
    };

    /// <summary>
    /// Who a message came from: <c>From</c> when Exchange has one, otherwise the <c>Sender</c>.
    /// Drafts and some system items have neither, and EWS throws
    /// <see cref="Ews.ServiceObjectPropertyException"/> when a property was not part of the requested
    /// property set - so an unloaded property reads as "not available" instead of failing the whole
    /// query (which is what made the Drafts folder and every conversation load answer a 502).
    /// </summary>
    private static Ews.EmailAddress? SenderOf(Ews.EmailMessage message) =>
        LoadedOrNull(() => message.From) ?? LoadedOrNull(() => message.Sender);

    private static T? LoadedOrNull<T>(Func<T?> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (Ews.ServiceObjectPropertyException)
        {
            return null;
        }
    }

    /// <summary>EWS DateTime properties are non-nullable; unset means DateTime.MinValue.</summary>
    private static DateTimeOffset? ToNullableUtc(DateTime value) =>
        value == DateTime.MinValue ? null : ToUtc(value);

    /// <summary>Builds a ThreadMessage from a conversation node item.</summary>
    public static ThreadMessage ToThreadMessage(Ews.EmailMessage message, int depth, string? parentId)
    {
        var s = ToSummary(message);

        return new ThreadMessage
        {
            Id = s.Id,
            Subject = s.Subject,
            From = s.From,
            To = s.To,
            ReceivedAt = s.ReceivedAt,
            IsRead = s.IsRead,
            HasAttachments = s.HasAttachments,
            Importance = s.Importance,
            ConversationId = s.ConversationId,
            ConversationTopic = s.ConversationTopic,
            Depth = depth,
            ParentId = parentId,
            Preview = string.IsNullOrWhiteSpace(message.Preview) ? null : message.Preview.Trim(),
        };
    }

    /// <summary>
    /// Flattens Exchange's conversation nodes into their items, preserving the requested
    /// date-ascending (chronological) order so parents always precede their replies. A node's items
    /// are the messages that share that node's parent.
    /// </summary>
    internal static List<(Ews.ConversationNode? Node, Ews.EmailMessage Item)> FlattenConversationNodes(
        Ews.ConversationNodeCollection nodes)
    {
        var flat = new List<(Ews.ConversationNode?, Ews.EmailMessage)>();
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            foreach (var item in node.Items)
            {
                if (item is Ews.EmailMessage message)
                {
                    flat.Add((node, message));
                }
            }
        }

        return flat;
    }

    /// <summary>
    /// The conversation state of one conversation, from the items Exchange returned for it: how many
    /// messages it holds and who is in it. The count is the number of items Exchange reported, so a
    /// conversation with a single message is a conversation of one - never a thread.
    /// </summary>
    public static ConversationSummary ToConversationSummary(
        Ews.ConversationResponse conversation,
        string requestedId)
    {
        var items = FlattenConversationNodes(conversation.ConversationNodes);
        var senders = new List<EmailAddress>(ConversationSummary.MaxParticipants);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? topic = null;

        foreach (var (_, item) in items)
        {
            if (topic is null && !string.IsNullOrWhiteSpace(item.ConversationTopic))
            {
                topic = item.ConversationTopic.Trim();
            }

            if (senders.Count >= ConversationSummary.MaxParticipants)
            {
                continue;
            }

            var sender = ToDomainAddress(LoadedOrNull(() => item.From) ?? LoadedOrNull(() => item.Sender));
            if (sender is not null && seen.Add(ConversationSummary.ParticipantKey(sender)))
            {
                senders.Add(sender);
            }
        }

        var id = conversation.ConversationId?.UniqueId;
        return new ConversationSummary(
            string.IsNullOrWhiteSpace(id) ? requestedId : id,
            topic,
            items.Count,
            senders);
    }

    /// <summary>
    /// Maps a discovered child folder to the UI-facing folder model. The identity is the Exchange
    /// folder id (encoded as a custom REST folder key), never the display name: names are neither
    /// unique nor stable, and the key must keep addressing the same folder after a rename.
    /// </summary>
    public static MailFolder ToChildFolder(Ews.Folder folder, string parentKey, string uniqueId) => new()
    {
        Id = CustomFolderKey.FromUniqueId(uniqueId),
        DisplayName = string.IsNullOrWhiteSpace(folder.DisplayName) ? UnnamedFolder : folder.DisplayName.Trim(),
        ParentId = parentKey,
        WellKnownType = MailFolderTypes.Custom,
        HasChildren = folder.ChildFolderCount > 0,
    };

    /// <summary>Shown instead of a blank sidebar row when Exchange reports no folder name.</summary>
    internal const string UnnamedFolder = "(unnamed folder)";

    public static EmailMessage ToDetail(Ews.EmailMessage message)
    {
        var summary = ToSummary(message);

        return new EmailMessage
        {
            Id = summary.Id,
            Subject = summary.Subject,
            From = summary.From,
            To = summary.To,
            ReceivedAt = summary.ReceivedAt,
            IsRead = summary.IsRead,
            HasAttachments = summary.HasAttachments,
            Importance = summary.Importance,
            ConversationId = summary.ConversationId,
            ConversationTopic = summary.ConversationTopic,

            Cc = ToDomainAddresses(message.CcRecipients),
            Bcc = ToDomainAddresses(message.BccRecipients),
            ReplyTo = ToDomainAddresses(message.ReplyTo),
            Attachments = ToAttachmentInfos(message.Attachments),
            BodyHtml = message.Body?.BodyType == Ews.BodyType.HTML ? message.Body.Text : null,
            BodyText = message.Body?.BodyType == Ews.BodyType.Text
                ? message.Body.Text
                : string.IsNullOrWhiteSpace(message.TextBody?.Text) ? null : message.TextBody.Text,
            SentAt = ToNullableUtc(message.DateTimeSent),
            InternetMessageId = message.InternetMessageId,
            InReplyTo = string.IsNullOrWhiteSpace(message.InReplyTo) ? null : message.InReplyTo,
            References = SplitReferences(message.References),
        };
    }

    public static AttachmentInfo ToAttachmentInfo(Ews.Attachment attachment) => attachment switch
    {
        Ews.FileAttachment file => new AttachmentInfo(
            file.Id ?? string.Empty,
            string.IsNullOrWhiteSpace(file.Name) ? null : file.Name,
            string.IsNullOrWhiteSpace(file.ContentType) ? null : file.ContentType,
            file.Size > 0 ? file.Size : null,
            file.IsInline),
        Ews.ItemAttachment item => new AttachmentInfo(
            item.Id ?? string.Empty,
            string.IsNullOrWhiteSpace(item.Name) ? null : item.Name,
            null,
            null,
            false),
        _ => new AttachmentInfo(
            attachment.Id ?? string.Empty,
            string.IsNullOrWhiteSpace(attachment.Name) ? null : attachment.Name,
            null,
            null,
            attachment.IsInline),
    };

    private static IReadOnlyList<AttachmentInfo> ToAttachmentInfos(IEnumerable<Ews.Attachment>? attachments) =>
        (attachments ?? [])
        .Select(ToAttachmentInfo)
        .Where(a => !string.IsNullOrWhiteSpace(a.Id))
        .ToList();

    /// <summary>The EWS References property is the raw space-separated header chain.</summary>
    private static IReadOnlyList<string> SplitReferences(string? references) =>
        string.IsNullOrWhiteSpace(references)
            ? []
            : references
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(r => r.Trim())
                .Where(r => r.Length > 0)
                .ToList();
}

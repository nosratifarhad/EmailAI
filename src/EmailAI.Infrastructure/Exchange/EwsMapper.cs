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
        From = ToDomainAddress(message.From ?? message.Sender),
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
        };
    }

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

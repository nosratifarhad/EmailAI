using EmailAI.Application.Notifications;

namespace EmailAI.Api.Endpoints;

/// <summary>
/// The new-mail feed used by the WINDOWS/DESKTOP shell to raise native notifications.
///
///   GET /api/notifications/mail?folder=inbox
///
/// Why a poll endpoint: Exchange/EWS offers no push channel to this application, so the
/// Electron main process asks this endpoint (the same cheap header query the mail list uses)
/// and raises a native notification for what it has not seen yet. The endpoint itself does the
/// de-duplication (see <see cref="IMailNotificationService"/>), so a re-render, a folder
/// switch or an extra poll can never produce a duplicate notification, and the first call of a
/// session only establishes the baseline (a fresh start never notifies for existing mail).
///
/// The response contains only header data the mail list already shows (sender, subject, time)
/// - never a body, never a credential. Browser/server deployments simply never call it.
/// </summary>
public static class NotificationEndpoints
{
    /// <summary>Folder polled by the desktop shell: notifications are for RECEIVED mail.</summary>
    public const string DefaultFolder = "inbox";

    public static void MapNotificationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/notifications").WithTags("notifications");

        group.MapGet("/mail", GetNewMailAsync).WithName("GetNewMailNotifications");
    }

    private static async Task<IResult> GetNewMailAsync(
        IMailNotificationService notifications,
        string? folder = null,
        CancellationToken cancellationToken = default)
    {
        var folderKey = string.IsNullOrWhiteSpace(folder) ? DefaultFolder : folder.Trim();
        var poll = await notifications.PollAsync(folderKey, cancellationToken);

        return Results.Ok(new NewMailNotificationsResponse
        {
            Folder = folderKey,
            Baseline = poll.Baseline,
            Count = poll.Items.Count,
            Items = poll.Items
                .Select(item => new NewMailNotificationDto
                {
                    Id = item.Id,
                    FromName = item.FromName,
                    FromAddress = item.FromAddress,
                    Subject = item.Subject,
                    ReceivedAt = item.ReceivedAt,
                })
                .ToList(),
        });
    }
}

/// <summary>
/// One newly arrived message, as needed by a desktop notification: who sent it, the subject
/// and when. No body content and no credential is ever included.
/// </summary>
public sealed class NewMailNotificationDto
{
    public string Id { get; init; } = string.Empty;

    public string? FromName { get; init; }

    public string? FromAddress { get; init; }

    public string? Subject { get; init; }

    public DateTimeOffset? ReceivedAt { get; init; }
}

/// <summary>
/// Result of one new-mail poll. <see cref="Baseline"/> true means "this poll only recorded the
/// current mailbox state" (and <see cref="Items"/> is empty).
/// </summary>
public sealed class NewMailNotificationsResponse
{
    public string Folder { get; init; } = string.Empty;

    public bool Baseline { get; init; }

    public int Count { get; init; }

    public IReadOnlyList<NewMailNotificationDto> Items { get; init; } = [];
}

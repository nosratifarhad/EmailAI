using System.Collections.Concurrent;
using EmailAI.Application.Exchange;
using EmailAI.Domain.Mail;

namespace EmailAI.Application.Notifications;

/// <summary>
/// <see cref="IMailNotificationService"/> implemented as an in-memory change detector over
/// <see cref="IExchangeMailService"/>.
///
/// How a new message is detected: every poll lists the newest page of headers for a folder
/// (the same cheap query the mail list already uses - no bodies) and reports only items that
/// are both unknown and not older than the folder's high-water mark. The mark and the set of
/// already-reported item ids make the detection idempotent, so a re-render, a folder switch
/// or an extra poll can never produce a duplicate notification.
///
/// The state is deliberately per-process and in-memory: it is created when the backend
/// starts (so the first poll of a session is always a baseline) and is lost on restart,
/// which is exactly what keeps a fresh launch quiet about pre-existing mail.
/// </summary>
public sealed class MailNotificationService(IExchangeMailService mail) : IMailNotificationService
{
    /// <summary>One page of headers is enough to spot new arrivals between two polls.</summary>
    private const int PageSize = 25;

    /// <summary>Bound on remembered item ids per folder (oldest are forgotten first).</summary>
    private const int MaxTrackedIds = 1000;

    private readonly ConcurrentDictionary<string, FolderState> _folders =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<MailNotificationPoll> PollAsync(
        string folderKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderKey))
        {
            throw new ArgumentException("A folder key is required.", nameof(folderKey));
        }

        var page = await mail.GetMessagesAsync(folderKey, 0, PageSize, cancellationToken);
        var items = page.Items ?? [];
        var state = _folders.GetOrAdd(folderKey.Trim(), static _ => new FolderState());

        lock (state.Gate)
        {
            if (!state.BaselineEstablished)
            {
                // Nothing that already existed when the backend started is "new mail".
                MarkKnown(state, items);
                state.HighWaterMark = NewestTimestamp(items) ?? default;
                state.BaselineEstablished = true;
                return new MailNotificationPoll(Baseline: true, Items: []);
            }

            var arrivals = new List<MessageSummary>();

            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.Id) || state.KnownIds.Contains(item.Id))
                {
                    continue;
                }

                // Unknown id: new when it carries no timestamp, or when it is not older
                // than the last message already accounted for.
                if (item.ReceivedAt is { } receivedAt
                    && state.HighWaterMark != default
                    && receivedAt < state.HighWaterMark)
                {
                    continue;
                }

                arrivals.Add(item);
            }

            MarkKnown(state, items);

            if (NewestTimestamp(items) is { } newest && newest > state.HighWaterMark)
            {
                state.HighWaterMark = newest;
            }

            if (arrivals.Count == 0)
            {
                return new MailNotificationPoll(Baseline: false, Items: []);
            }

            arrivals.Sort(static (left, right) => Nullable.Compare(left.ReceivedAt, right.ReceivedAt));

            return new MailNotificationPoll(
                Baseline: false,
                Items: arrivals.Select(ToNotification).ToList());
        }
    }

    /// <summary>Adds every id to the known set, keeping the set bounded (FIFO eviction).</summary>
    private static void MarkKnown(FolderState state, IReadOnlyList<MessageSummary> items)
    {
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Id) || !state.KnownIds.Add(item.Id))
            {
                continue;
            }

            state.Order.Enqueue(item.Id);
            while (state.Order.Count > MaxTrackedIds)
            {
                state.KnownIds.Remove(state.Order.Dequeue());
            }
        }
    }

    private static DateTimeOffset? NewestTimestamp(IReadOnlyList<MessageSummary> items)
    {
        DateTimeOffset? newest = null;
        foreach (var item in items)
        {
            if (item.ReceivedAt is { } receivedAt && (newest is null || receivedAt > newest))
            {
                newest = receivedAt;
            }
        }

        return newest;
    }

    private static MailNotification ToNotification(MessageSummary item) => new(
        item.Id,
        string.IsNullOrWhiteSpace(item.From?.Name) ? null : item.From!.Name,
        string.IsNullOrWhiteSpace(item.From?.Address) ? null : item.From!.Address,
        string.IsNullOrWhiteSpace(item.Subject) ? null : item.Subject,
        item.ReceivedAt);

    private sealed class FolderState
    {
        public object Gate { get; } = new();

        public HashSet<string> KnownIds { get; } = new(StringComparer.Ordinal);

        public Queue<string> Order { get; } = new();

        public DateTimeOffset HighWaterMark { get; set; }

        public bool BaselineEstablished { get; set; }
    }
}


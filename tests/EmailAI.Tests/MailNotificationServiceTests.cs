using EmailAI.Application.Notifications;
using EmailAI.Domain.Mail;

namespace EmailAI.Tests;

/// <summary>
/// New-mail detection for the desktop shell. The contract the Electron main process relies on:
///   * the first poll of a session is a BASELINE (a fresh start never notifies for old mail),
///   * each message is reported exactly once, however often the folder is re-listed,
///   * folders keep separate state, so switching folders cannot create duplicates,
///   * only header data is returned (no body, no secret).
/// </summary>
public sealed class MailNotificationServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FirstPoll_IsABaseline_AndReportsNothing()
    {
        var mail = new FakeExchangeMailService();
        mail.Add("inbox", "id-1", T0, subject: "Already here");
        var service = new MailNotificationService(mail);

        var poll = await service.PollAsync("inbox");

        Assert.True(poll.Baseline);
        Assert.Empty(poll.Items);
    }

    [Fact]
    public async Task SecondPoll_ReportsOnlyTheMessageThatArrivedAfterTheBaseline()
    {
        var mail = new FakeExchangeMailService();
        mail.Add("inbox", "id-1", T0, subject: "Old");
        var service = new MailNotificationService(mail);

        await service.PollAsync("inbox"); // baseline

        mail.Add("inbox", "id-2", T0.AddMinutes(5), subject: "Fresh", fromName: "Ali", fromAddress: "ali@example.com");
        var poll = await service.PollAsync("inbox");

        Assert.False(poll.Baseline);
        var item = Assert.Single(poll.Items);
        Assert.Equal("id-2", item.Id);
        Assert.Equal("Ali", item.FromName);
        Assert.Equal("ali@example.com", item.FromAddress);
        Assert.Equal("Fresh", item.Subject);
        Assert.Equal(T0.AddMinutes(5), item.ReceivedAt);
    }

    [Fact]
    public async Task RepeatedPollsOfTheSameMail_NeverReportDuplicates()
    {
        var mail = new FakeExchangeMailService();
        mail.Add("inbox", "id-1", T0);
        var service = new MailNotificationService(mail);

        await service.PollAsync("inbox");
        mail.Add("inbox", "id-2", T0.AddMinutes(1), subject: "Only once");

        Assert.Single((await service.PollAsync("inbox")).Items);
        Assert.Empty((await service.PollAsync("inbox")).Items);
        Assert.Empty((await service.PollAsync("inbox")).Items);
    }

    [Fact]
    public async Task MailThatExistedAtStartup_IsNeverReportedLater()
    {
        var mail = new FakeExchangeMailService();
        mail.Add("inbox", "old-1", T0.AddDays(-2));
        mail.Add("inbox", "old-2", T0.AddDays(-1));
        var service = new MailNotificationService(mail);

        await service.PollAsync("inbox"); // baseline records both

        // The same messages are still listed (nothing new arrived).
        Assert.Empty((await service.PollAsync("inbox")).Items);
    }

    [Fact]
    public async Task FolderSwitch_KeepsASeparateBaseline_PerFolder()
    {
        var mail = new FakeExchangeMailService();
        mail.Add("inbox", "in-1", T0, subject: "Inbox message");
        mail.Add("sent", "sent-1", T0, subject: "Sent message");
        var service = new MailNotificationService(mail);

        // The mail UI lists the inbox first, then the sent folder: neither may notify.
        Assert.Empty((await service.PollAsync("inbox")).Items);
        Assert.Empty((await service.PollAsync("sent")).Items);

        // Back with one new message per folder: each folder reports only its own arrival.
        mail.Add("inbox", "in-2", T0.AddMinutes(3), subject: "Inbox arrival");
        mail.Add("sent", "sent-2", T0.AddMinutes(4), subject: "Sent arrival");

        var inbox = await service.PollAsync("inbox");
        Assert.Equal("in-2", Assert.Single(inbox.Items).Id);

        var sent = await service.PollAsync("sent");
        Assert.Equal("sent-2", Assert.Single(sent.Items).Id);
    }

    [Fact]
    public async Task Arrivals_AreReturnedOldestFirst()
    {
        var mail = new FakeExchangeMailService();
        mail.Add("inbox", "seed", T0);
        var service = new MailNotificationService(mail);
        await service.PollAsync("inbox");

        mail.Add("inbox", "b", T0.AddMinutes(2), subject: "second");
        mail.Add("inbox", "a", T0.AddMinutes(1), subject: "first");
        mail.Add("inbox", "c", T0.AddMinutes(3), subject: "third");

        var poll = await service.PollAsync("inbox");

        Assert.Equal(["a", "b", "c"], poll.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task ANewMessageWithoutATimestamp_IsStillReported()
    {
        var mail = new FakeMailWithoutTimestamps();
        var service = new MailNotificationService(mail);

        await service.PollAsync("inbox"); // baseline

        mail.Add("id-2", subject: "No date");
        var poll = await service.PollAsync("inbox");

        Assert.Equal("id-2", Assert.Single(poll.Items).Id);
    }

    [Fact]
    public async Task BlankFolderKey_IsRejected()
    {
        var service = new MailNotificationService(new FakeExchangeMailService());

        await Assert.ThrowsAsync<ArgumentException>(() => service.PollAsync("  "));
    }

    [Fact]
    public async Task Notification_CarriesHeaderDataOnly()
    {
        var mail = new FakeExchangeMailService();
        mail.Add("inbox", "seed", T0);
        var service = new MailNotificationService(mail);
        await service.PollAsync("inbox");

        mail.Add("inbox", "id-2", T0.AddMinutes(1), subject: "Quarterly report", fromName: "Sara", fromAddress: "sara@example.com");
        var item = Assert.Single((await service.PollAsync("inbox")).Items);

        Assert.Equal("Quarterly report", item.Subject);
        // The notification model has no body/token member at all - the shape is the guarantee.
        Assert.Equal(
            ["Id", "FromName", "FromAddress", "Subject", "ReceivedAt"],
            typeof(MailNotification).GetProperties().Select(property => property.Name));
    }

    /// <summary>Minimal mail service that lists messages without a received timestamp.</summary>
    private sealed class FakeMailWithoutTimestamps : EmailAI.Application.Exchange.IExchangeMailService
    {
        private readonly List<MessageSummary> _items = [];

        public void Add(string id, string? subject) => _items.Insert(0, new MessageSummary
        {
            Id = id,
            Subject = subject,
            From = new EmailAddress("Sara", "sara@example.com"),
            ReceivedAt = null,
        });

        public Task<MessagePage> GetMessagesAsync(string folderKey, int offset, int pageSize, CancellationToken cancellationToken)
            => Task.FromResult(new MessagePage
            {
                Items = [.. _items],
                Offset = offset,
                PageSize = pageSize,
                TotalCount = _items.Count,
            });

        public Task<EmailMessage> GetMessageAsync(string itemId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<MailFolder>> GetChildFoldersAsync(string parentKey, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<MessageThread> GetThreadAsync(string itemId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ReplyResult> ReplyAsync(string itemId, ReplyDraft draft, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<EmailAI.Domain.Exchange.ExchangeHealthStatus> CheckHealthAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}

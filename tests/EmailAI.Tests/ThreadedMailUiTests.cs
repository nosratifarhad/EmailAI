using EmailAI.Application.Exceptions;
using EmailAI.Application.Exchange;
using EmailAI.Domain.Mail;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EmailAI.Tests;

/// <summary>
/// The conversation surfaces as the user actually meets them, rendered by the real host: the
/// conversation badge in the message list, the conversation view in the reading pane, and above all
/// the state of "Summarize thread".
///
/// The deep link (<c>/?item=...</c>) is used deliberately: it opens a message while the page is being
/// PRERENDERED, so the assertions run against the HTML the page really produces - including the
/// <c>disabled</c> attribute of the AI button, whose previous version was swallowed by Razor's
/// implicit-expression rules and rendered as the literal text
/// <c>disabled="False || string.IsNullOrEmpty(_detail.ConversationId)"</c>, i.e. permanently disabled.
/// The AI readiness probe runs after the first render, so during prerendering the button's disabled
/// state is decided by the conversation alone - exactly what these tests pin.
/// </summary>
public sealed class ThreadedMailUiTests : IClassFixture<SettingsHostFactory>
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    private const string ThreadedId = "item-threaded";
    private const string StandaloneId = "item-standalone";
    private const string LooseId = "item-no-conversation";
    private const string ThreadedConversation = "conv-project-deadline";
    private const string StandaloneConversation = "conv-one-off";

    private readonly SettingsHostFactory _factory;

    public ThreadedMailUiTests(SettingsHostFactory factory) => _factory = factory;

    [Fact]
    public async Task FirstPaint_HasTheListAndTheExpandControl_ButPerformsNoConversationLookup()
    {
        var mail = new FakeExchangeMailService();
        mail.Add("inbox", ThreadedId, T0, subject: "Project deadline update", conversationId: ThreadedConversation);
        mail.Add("inbox", StandaloneId, T0.AddMinutes(-5), subject: "Lunch");
        var client = CreateClient(mail);

        var html = await client.GetStringAsync("/");

        // The list is there and nothing about conversations was asked: the first paint stays exactly one
        // Exchange query (spec 11), and no row claims to be a thread yet.
        Assert.Contains("Project deadline update", html, StringComparison.Ordinal);
        Assert.Equal(1, mail.ListCalls);
        Assert.Equal(0, mail.ConversationLookupCalls);
        Assert.Equal(0, mail.FolderListCalls);
        Assert.DoesNotContain("thread-chip", html, StringComparison.Ordinal);

        // The expand/collapse control of Inbox: a real chevron button, collapsed, with an accessible
        // name - not the 11px text glyph it replaced.
        Assert.Contains("aria-label=\"Expand Inbox\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-expanded=\"false\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"folder-caret", html, StringComparison.Ordinal);
        Assert.DoesNotContain("is-expanded", html, StringComparison.Ordinal);
        Assert.Contains("<path d=\"M6 3.4 10.6 8 6 12.6\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AThreadedMessage_IsBadgedInTheList_ShowsTheConversation_AndOffersSummarizeThread()
    {
        var mail = Mailbox();
        mail.ArrangeConversation(
            ThreadedConversation,
            4,
            new EmailAddress("Farhad", "farhad@contoso.com"),
            new EmailAddress("Alice", "alice@contoso.com"),
            new EmailAddress("Bob", "bob@contoso.com"));
        mail.ArrangeThread(
            FakeExchangeMailService.Thread(
                ThreadedConversation,
                "Project deadline update",
                Header("t1", "Farhad", "farhad@contoso.com", "Project deadline update", T0),
                Header("t2", "Alice", "alice@contoso.com", "RE: Project deadline update", T0.AddMinutes(30)),
                Header("t3", "Bob", "bob@contoso.com", "RE: Project deadline update", T0.AddMinutes(50)),
                Header(ThreadedId, "Farhad", "farhad@contoso.com", "RE: Project deadline update", T0.AddMinutes(80))),
            ThreadedId);
        var client = CreateClient(mail);

        var html = await client.GetStringAsync($"/?item={Uri.EscapeDataString(ThreadedId)}");

        // The conversation state came from Exchange: one lookup, for exactly this conversation.
        Assert.Equal(1, mail.ConversationLookupCalls);
        Assert.Equal([ThreadedConversation], Assert.Single(mail.RequestedConversations));

        // "Summarize thread" is available, and the old broken expression is gone for good.
        Assert.DoesNotContain("string.IsNullOrEmpty", html, StringComparison.Ordinal);
        Assert.DoesNotContain("disabled", SummarizeThreadButton(html), StringComparison.Ordinal);
        Assert.Contains("Summarize the whole conversation (4 messages).", html, StringComparison.Ordinal);

        // The list row carries the conversation badge with the message count - and only that row: the
        // conversations of the other rows are unknown, so nothing is claimed about them.
        Assert.Equal(1, CountOf(html, "class=\"thread-chip\""));
        Assert.Contains("thread-chip-count\">4</span>", html, StringComparison.Ordinal);
        Assert.Contains("Conversation with Farhad, Alice", html, StringComparison.Ordinal);

        // The reading pane shows the conversation itself: count, people, previews, latest message.
        Assert.Contains("class=\"thread-block\"", html, StringComparison.Ordinal);
        Assert.Contains("thread-people\">Farhad, Alice, Bob", html, StringComparison.Ordinal);
        Assert.Contains("thread-count\">4 messages", html, StringComparison.Ordinal);
        Assert.Contains("class=\"thread-preview\"", html, StringComparison.Ordinal);
        Assert.Contains("thread-latest\">Latest", html, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"true\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStandaloneMessage_ShowsNoConversation_AndExplainsWhySummarizeThreadIsUnavailable()
    {
        var mail = Mailbox();
        mail.ArrangeConversation(StandaloneConversation, 1);
        var client = CreateClient(mail);

        var html = await client.GetStringAsync($"/?item={Uri.EscapeDataString(StandaloneId)}");

        // Exchange was asked (and answered): a conversation of one message is not a thread.
        Assert.Equal(1, mail.ConversationLookupCalls);
        Assert.Contains("disabled", SummarizeThreadButton(html), StringComparison.Ordinal);
        Assert.Contains("This message is not part of a conversation with replies.", html, StringComparison.Ordinal);

        // Nothing pretends it is a conversation: no badge, no conversation view, no toggle.
        Assert.DoesNotContain("thread-chip", html, StringComparison.Ordinal);
        Assert.DoesNotContain("thread-block", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Hide conversation", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMessageOutsideAnyConversation_IsNeverAskedAbout_AndNeverOffered()
    {
        var mail = Mailbox();
        var client = CreateClient(mail);

        var html = await client.GetStringAsync($"/?item={Uri.EscapeDataString(LooseId)}");

        // There is nothing to ask about, so nothing is asked - and nothing is claimed.
        Assert.Equal(0, mail.ConversationLookupCalls);
        Assert.Contains("disabled", SummarizeThreadButton(html), StringComparison.Ordinal);
        Assert.Contains("This message is not part of a conversation.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("thread-chip", html, StringComparison.Ordinal);
        Assert.DoesNotContain("thread-block", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConversationThatCannotBeChecked_IsNotGuessedToBeAThread()
    {
        var mail = Mailbox();
        mail.FailNextConversationLookupKind = ExchangeMailErrorKind.MailboxError;
        var client = CreateClient(mail);

        var html = await client.GetStringAsync($"/?item={Uri.EscapeDataString(ThreadedId)}");

        // The failure changed nothing the user came for ...
        Assert.Contains("Project deadline update", html, StringComparison.Ordinal);
        Assert.Contains("class=\"message-row", html, StringComparison.Ordinal);

        // ... and it is not translated into "this is a thread": the button says it is still checking.
        Assert.Equal(1, mail.ConversationLookupCalls);
        Assert.Contains("disabled", SummarizeThreadButton(html), StringComparison.Ordinal);
        Assert.Contains("Checking whether this message belongs to a conversation", html, StringComparison.Ordinal);
        Assert.DoesNotContain("thread-block", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A mailbox with four inbox rows: a threaded message, a standalone one, a message Exchange keeps
    /// outside any conversation, and a row nobody arranged (so its conversation stays unknown).
    /// </summary>
    private static FakeExchangeMailService Mailbox()
    {
        var mail = new FakeExchangeMailService();
        mail.Add("inbox", ThreadedId, T0, subject: "Project deadline update", conversationId: ThreadedConversation);
        mail.Add("inbox", StandaloneId, T0.AddMinutes(-5), subject: "Lunch?", conversationId: StandaloneConversation);
        mail.Add("inbox", LooseId, T0.AddMinutes(-10), subject: "Welcome to EmailAI");
        mail.Add("inbox", "item-unknown", T0.AddMinutes(-15), subject: "Newsletter", conversationId: "conv-not-arranged");

        mail.ArrangeMessage(Detail(ThreadedId, "Project deadline update", ThreadedConversation, "Project deadline update"));
        mail.ArrangeMessage(Detail(StandaloneId, "Lunch?", StandaloneConversation, "Lunch?"));
        mail.ArrangeMessage(Detail(LooseId, "Welcome to EmailAI", null, null));
        return mail;
    }

    private static EmailMessage Detail(string id, string subject, string? conversationId, string? topic)
        => new()
        {
            Id = id,
            Subject = subject,
            From = new EmailAddress("Farhad", "farhad@contoso.com"),
            ReceivedAt = T0,
            SentAt = T0,
            ConversationId = conversationId,
            ConversationTopic = topic,
            BodyText = "Body of " + subject,
        };

    private static MessageSummary Header(
        string id,
        string fromName,
        string fromAddress,
        string subject,
        DateTimeOffset receivedAt)
        => new()
        {
            Id = id,
            Subject = subject,
            From = new EmailAddress(fromName, fromAddress),
            ReceivedAt = receivedAt,
        };

    /// <summary>The markup of the "Summarize thread" button, up to its label.</summary>
    private static string SummarizeThreadButton(string html)
    {
        var label = html.IndexOf(">Summarize thread<", StringComparison.Ordinal);
        Assert.True(label >= 0, "The AI panel must always offer \"Summarize thread\".");
        var start = html.LastIndexOf("<button", label, StringComparison.Ordinal);
        Assert.True(start >= 0);
        return html[start..label];
    }

    private static int CountOf(string html, string value)
        => html.Split(value, StringSplitOptions.None).Length - 1;

    /// <summary>
    /// Creates a client for the real host with in-memory Exchange. The prerendering page calls the
    /// application's own REST API with an HttpClient whose base address is the request's origin, so the
    /// DI HttpClient is replaced by one that loops straight back into the in-memory test server.
    /// </summary>
    private HttpClient CreateClient(FakeExchangeMailService mail)
    {
        WebApplicationFactory<Program>? derived = null;

        derived = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            SettingsHostFactory.RemoveRegistrations<IExchangeMailService>(services);
            services.AddSingleton<IExchangeMailService>(mail);

            SettingsHostFactory.RemoveRegistrations<HttpClient>(services);
            services.AddScoped(_ => derived!.CreateClient());
        }));

        return derived.CreateClient();
    }
}


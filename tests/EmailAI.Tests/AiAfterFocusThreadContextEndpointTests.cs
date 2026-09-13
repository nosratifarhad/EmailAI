using System.Net;
using System.Net.Http.Json;
using EmailAI.Application.AI;
using EmailAI.Application.Exchange;
using EmailAI.Domain.Exchange;
using EmailAI.Domain.Mail;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EmailAI.Tests;

/// <summary>
/// Endpoint → LoadThreadContextAsync → AiService → AiPrompts → IAiClient.
///
/// Ensures the "after-focus context" requirement for reply suggestion:
/// when reply target is M3, the AI prompt must include M1..M6, including
/// messages after M3 (M4..M6), in chronological order, with M3 identified
/// exactly once as the reply target.
/// </summary>
public sealed class AiAfterFocusThreadContextEndpointTests : IClassFixture<SettingsHostFactory>
{
    private const string ConversationId = "conv-1";
    private const string ReplyTargetId = "M3";

    private readonly SettingsHostFactory _factory;

    public AiAfterFocusThreadContextEndpointTests(SettingsHostFactory factory) => _factory = factory;

    [Fact]
    public async Task SuggestReply_ReplyTargetFocusedAfterMessagesReachAiClient()
    {
        // Arrange
        var currentUser = MailboxIdentity.Create(
            "Alex Doe",
            "alex@contoso.com",
            "alex",
            MailboxIdentity.ExchangeDirectorySource);

        var exchange = new FakeExchangeMailService();
        exchange.ArrangeConversation(
            ConversationId,
            messageCount: 6,
            new EmailAddress("Alex Doe", "alex@contoso.com"),
            new EmailAddress("Sam", "sam@example.com"));

        // Message identities used inside the prompt come from subject/from/ids.
        // We use unique subjects so the captured prompt can assert exact presence.
        var t0 = new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);
        var m1 = ThreadMessage("M1", "Subject M1", t0);
        var m2 = ThreadMessage("M2", "Subject M2", t0.AddMinutes(1));
        var m3 = ThreadMessage(ReplyTargetId, "Subject M3", t0.AddMinutes(2));
        var m4 = ThreadMessage("M4", "Subject M4", t0.AddMinutes(3));
        var m5 = ThreadMessage("M5", "Subject M5", t0.AddMinutes(4));
        var m6 = ThreadMessage("M6", "Subject M6", t0.AddMinutes(5));

        var thread = FakeExchangeMailService.Thread(
            ConversationId,
            topic: "Thread topic",
            m1, m2, m3, m4, m5, m6);

        exchange.ArrangeThread(thread, "M1", "M2", "M3", "M4", "M5", "M6");

        // Ensure the endpoint can also load each message body by id.
        exchange.ArrangeMessage(FullMessage("M1", t0, "Subject M1"));
        exchange.ArrangeMessage(FullMessage("M2", t0.AddMinutes(1), "Subject M2"));
        exchange.ArrangeMessage(FullMessage("M3", t0.AddMinutes(2), "Subject M3"));
        exchange.ArrangeMessage(FullMessage("M4", t0.AddMinutes(3), "Subject M4"));
        exchange.ArrangeMessage(FullMessage("M5", t0.AddMinutes(4), "Subject M5"));
        exchange.ArrangeMessage(FullMessage("M6", t0.AddMinutes(5), "Subject M6"));

        var capturingAiClient = new CapturingAiClient();
        var aiOptions = TestOptions.Ai();

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(EnvironmentName.Production);
            builder.ConfigureServices(services =>
            {
                SettingsHostFactory.RemoveRegistrations<IExchangeMailService>(services);
                SettingsHostFactory.RemoveRegistrations<IMailboxIdentityProvider>(services);
                SettingsHostFactory.RemoveRegistrations<IAiClient>(services);

                services.AddSingleton<IExchangeMailService>(exchange);
                services.AddSingleton<IMailboxIdentityProvider>(new StubMailboxIdentityProvider(currentUser));
                services.AddSingleton<IAiClient>(capturingAiClient);
                // Provide options so the endpoint considers AI configured.
                services.AddSingleton<IOptionsMonitor<EmailAI.Domain.AI.AiOptions>>(
                    new StaticOptionsMonitor<EmailAI.Domain.AI.AiOptions>(aiOptions));
            });
        }).CreateClient();

        // Act
        using var response = await client.PostAsJsonAsync(
            $"/api/ai/messages/{ReplyTargetId}/suggest-reply",
            new { language = "Auto" });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var request = Assert.Single(capturingAiClient.Requests);
        Assert.NotEmpty(request.Messages);

        // Current-user identity in system prompt
        var system = request.Messages.First(m => m.Role == AiChatRole.System).Content;
        Assert.Contains("Alex Doe", system, StringComparison.Ordinal);
        Assert.Contains("alex@contoso.com", system, StringComparison.Ordinal);

        // User message contains fenced thread context.
        var user = request.Messages.First(m => m.Role == AiChatRole.User).Content;

        // Proof that M4..M6 are included in thread context.
        Assert.Contains("Subject M4", user, StringComparison.Ordinal);
        Assert.Contains("Subject M5", user, StringComparison.Ordinal);
        Assert.Contains("Subject M6", user, StringComparison.Ordinal);

        // Full chronological thread context presence (by subject markers)
        var p1 = user.IndexOf("Subject M1", StringComparison.Ordinal);
        var p2 = user.IndexOf("Subject M2", StringComparison.Ordinal);
        var p3 = user.IndexOf("Subject M3", StringComparison.Ordinal);
        var p4 = user.IndexOf("Subject M4", StringComparison.Ordinal);
        var p5 = user.IndexOf("Subject M5", StringComparison.Ordinal);
        var p6 = user.IndexOf("Subject M6", StringComparison.Ordinal);

        Assert.True(p1 >= 0 && p2 >= 0 && p3 >= 0 && p4 >= 0 && p5 >= 0 && p6 >= 0);
        Assert.True(p1 < p2 && p2 < p3 && p3 < p4 && p4 < p5 && p5 < p6);

        // M3 appears exactly once inside the thread context.
        // We treat "thread context" as the fenced block content, while the reply target
        // is described separately outside the fences.
        var fenced = ExtractFencedUserData(user);
        Assert.Equal(1, CountOccurrences(fenced, "Subject M3", StringComparison.Ordinal));

        // Target message separately identified as reply target.
        Assert.Contains("Email being replied to", user, StringComparison.Ordinal);
        Assert.Contains("Subject M3", user, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateReply_ReplyTargetFocusedAfterMessagesReachAiClient()
    {
        // Arrange (reuse the same M1..M6 / focus M3 fixture)
        var currentUser = MailboxIdentity.Create(
            "Alex Doe",
            "alex@contoso.com",
            "alex",
            MailboxIdentity.ExchangeDirectorySource);

        var exchange = new FakeExchangeMailService();
        exchange.ArrangeConversation(
            ConversationId,
            messageCount: 6,
            new EmailAddress("Alex Doe", "alex@contoso.com"),
            new EmailAddress("Sam", "sam@example.com"));

        var t0 = new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);
        var m1 = ThreadMessage("M1", "Subject M1", t0);
        var m2 = ThreadMessage("M2", "Subject M2", t0.AddMinutes(1));
        var m3 = ThreadMessage(ReplyTargetId, "Subject M3", t0.AddMinutes(2));
        var m4 = ThreadMessage("M4", "Subject M4", t0.AddMinutes(3));
        var m5 = ThreadMessage("M5", "Subject M5", t0.AddMinutes(4));
        var m6 = ThreadMessage("M6", "Subject M6", t0.AddMinutes(5));

        var thread = FakeExchangeMailService.Thread(
            ConversationId,
            topic: "Thread topic",
            m1, m2, m3, m4, m5, m6);

        exchange.ArrangeThread(thread, "M1", "M2", "M3", "M4", "M5", "M6");

        exchange.ArrangeMessage(FullMessage("M1", t0, "Subject M1"));
        exchange.ArrangeMessage(FullMessage("M2", t0.AddMinutes(1), "Subject M2"));
        exchange.ArrangeMessage(FullMessage("M3", t0.AddMinutes(2), "Subject M3"));
        exchange.ArrangeMessage(FullMessage("M4", t0.AddMinutes(3), "Subject M4"));
        exchange.ArrangeMessage(FullMessage("M5", t0.AddMinutes(4), "Subject M5"));
        exchange.ArrangeMessage(FullMessage("M6", t0.AddMinutes(5), "Subject M6"));

        var capturingAiClient = new CapturingAiClient();
        var aiOptions = TestOptions.Ai();

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(EnvironmentName.Production);
            builder.ConfigureServices(services =>
            {
                SettingsHostFactory.RemoveRegistrations<IExchangeMailService>(services);
                SettingsHostFactory.RemoveRegistrations<IMailboxIdentityProvider>(services);
                SettingsHostFactory.RemoveRegistrations<IAiClient>(services);

                services.AddSingleton<IExchangeMailService>(exchange);
                services.AddSingleton<IMailboxIdentityProvider>(new StubMailboxIdentityProvider(currentUser));
                services.AddSingleton<IAiClient>(capturingAiClient);
                services.AddSingleton<IOptionsMonitor<EmailAI.Domain.AI.AiOptions>>(
                    new StaticOptionsMonitor<EmailAI.Domain.AI.AiOptions>(aiOptions));
            });
        }).CreateClient();

        // Act
        using var response = await client.PostAsJsonAsync(
            $"/api/ai/messages/{ReplyTargetId}/generate-reply",
            new { language = "Auto" });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var request = Assert.Single(capturingAiClient.Requests);
        Assert.NotEmpty(request.Messages);

        var system = request.Messages.First(m => m.Role == AiChatRole.System).Content;
        Assert.Contains("Alex Doe", system, StringComparison.Ordinal);
        Assert.Contains("alex@contoso.com", system, StringComparison.Ordinal);

        var user = request.Messages.First(m => m.Role == AiChatRole.User).Content;
        Assert.Contains("Email being replied to", user, StringComparison.Ordinal);

        // Reply target: M3
        Assert.Contains("Subject M3", user, StringComparison.Ordinal);

        // Thread context: must include M1..M6 after focus, specifically M4/M5/M6 after M3
        var fenced = ExtractFencedUserData(user);
        Assert.Contains("Subject M4", fenced, StringComparison.Ordinal);
        Assert.Contains("Subject M5", fenced, StringComparison.Ordinal);
        Assert.Contains("Subject M6", fenced, StringComparison.Ordinal);

        var p3 = fenced.IndexOf("Subject M3", StringComparison.Ordinal);
        var p4 = fenced.IndexOf("Subject M4", StringComparison.Ordinal);
        var p5 = fenced.IndexOf("Subject M5", StringComparison.Ordinal);
        var p6 = fenced.IndexOf("Subject M6", StringComparison.Ordinal);
        Assert.True(p3 >= 0 && p4 >= 0 && p5 >= 0 && p6 >= 0);
        Assert.True(p3 < p4 && p4 < p5 && p5 < p6);

        // M3 appears exactly once inside the thread fenced block
        Assert.Equal(1, CountOccurrences(fenced, "Subject M3", StringComparison.Ordinal));
    }

    // ---------- helpers ----------

    private static MessageSummary ThreadMessage(string id, string subject, DateTimeOffset receivedAt)
        => new()
        {
            Id = id,
            Subject = subject,
            From = new EmailAddress("Sam", "sam@example.com"),
            ReceivedAt = receivedAt,
        };

    private static EmailMessage FullMessage(string id, DateTimeOffset receivedAt, string subject)
        => new()
        {
            Id = id,
            Subject = subject,
            From = new EmailAddress("Sam", "sam@example.com"),
            BodyText = $"Body {id}",
            ReceivedAt = receivedAt,
        };

    private static int CountOccurrences(string haystack, string needle, StringComparison comparison)
    {
        var count = 0;
        var index = 0;
        while (true)
        {
            index = haystack.IndexOf(needle, index, comparison);
            if (index < 0) return count;
            count++;
            index += needle.Length;
        }
    }

    private static string ExtractFencedUserData(string content)
    {
        const string begin = "<BEGIN EMAIL DATA>";
        const string end = "<END EMAIL DATA>";
        var start = content.IndexOf(begin, StringComparison.Ordinal);
        var finish = content.IndexOf(end, StringComparison.Ordinal);
        Assert.True(start >= 0 && finish > start, "Expected <BEGIN EMAIL DATA> ... <END EMAIL DATA> in AI user message.");
        return content[(start + begin.Length)..finish];
    }

    private sealed class CapturingAiClient : IAiClient
    {
        public List<AiChatRequest> Requests { get; } = [];

        public Task<AiProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AiProbeResult(true, 1, null));

        public Task<AiChatCompletion> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new AiChatCompletion("ok", "gpt-5", "stop"));
        }
    }
}

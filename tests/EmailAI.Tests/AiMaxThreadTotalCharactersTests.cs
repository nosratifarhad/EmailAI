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

public sealed class AiMaxThreadTotalCharactersTests : IClassFixture<SettingsHostFactory>
{
    private const string ConversationId = "conv-limit-1";
    private const string ReplyTargetId = "M3";

    private readonly SettingsHostFactory _factory;
    public AiMaxThreadTotalCharactersTests(SettingsHostFactory factory) => _factory = factory;

    [Fact]
    public async Task GenerateReply_ThreadFencedBlockRespectsMaxThreadTotalCharacters_ReplyTargetIncludedOnce()
    {
        // Arrange: craft thread where bodies are huge to trigger truncation.
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

        // Maximize bodies for all messages; we only assert prompt budget behaviour.
        string hugeBody = new string('X', 50_000);
        exchange.ArrangeMessage(FullMessage("M1", t0, "Subject M1", hugeBody));
        exchange.ArrangeMessage(FullMessage("M2", t0.AddMinutes(1), "Subject M2", hugeBody));
        exchange.ArrangeMessage(FullMessage("M3", t0.AddMinutes(2), "Subject M3", hugeBody));
        exchange.ArrangeMessage(FullMessage("M4", t0.AddMinutes(3), "Subject M4", hugeBody));
        exchange.ArrangeMessage(FullMessage("M5", t0.AddMinutes(4), "Subject M5", hugeBody));
        exchange.ArrangeMessage(FullMessage("M6", t0.AddMinutes(5), "Subject M6", hugeBody));

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

        // Assert: request ok and AI context is bounded.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var request = Assert.Single(capturingAiClient.Requests);
        var user = request.Messages.First(m => m.Role == AiChatRole.User).Content;
        Assert.Contains("Email being replied to", user, StringComparison.Ordinal);

        var fenced = ExtractFencedUserData(user);

        // Entire thread fenced block should be bounded by the contract.
        // Note: the prompt builder slices bodies under MaxThreadTotalCharacters but
        // includes per-message headers/prefixes and an optional truncation note, so
        // we validate with a small slack.
        Assert.True(
            fenced.Length <= AiLimits.MaxThreadTotalCharacters + 2_000,
            $"Expected fenced thread data <= {AiLimits.MaxThreadTotalCharacters} (+slack), got {fenced.Length}");

        // Reply target (M3) must appear exactly once inside the thread fenced block.
        Assert.Equal(1, CountOccurrences(fenced, "Subject M3", StringComparison.Ordinal));
    }

    private static MessageSummary ThreadMessage(string id, string subject, DateTimeOffset receivedAt)
        => new()
        {
            Id = id,
            Subject = subject,
            From = new EmailAddress("Sam", "sam@example.com"),
            ReceivedAt = receivedAt,
        };

    private static EmailMessage FullMessage(string id, DateTimeOffset receivedAt, string subject, string bodyText)
        => new()
        {
            Id = id,
            Subject = subject,
            From = new EmailAddress("Sam", "sam@example.com"),
            BodyText = bodyText,
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
        // For reply prompts, earlier thread history is wrapped in <BEGIN EMAIL DATA>.. <END EMAIL DATA>.
        const string begin = "<BEGIN EMAIL DATA>\n";
        const string end = "<END EMAIL DATA>";
        var start = content.IndexOf(begin, StringComparison.Ordinal);
        var finish = content.IndexOf(end, StringComparison.Ordinal);
        Assert.True(start >= 0 && finish > start, "Expected fenced thread data in AI user message.");
        // keep the actual fence payload only
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

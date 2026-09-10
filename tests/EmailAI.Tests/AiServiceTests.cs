using EmailAI.Application.AI;
using EmailAI.Domain.Exchange;
using EmailAI.Domain.Mail;

namespace EmailAI.Tests;

public class AiServiceTests
{
    [Fact]
    public async Task SummarizeMessageAsync_ReturnsModelContent_AndUsesPromptBuilder()
    {
        var stub = new StubAiClient(new AiChatCompletion("Short summary.", "gpt-5", "stop"));
        var service = new AiService(stub);

        var result = await service.SummarizeMessageAsync(
            Message("Body to summarize"), AiLanguage.Auto, CurrentUser, CancellationToken.None);

        Assert.Equal("Short summary.", result.Content);
        var request = Assert.Single(stub.Requests);
        Assert.Contains("summarize", request.Messages[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Body to summarize", request.Messages[1].Content);
    }

    [Fact]
    public async Task GenerateReplyAsync_ForwardsPersianLanguageIntoPrompt()
    {
        var stub = new StubAiClient(new AiChatCompletion("پاسخ آماده است.", "gpt-5", "stop"));
        var service = new AiService(stub);

        await service.GenerateReplyAsync(
            Message("متن ایمیل"), [Message("قدیمی‌تر")], AiLanguage.Persian, CurrentUser, CancellationToken.None);

        var request = Assert.Single(stub.Requests);
        Assert.Contains("Persian (Farsi)", request.Messages[0].Content);
        Assert.Contains("متن ایمیل", request.Messages[1].Content);
    }

    [Fact]
    public async Task ThreadSummary_ReceivesEverySuppliedMessage()
    {
        var stub = new StubAiClient(new AiChatCompletion("Thread summary.", "gpt-5", "stop"));
        var service = new AiService(stub);

        await service.SummarizeThreadAsync(
            [Message("one"), Message("two")], AiLanguage.English, CurrentUser, CancellationToken.None);

        var request = Assert.Single(stub.Requests);
        var user = request.Messages[1].Content;
        Assert.Contains("MESSAGE 1", user);
        Assert.Contains("MESSAGE 2", user);
    }

    private sealed class StubAiClient(AiChatCompletion result) : IAiClient
    {
        public List<AiChatRequest> Requests { get; } = [];

        public Task<AiChatCompletion> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(result);
        }

        public Task<AiProbeResult> ProbeAsync(CancellationToken cancellationToken)
            => Task.FromResult(new AiProbeResult(true, 1, null));
    }

    /// <summary>
    /// The identity the API layer resolves from the Exchange context and hands to every
    /// operation, so the model knows who it is helping.
    /// </summary>
    private static MailboxIdentity CurrentUser { get; } = MailboxIdentity.Create(
        "Alex Doe",
        "alex@contoso.com",
        "alex",
        MailboxIdentity.ExchangeDirectorySource);

    [Fact]
    public async Task SummarizeMessageAsync_PutsTheCurrentUserIntoTheSystemPrompt()
    {
        var stub = new StubAiClient(new AiChatCompletion("ok", "gpt-5", "stop"));
        var service = new AiService(stub);

        await service.SummarizeMessageAsync(Message(), AiLanguage.Auto, CurrentUser, CancellationToken.None);

        var system = Assert.Single(stub.Requests).Messages[0].Content;
        Assert.Contains("CURRENT USER", system, StringComparison.Ordinal);
        Assert.Contains("alex@contoso.com", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SummarizeMessageAsync_WithoutAnIdentity_StillProducesAUsablePrompt()
    {
        var stub = new StubAiClient(new AiChatCompletion("ok", "gpt-5", "stop"));
        var service = new AiService(stub);

        await service.SummarizeMessageAsync(
            Message(), AiLanguage.Auto, currentUser: null, CancellationToken.None);

        var system = Assert.Single(stub.Requests).Messages[0].Content;
        Assert.Contains("CURRENT USER: unknown", system, StringComparison.Ordinal);
    }

    private static EmailMessage Message(string body = "Hi, please call me back.")
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Subject = "Hello",
            From = new EmailAddress("Sara", "sara@example.com"),
            BodyText = body,
            ReceivedAt = DateTimeOffset.UtcNow,
        };
}

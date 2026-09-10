using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EmailAI.Api.Web;
using EmailAI.Application.Exceptions;
using EmailAI.Application.Exchange;
using EmailAI.Domain.Mail;
using Microsoft.Extensions.DependencyInjection;

namespace EmailAI.Tests;

/// <summary>
/// The conversation lookup through the REAL host: one call answers for several conversations, only
/// what Exchange reported comes back (a conversation it no longer holds is omitted, never invented as
/// a conversation of one), the request is validated before any Exchange work, and a mailbox failure
/// stays a typed, secret-free answer. This is the source of truth the list badge, the reading pane and
/// "Summarize thread" share.
/// </summary>
public sealed class ConversationEndpointsTests : IClassFixture<SettingsHostFactory>
{
    private readonly SettingsHostFactory _factory;

    public ConversationEndpointsTests(SettingsHostFactory factory) => _factory = factory;

    private HttpClient CreateClient(FakeExchangeMailService mail)
        => _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            SettingsHostFactory.RemoveRegistrations<IExchangeMailService>(services);
            services.AddSingleton<IExchangeMailService>(mail);
        })).CreateClient();

    [Fact]
    public async Task SeveralConversations_AreAnsweredInOneLookup()
    {
        var mail = new FakeExchangeMailService();
        mail.ArrangeConversation(
            "conv-1",
            4,
            new EmailAddress("Farhad", "farhad@contoso.com"),
            new EmailAddress("Alice", "alice@contoso.com"));
        mail.ArrangeConversation("conv-2", 1);
        var client = CreateClient(mail);

        var payload = await PostJsonAsync(client, new { conversationIds = new[] { "conv-1", "conv-2" } });

        var summaries = payload.EnumerateArray().ToArray();
        Assert.Equal(2, summaries.Length);

        // The documented shape, including the derived thread answer the UI renders from.
        Assert.Equal(
            ["conversationId", "hasConversation", "isKnown", "isThread", "messageCount", "participants", "topic"],
            summaries[0].EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));

        var threaded = summaries.Single(summary => Text(summary, "conversationId") == "conv-1");
        Assert.Equal(4, threaded.GetProperty("messageCount").GetInt32());
        Assert.True(threaded.GetProperty("isThread").GetBoolean());
        Assert.Equal(2, threaded.GetProperty("participants").GetArrayLength());
        Assert.Equal("farhad@contoso.com", Text(threaded.GetProperty("participants")[0], "address"));

        var single = summaries.Single(summary => Text(summary, "conversationId") == "conv-2");
        Assert.Equal(1, single.GetProperty("messageCount").GetInt32());
        Assert.False(single.GetProperty("isThread").GetBoolean());
        Assert.Empty(single.GetProperty("participants").EnumerateArray());

        // Exactly one Exchange query, for exactly the conversations asked about.
        Assert.Equal(1, mail.ConversationLookupCalls);
        Assert.Equal(["conv-1", "conv-2"], Assert.Single(mail.RequestedConversations));
    }

    [Fact]
    public async Task AConversationExchangeNoLongerHolds_IsOmitted_NotInvented()
    {
        var mail = new FakeExchangeMailService();
        mail.ArrangeConversation("conv-1", 2);
        var client = CreateClient(mail);

        var payload = await PostJsonAsync(client, new { conversationIds = new[] { "conv-1", "gone-from-mailbox" } });

        Assert.Equal(["conv-1"], payload.EnumerateArray().Select(summary => Text(summary, "conversationId")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"conversationIds\":[]}")]
    [InlineData("{\"conversationIds\":[\"   \",\"\"]}")]
    public async Task AnEmptyRequest_IsACallerError_AndNeverTouchesExchange(string body)
    {
        var mail = new FakeExchangeMailService();
        var client = CreateClient(mail);

        using var response = await client.PostAsync(
            "/api/conversations/summary", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, mail.ConversationLookupCalls);
    }

    [Fact]
    public async Task MoreConversationsThanOneLookupAllows_IsRejectedBeforeExchange()
    {
        var mail = new FakeExchangeMailService();
        var client = CreateClient(mail);
        var tooMany = Enumerable.Range(0, ConversationSummary.MaxLookupBatch + 1)
            .Select(index => $"conv-{index}")
            .ToArray();

        using var response = await client.PostAsJsonAsync("/api/conversations/summary", new { conversationIds = tooMany });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, mail.ConversationLookupCalls);
    }

    [Fact]
    public async Task ADuplicateId_IsAskedAboutOnce()
    {
        var mail = new FakeExchangeMailService();
        mail.ArrangeConversation("conv-1", 2);
        var client = CreateClient(mail);

        await PostJsonAsync(client, new { conversationIds = new[] { "conv-1", "conv-1", "  " } });

        Assert.Equal(["conv-1"], Assert.Single(mail.RequestedConversations));
    }

    [Fact]
    public async Task AMailboxFailure_IsATypedAnswer_WithoutInternals()
    {
        var mail = new FakeExchangeMailService
        {
            FailNextConversationLookupKind = ExchangeMailErrorKind.MailboxError,
        };
        var client = CreateClient(mail);

        using var response = await client.PostAsJsonAsync(
            "/api/conversations/summary", new { conversationIds = new[] { "conv-1" } });
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        using var document = JsonDocument.Parse(raw);
        Assert.Equal("exchange_mailbox_error", Text(document.RootElement.GetProperty("error"), "code"));
        Assert.DoesNotContain("Injected typed Exchange failure", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTypedClient_PostsTheIdsAndReadsTheThreadStateBack()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, """
            [
              {
                "conversationId": "conv-1",
                "topic": "Budget review",
                "messageCount": 3,
                "participants": [ { "name": "Sara", "address": "sara@contoso.com" } ],
                "isKnown": true,
                "hasConversation": true,
                "isThread": true
              }
            ]
            """));
        var client = new EmailApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        var summaries = await client.GetConversationSummariesAsync(["conv-1"], CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://localhost/api/conversations/summary", request.Url.ToString());
        Assert.Contains("conversationIds", request.Body, StringComparison.Ordinal);
        Assert.Contains("conv-1", request.Body, StringComparison.Ordinal);

        var state = Assert.Single(summaries);
        Assert.True(state.IsThread);
        Assert.Equal(3, state.MessageCount);
        Assert.Equal("sara@contoso.com", Assert.Single(state.Participants).Address);
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync("/api/conversations/summary", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static string? Text(JsonElement element, string property)
        => element.GetProperty(property).GetString();
}

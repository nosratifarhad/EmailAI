using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EmailAI.Application.AI;
using EmailAI.Application.Exchange;
using EmailAI.Domain.Exchange;
using EmailAI.Domain.Mail;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EmailAI.Tests;

/// <summary>
/// The AI language contract through the REAL host: a documented language name reaches
/// <see cref="IAiService"/> unchanged (together with the Exchange-derived current user), a
/// numeric value keeps working for older clients, and an unusable value is a 400 with a message
/// that never reaches the assistant or Exchange.
/// </summary>
public sealed class AiLanguageEndpointTests : IClassFixture<SettingsHostFactory>
{
    private const string ItemId = "item-42";

    private readonly SettingsHostFactory _factory;

    public AiLanguageEndpointTests(SettingsHostFactory factory) => _factory = factory;

    private (HttpClient Client, RecordingAiService Ai, FakeExchangeMailService Mail, MailboxIdentity Identity) CreateHost()
    {
        var ai = new RecordingAiService();
        var mail = new FakeExchangeMailService();
        mail.ArrangeMessage(new EmailMessage
        {
            Id = ItemId,
            Subject = "Budget review",
            From = new EmailAddress("Sara Ahmadi", "sara@contoso.com"),
            BodyText = "Please review the budget.",
        });

        var identity = MailboxIdentity.Create(
            "Alex Doe",
            "alex@contoso.com",
            "CONTOSO\\alex",
            MailboxIdentity.ExchangeDirectorySource);

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureServices(services =>
            {
                SettingsHostFactory.RemoveRegistrations<IExchangeMailService>(services);
                SettingsHostFactory.RemoveRegistrations<IMailboxIdentityProvider>(services);
                SettingsHostFactory.RemoveRegistrations<IAiService>(services);
                services.AddSingleton<IExchangeMailService>(mail);
                services.AddSingleton<IMailboxIdentityProvider>(new StubMailboxIdentityProvider(identity));
                services.AddSingleton<IAiService>(ai);
            });
        }).CreateClient();

        return (client, ai, mail, identity);
    }

    [Fact]
    public async Task DocumentedName_ReachesTheAssistant_WithTheCurrentUser()
    {
        var host = CreateHost();

        using var response = await host.Client.PostAsJsonAsync(
            $"/api/ai/messages/{ItemId}/suggest-reply",
            new { language = "Persian" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(host.Ai.Calls);
        Assert.Equal("suggest-reply", call.Operation);
        Assert.Equal(AiLanguage.Persian, call.Language);
        Assert.Equal(host.Identity, call.CurrentUser);
    }

    [Fact]
    public async Task NumericValue_KeepsWorking_ForOlderClients()
    {
        var host = CreateHost();

        using var response = await host.Client.PostAsJsonAsync(
            $"/api/ai/messages/{ItemId}/summarize",
            new { language = 2 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AiLanguage.Persian, Assert.Single(host.Ai.Calls).Language);
    }

    [Fact]
    public async Task OmittedLanguage_DefaultsToAuto()
    {
        var host = CreateHost();

        using var response = await host.Client.PostAsJsonAsync(
            $"/api/ai/messages/{ItemId}/generate-reply",
            new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AiLanguage.Auto, Assert.Single(host.Ai.Calls).Language);
    }

    [Fact]
    public async Task UnsupportedName_Answers400WithAMessage_AndNeverCallsTheAssistant()
    {
        var host = CreateHost();

        using var response = await host.Client.PostAsJsonAsync(
            $"/api/ai/messages/{ItemId}/suggest-reply",
            new { language = "Klingon" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var message = payload.GetProperty("error").GetString();
        Assert.NotNull(message);
        Assert.Contains("Klingon", message, StringComparison.Ordinal);
        Assert.Empty(host.Ai.Calls);
        Assert.Equal(0, host.Mail.ListCalls);
    }

    [Fact]
    public async Task UnsupportedLanguage_OnThreadSummary_IsAlsoRejectedBeforeAnyLookup()
    {
        var host = CreateHost();

        using var response = await host.Client.PostAsJsonAsync(
            $"/api/ai/messages/{ItemId}/thread-summarize",
            new { language = "fr" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Ai.Calls);
        Assert.Equal(0, host.Mail.ListCalls);
    }
}

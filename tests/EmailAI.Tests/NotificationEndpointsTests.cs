using System.Net;
using System.Text.Json;
using EmailAI.Application.Exchange;
using Microsoft.Extensions.DependencyInjection;

namespace EmailAI.Tests;

/// <summary>
/// The new-mail feed the Windows/Electron shell polls (GET /api/notifications/mail), booted in
/// the real host with in-memory Exchange. It must establish a baseline first, report only mail
/// that arrived afterwards, never duplicate an item, keep folders independent, and return
/// header data only.
/// </summary>
public sealed class NotificationEndpointsTests : IClassFixture<SettingsHostFactory>
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly SettingsHostFactory _factory;

    public NotificationEndpointsTests(SettingsHostFactory factory) => _factory = factory;

    private HttpClient CreateClient(FakeExchangeMailService mail)
        => _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            SettingsHostFactory.RemoveRegistrations<IExchangeMailService>(services);
            services.AddSingleton<IExchangeMailService>(mail);
        })).CreateClient();

    [Fact]
    public async Task FirstPollIsABaseline_ThenOnlyNewMailIsReported()
    {
        var mail = new FakeExchangeMailService();
        mail.Add("inbox", "id-1", T0, subject: "Existing");
        var client = CreateClient(mail);

        var baseline = await GetAsync(client, "/api/notifications/mail");
        Assert.Equal("inbox", Text(baseline, "folder"));
        Assert.True(baseline.GetProperty("baseline").GetBoolean());
        Assert.Equal(0, baseline.GetProperty("count").GetInt32());
        Assert.Empty(baseline.GetProperty("items").EnumerateArray());

        mail.Add("inbox", "id-2", T0.AddMinutes(30), subject: "Fresh", fromName: "Ali", fromAddress: "ali@example.com");

        var second = await GetAsync(client, "/api/notifications/mail");
        Assert.False(second.GetProperty("baseline").GetBoolean());
        Assert.Equal(1, second.GetProperty("count").GetInt32());

        var item = Assert.Single(second.GetProperty("items").EnumerateArray());
        Assert.Equal("id-2", Text(item, "id"));
        Assert.Equal("Ali", Text(item, "fromName"));
        Assert.Equal("ali@example.com", Text(item, "fromAddress"));
        Assert.Equal("Fresh", Text(item, "subject"));
        Assert.Equal(T0.AddMinutes(30), item.GetProperty("receivedAt").GetDateTimeOffset());

        // Re-polling the same folder reports nothing again (no duplicate notifications).
        var third = await GetAsync(client, "/api/notifications/mail");
        Assert.Equal(0, third.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task PayloadCarriesHeaderDataOnly_NeverABody()
    {
        var mail = new FakeExchangeMailService();
        mail.Add("inbox", "seed", T0);
        var client = CreateClient(mail);
        await GetAsync(client, "/api/notifications/mail");

        mail.Add("inbox", "id-2", T0.AddMinutes(1), subject: "A subject with body-like words");
        var payload = await GetAsync(client, "/api/notifications/mail");

        // The envelope and every item carry exactly the documented header members.
        Assert.Equal(
            ["folder", "baseline", "count", "items"],
            payload.EnumerateObject().Select(property => property.Name));

        var item = Assert.Single(payload.GetProperty("items").EnumerateArray());
        Assert.Equal(
            ["id", "fromName", "fromAddress", "subject", "receivedAt"],
            item.EnumerateObject().Select(property => property.Name));

        var raw = await client.GetStringAsync("/api/notifications/mail");
        Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FolderQueryParameter_KeepsABaselinePerFolder()
    {
        var mail = new FakeExchangeMailService();
        mail.Add("inbox", "in-1", T0, subject: "Inbox");
        mail.Add("sent", "sent-1", T0, subject: "Sent");
        var client = CreateClient(mail);

        var inbox = await GetAsync(client, "/api/notifications/mail?folder=inbox");
        Assert.Equal("inbox", Text(inbox, "folder"));
        Assert.True(inbox.GetProperty("baseline").GetBoolean());

        var sent = await GetAsync(client, "/api/notifications/mail?folder=sent");
        Assert.Equal("sent", Text(sent, "folder"));
        // The first poll of a DIFFERENT folder is still a fresh baseline for that folder.
        Assert.True(sent.GetProperty("baseline").GetBoolean());

        mail.Add("sent", "sent-2", T0.AddMinutes(10), subject: "Sent arrival");
        var sentAgain = await GetAsync(client, "/api/notifications/mail?folder=sent");

        Assert.Equal("sent-2", Text(Assert.Single(sentAgain.GetProperty("items").EnumerateArray()), "id"));
        Assert.Equal(["inbox", "sent", "sent"], mail.RequestedFolders);
    }

    [Fact]
    public async Task EveryPollCostsExactlyOneHeaderQuery()
    {
        var mail = new FakeExchangeMailService();
        mail.Add("inbox", "seed", T0);
        var client = CreateClient(mail);

        await client.GetAsync("/api/notifications/mail");
        await client.GetAsync("/api/notifications/mail");

        Assert.Equal(2, mail.ListCalls);
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static string? Text(JsonElement element, string property)
        => element.GetProperty(property).GetString();
}

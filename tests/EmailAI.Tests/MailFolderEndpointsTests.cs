using System.Net;
using System.Text.Json;
using EmailAI.Application.Exceptions;
using EmailAI.Application.Exchange;
using Microsoft.Extensions.DependencyInjection;

namespace EmailAI.Tests;

/// <summary>
/// Custom mailbox folders through the REAL host: GET /api/folders/{folderKey}/children reports the
/// folders Exchange holds below a parent, and a custom folder key addresses messages exactly like a
/// well-known one. The folder walk is the same walk the first paint must NOT pay for, the keys
/// survive the URL unchanged, and every failure is a typed, secret-free answer.
/// </summary>
public sealed class MailFolderEndpointsTests : IClassFixture<SettingsHostFactory>
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly SettingsHostFactory _factory;

    public MailFolderEndpointsTests(SettingsHostFactory factory) => _factory = factory;

    private HttpClient CreateClient(FakeExchangeMailService mail)
        => _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            SettingsHostFactory.RemoveRegistrations<IExchangeMailService>(services);
            services.AddSingleton<IExchangeMailService>(mail);
        })).CreateClient();

    [Fact]
    public async Task ChildFolders_OfInbox_AreTheCustomFoldersExchangeReports()
    {
        var customers = FakeExchangeMailService.CustomKey("inbox-child-a");
        var projects = FakeExchangeMailService.CustomKey("inbox-child-b");
        var mail = new FakeExchangeMailService();
        mail.ArrangeChildren(
            "inbox",
            FakeExchangeMailService.Child(customers, "Customers", hasChildren: true),
            FakeExchangeMailService.Child(projects, "Projects"));
        var client = CreateClient(mail);

        var payload = await GetJsonAsync(client, "/api/folders/inbox/children");

        var folders = payload.EnumerateArray().ToArray();
        Assert.Equal(2, folders.Length);

        // The documented shape: identity, name, parent, type and whether anything is nested below.
        Assert.Equal(
            ["displayName", "hasChildren", "id", "parentId", "wellKnownType"],
            folders[0].EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));

        var reported = folders.Single(folder => Text(folder, "displayName") == "Customers");
        Assert.Equal(customers, Text(reported, "id"));
        Assert.Equal("inbox", Text(reported, "parentId"));
        Assert.Equal("custom", Text(reported, "wellKnownType"));
        Assert.True(reported.GetProperty("hasChildren").GetBoolean());
        Assert.False(folders.Single(folder => Text(folder, "displayName") == "Projects")
            .GetProperty("hasChildren").GetBoolean());

        // Exactly one Exchange query, for exactly the folder the user expanded.
        Assert.Equal(1, mail.FolderListCalls);
        Assert.Equal(["inbox"], mail.RequestedFolderParents);
    }

    [Fact]
    public async Task ChildFolders_OnlyReportTheChildrenOfTheRequestedFolder()
    {
        var customers = FakeExchangeMailService.CustomKey("inbox-child-a");
        var nested = FakeExchangeMailService.CustomKey("nested-child");
        var mail = new FakeExchangeMailService();
        mail.ArrangeChildren("inbox", FakeExchangeMailService.Child(customers, "Customers"));
        mail.ArrangeChildren(customers, FakeExchangeMailService.Child(nested, "Nested", parentKey: customers));
        var client = CreateClient(mail);

        var inboxChildren = await GetJsonAsync(client, "/api/folders/inbox/children");
        Assert.Equal(["Customers"], Names(inboxChildren));

        // A folder deeper in the tree is a child of ITS parent, never of Inbox ...
        var parentChildren = await GetJsonAsync(client, $"/api/folders/{Uri.EscapeDataString(customers)}/children");
        Assert.Equal(["Nested"], Names(parentChildren));

        Assert.Equal(["inbox", customers], mail.RequestedFolderParents);
    }

    [Fact]
    public async Task ChildFolders_WithAnUnsupportedFolderKey_Answer400()
    {
        var mail = new FakeExchangeMailService();
        var client = CreateClient(mail);

        var (status, body, _) = await GetRawAsync(client, "/api/folders/nonsense/children");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad_request", Text(body.GetProperty("error"), "code"));
    }

    [Fact]
    public async Task ChildFolders_WhenExchangeIsUnavailable_AnswerATypedFailureWithoutInternals()
    {
        var mail = new FakeExchangeMailService { FailNextChildFoldersKind = ExchangeMailErrorKind.Connectivity };
        var client = CreateClient(mail);

        var (status, body, raw) = await GetRawAsync(client, "/api/folders/inbox/children");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("exchange_connection_failed", Text(body.GetProperty("error"), "code"));
        Assert.DoesNotContain("Injected typed Exchange failure", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ExchangeMailException", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChildFolders_WhenTheFolderIsGone_AnswerFolderNotFound()
    {
        var mail = new FakeExchangeMailService { FailNextChildFoldersKind = ExchangeMailErrorKind.FolderNotFound };
        var client = CreateClient(mail);

        var (status, body, _) = await GetRawAsync(client, "/api/folders/inbox/children");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("folder_not_found", Text(body.GetProperty("error"), "code"));
    }

    [Fact]
    public async Task Messages_OfACustomFolder_AreListedFromThatFolderOnly()
    {
        // A key built from an id that is full of URL-hostile characters: if the key did not survive
        // the request path unchanged, the wrong folder (or none) would be listed.
        var key = FakeExchangeMailService.CustomKey("AQMkADYy/abc+def==XYZ");
        var mail = new FakeExchangeMailService();
        mail.Add(key, "custom-1", T0, subject: "Only in Customers");
        mail.Add("inbox", "inbox-1", T0, subject: "Only in Inbox");
        var client = CreateClient(mail);

        var payload = await GetJsonAsync(
            client, $"/api/folders/{Uri.EscapeDataString(key)}/messages?offset=0&pageSize=20");

        Assert.Equal(1, payload.GetProperty("totalCount").GetInt32());
        var item = Assert.Single(payload.GetProperty("items").EnumerateArray());
        Assert.Equal("Only in Customers", Text(item, "subject"));
        Assert.Equal([key], mail.RequestedFolders);
    }

    [Fact]
    public async Task AnEmptyCustomFolder_IsAnEmptyList_NotAnError()
    {
        var key = FakeExchangeMailService.CustomKey("empty-folder");
        var mail = new FakeExchangeMailService();
        mail.Arrange(key); // the folder exists and holds nothing
        var client = CreateClient(mail);

        var payload = await GetJsonAsync(client, $"/api/folders/{Uri.EscapeDataString(key)}/messages");

        Assert.Equal(0, payload.GetProperty("totalCount").GetInt32());
        Assert.Empty(payload.GetProperty("items").EnumerateArray());
        Assert.False(payload.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task Messages_OfAFolderThatNoLongerExists_AnswerFolderNotFound()
    {
        var key = FakeExchangeMailService.CustomKey("deleted-in-outlook");
        var mail = new FakeExchangeMailService { FailNextListKind = ExchangeMailErrorKind.FolderNotFound };
        var client = CreateClient(mail);

        var (status, body, raw) = await GetRawAsync(client, $"/api/folders/{Uri.EscapeDataString(key)}/messages");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("folder_not_found", Text(body.GetProperty("error"), "code"));
        Assert.DoesNotContain("Injected typed Exchange failure", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Messages_WhenFolderAccessIsDenied_AnswerATypedFailure()
    {
        var key = FakeExchangeMailService.CustomKey("no-permission");
        var mail = new FakeExchangeMailService { FailNextListKind = ExchangeMailErrorKind.Authentication };
        var client = CreateClient(mail);

        var (status, body, _) = await GetRawAsync(client, $"/api/folders/{Uri.EscapeDataString(key)}/messages");

        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Equal("exchange_authentication_failed", Text(body.GetProperty("error"), "code"));
    }

    private static IEnumerable<string?> Names(JsonElement array)
        => array.EnumerateArray().Select(folder => Text(folder, "displayName"));

    private static string? Text(JsonElement element, string property)
        => element.GetProperty(property).GetString();

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body, string Raw)> GetRawAsync(
        HttpClient client,
        string url)
    {
        using var response = await client.GetAsync(url);
        var raw = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(raw);
        return (response.StatusCode, document.RootElement.Clone(), raw);
    }
}

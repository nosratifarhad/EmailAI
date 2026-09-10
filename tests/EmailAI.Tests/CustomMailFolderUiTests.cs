using System.Net;
using EmailAI.Api.Web;
using EmailAI.Application.Exceptions;
using EmailAI.Application.Exchange;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EmailAI.Tests;

/// <summary>
/// The folder sidebar as the user first sees it (prerendered HTML) and the typed client call behind
/// the expand action. Two properties matter here:
///
/// 1. The first paint renders the folder hierarchy control but performs NO folder walk - the custom
///    folders are discovered only when the user expands Inbox, so the initial Inbox load stays
///    exactly as cheap as it was before this feature.
/// 2. The custom folders come from Exchange: nothing is hard-coded, and the request the UI makes
///    carries the encoded folder key, so the key survives the URL unchanged.
/// </summary>
public sealed class CustomMailFolderUiTests : IClassFixture<SettingsHostFactory>
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly SettingsHostFactory _factory;

    public CustomMailFolderUiTests(SettingsHostFactory factory) => _factory = factory;

    [Fact]
    public async Task FirstPaint_OffersTheExpandControlForInbox_AndQueriesNoFoldersAtAll()
    {
        var mail = new FakeExchangeMailService();
        mail.Add("inbox", "id-1", T0, subject: "Quarterly report");
        mail.ArrangeChildren(
            "inbox",
            FakeExchangeMailService.Child(FakeExchangeMailService.CustomKey("a"), "Customers"));

        var html = await CreateClient(mail).GetStringAsync("/");

        // The arrow is there (expanding is a separate interaction from selecting Inbox) ...
        Assert.Contains("aria-label=\"Expand Inbox\"", html, StringComparison.Ordinal);

        // ... and the custom folders are NOT: the folder walk belongs to the expand action.
        Assert.Equal(0, mail.FolderListCalls);
        Assert.DoesNotContain("Customers", html, StringComparison.Ordinal);

        // The initial Inbox load is exactly as cheap as before this feature.
        Assert.Equal(1, mail.ListCalls);
        Assert.Equal(["inbox"], mail.RequestedFolders);
    }

    [Fact]
    public async Task FirstPaint_KeepsEveryTopLevelFolder_AndGivesOnlyInboxAnArrow()
    {
        var mail = new FakeExchangeMailService();
        mail.Add("inbox", "id-1", T0, subject: "Quarterly report");

        var html = await CreateClient(mail).GetStringAsync("/");

        foreach (var folder in FolderCatalog.All)
        {
            Assert.Contains($">{folder.Label}</span>", html, StringComparison.Ordinal);
        }

        // One expandable folder (Inbox) and one alignment spacer for each of the other five.
        Assert.Equal(1, CountOf(html, "class=\"folder-toggle\""));
        Assert.Equal(FolderCatalog.All.Count - 1, CountOf(html, "folder-toggle-empty"));

        // The sidebar keeps its documented order: Inbox, then the other top-level folders.
        var inbox = html.IndexOf(">Inbox</span>", StringComparison.Ordinal);
        var sent = html.IndexOf(">Sent</span>", StringComparison.Ordinal);
        var deleted = html.IndexOf(">Deleted</span>", StringComparison.Ordinal);
        Assert.True(inbox >= 0 && sent > inbox && deleted > sent);
    }

    [Fact]
    public async Task FirstPaint_StillShowsTheInboxMessages_WhenTheFolderWalkWouldFail()
    {
        var mail = new FakeExchangeMailService();
        mail.Add("inbox", "id-1", T0, subject: "Quarterly report");
        mail.FailNextChildFoldersKind = ExchangeMailErrorKind.Connectivity;

        var html = await CreateClient(mail).GetStringAsync("/");

        // A broken folder walk cannot break the mail the user came for (it is not even attempted).
        Assert.Contains("Quarterly report", html, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be loaded.", html, StringComparison.Ordinal);
        Assert.Equal(0, mail.FolderListCalls);
    }

    [Fact]
    public async Task ApiClient_RequestsTheChildrenEndpointWithTheEncodedFolderKey()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, """
            [
              {
                "id": "folder:4142",
                "displayName": "Customers",
                "parentId": "inbox",
                "wellKnownType": "custom",
                "hasChildren": true
              }
            ]
            """));
        var client = new EmailApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        var folders = await client.GetChildFoldersAsync("folder:4142", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://localhost/api/folders/folder%3A4142/children", request.Url.ToString());

        var folder = Assert.Single(folders);
        Assert.Equal("folder:4142", folder.Id);
        Assert.Equal("Customers", folder.DisplayName);
        Assert.Equal("inbox", folder.ParentId);
        Assert.Equal("custom", folder.WellKnownType);
        Assert.True(folder.HasChildren);
    }

    /// <summary>
    /// Creates a client for the real host with in-memory Exchange. The prerendering page calls the
    /// application's own REST API with an HttpClient whose base address is the request's origin, so
    /// the DI HttpClient is replaced by one that loops straight back into the in-memory test server.
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

    private static int CountOf(string html, string value)
        => html.Split(value, StringSplitOptions.None).Length - 1;
}

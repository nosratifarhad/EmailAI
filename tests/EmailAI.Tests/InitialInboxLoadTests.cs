using System.Net.Http;
using EmailAI.Application.Exchange;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EmailAI.Tests;

/// <summary>
/// The initial Inbox load. The folder page is fetched in the component's INITIALIZATION
/// pipeline (prerendering AND the interactive circuit), so the very first HTML the user sees
/// already contains the messages. This test proves the first paint is never the empty/loading
/// state, and that the browser loop back to the app's own API is exercised exactly once.
/// </summary>
public sealed class InitialInboxLoadTests : IClassFixture<SettingsHostFactory>
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly SettingsHostFactory _factory;

    public InitialInboxLoadTests(SettingsHostFactory factory) => _factory = factory;

    [Fact]
    public async Task RootPage_AlreadyContainsTheInboxMessages_OnTheFirstPaint()
    {
        var mail = new FakeExchangeMailService();
        mail.Add("inbox", "id-1", T0, subject: "Quarterly report", fromName: "Sara Ahmadi", fromAddress: "sara@example.com");
        mail.Add("inbox", "id-2", T0.AddMinutes(-10), subject: "Second message", fromName: "Ali", fromAddress: "ali@example.com");

        var client = CreateClient(mail);

        var html = await client.GetStringAsync("/");

        Assert.Contains("Quarterly report", html, StringComparison.Ordinal);
        Assert.Contains("Second message", html, StringComparison.Ordinal);
        Assert.Contains("message-row", html, StringComparison.Ordinal);
        // Menu/config errors must not have swallowed the list.
        Assert.DoesNotContain("is empty.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Loading messages", html, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be loaded.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RootPage_QueriesOnlyTheInbox_OnceForTheFirstPaint()
    {
        var mail = new FakeExchangeMailService();
        mail.Add("inbox", "id-1", T0, subject: "One message");

        var client = CreateClient(mail);
        await client.GetStringAsync("/");

        Assert.Equal(["inbox"], mail.RequestedFolders);
        Assert.Equal(1, mail.ListCalls);
    }

    [Fact]
    public async Task RootPage_WithAnEmptyInbox_ShowsTheEmptyStateInsteadOfAnError()
    {
        var client = CreateClient(new FakeExchangeMailService());

        var html = await client.GetStringAsync("/");

        Assert.Contains("is empty.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be loaded.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RootPage_RetriesOneTransientConnectionFailure_BeforeShowingAnError()
    {
        var mail = new FakeExchangeMailService();
        mail.Add("inbox", "id-1", T0, subject: "Quarterly report");
        mail.FailNextListCalls = 1;

        var client = CreateClient(mail);

        var html = await client.GetStringAsync("/");

        // The first paint still carries the Inbox: one transparent retry, no user action, no delay.
        Assert.Contains("Quarterly report", html, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be loaded.", html, StringComparison.Ordinal);
        Assert.Equal(2, mail.ListCalls);
    }

    [Fact]
    public async Task RootPage_AfterTwoFailures_ReportsTheRealError()
    {
        var mail = new FakeExchangeMailService();
        mail.Add("inbox", "id-1", T0, subject: "Quarterly report");
        mail.FailNextListCalls = 2;

        var client = CreateClient(mail);

        var html = await client.GetStringAsync("/");

        Assert.DoesNotContain("message-row", html, StringComparison.Ordinal);
        Assert.Contains("Exchange connection failed", html, StringComparison.Ordinal);
        Assert.Equal(2, mail.ListCalls);
    }

    /// <summary>
    /// Creates a client for the real host with in-memory Exchange. The prerendering page calls
    /// the application's own REST API with an HttpClient whose base address is the request's
    /// origin, so the DI HttpClient is replaced by one that loops straight back into the
    /// in-memory test server (instead of trying to open a real socket on port 80).
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

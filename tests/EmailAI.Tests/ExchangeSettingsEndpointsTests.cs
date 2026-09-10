using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EmailAI.Application.Settings;
using EmailAI.Domain.Exchange;
using EmailAI.Domain.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace EmailAI.Tests;

/// <summary>
/// The Exchange Settings API against the real host with fresh in-memory stores:
///   * GET never returns the password (or a deployment-managed account value),
///   * PUT writes the endpoint/mode/account to the per-user document and the password to the
///     credential store (target EmailAI/Exchange) only - never into JSON, never to the client,
///   * DELETE returns the deployment to its environment configuration and clears the secret,
///   * POST /test probes a draft without persisting anything and answers with a secret-free
///     status,
///   * Exchange operations never touch the AI API key.
/// </summary>
public sealed class ExchangeSettingsEndpointsTests : IClassFixture<SettingsHostFactory>
{
    private const string Url = "https://mail.contoso.com/EWS/Exchange.asmx";
    private const string Password = "super-secret-pw";

    private readonly SettingsHostFactory _factory;

    public ExchangeSettingsEndpointsTests(SettingsHostFactory factory) => _factory = factory;

    private HttpClient CreateClient(
        out InMemoryUserSettingsStore settings,
        out InMemorySecretStore secrets,
        IExchangeConnectionTester? tester = null)
    {
        var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            if (tester is not null)
            {
                SettingsHostFactory.RemoveRegistrations<IExchangeConnectionTester>(services);
                services.AddSingleton(tester);
            }
        }));

        var client = factory.CreateClient();
        settings = (InMemoryUserSettingsStore)factory.Services.GetRequiredService<IUserSettingsStore>();
        secrets = (InMemorySecretStore)factory.Services.GetRequiredService<ISecretStore>();
        return client;
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        var raw = await client.GetStringAsync(path);
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task Get_WithoutPerUserConfiguration_ReportsTheDeploymentConfiguration()
    {
        var client = CreateClient(out var settings, out _);

        var root = await GetJsonAsync(client, "/api/settings/exchange");

        Assert.True(root.GetProperty("configured").GetBoolean());
        Assert.False(root.GetProperty("userManaged").GetBoolean());
        Assert.Equal("http://127.0.0.1:1/EWS/Exchange.asmx", root.GetProperty("ewsUrl").GetString());
        Assert.Equal("Windows", root.GetProperty("authenticationMode").GetString());
        Assert.True(root.GetProperty("usesDefaultCredentials").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("username").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("domain").ValueKind);
        Assert.False(root.GetProperty("hasPassword").GetBoolean());

        Assert.Null(settings.Stored.Exchange);
    }

    [Fact]
    public async Task Put_WindowsMode_SavesTheEndpointAndNeverStoresAPassword()
    {
        var client = CreateClient(out var settings, out var secrets);

        var put = await client.PutAsJsonAsync("/api/settings/exchange", new
        {
            ewsUrl = Url,
            authentication = "Windows",
            username = "mail-bot",
            domain = "CONTOSO",
            password = Password,
        });

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var stored = settings.Stored.Exchange;
        Assert.NotNull(stored);
        Assert.Equal(Url, stored!.EwsUrl);
        Assert.Equal(ExchangeOptions.WindowsMode, stored.Authentication);
        // Windows mode stores no account and no password.
        Assert.Null(stored.Username);
        Assert.Null(stored.Domain);
        Assert.Empty(secrets.Targets);

        var root = await GetJsonAsync(client, "/api/settings/exchange");
        Assert.True(root.GetProperty("userManaged").GetBoolean());
        Assert.True(root.GetProperty("usesDefaultCredentials").GetBoolean());
        Assert.Equal(Url, root.GetProperty("ewsUrl").GetString());
        Assert.False(root.GetProperty("hasPassword").GetBoolean());
    }

    [Fact]
    public async Task Put_UsernamePassword_StoresThePasswordOnlyInTheCredentialStore()
    {
        var client = CreateClient(out var settings, out var secrets);

        var put = await client.PutAsJsonAsync("/api/settings/exchange", new
        {
            ewsUrl = Url,
            authentication = "UsernamePassword",
            username = "mail-bot",
            domain = "CONTOSO",
            password = Password,
        });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        // Secret isolation: only the Exchange target is written, never the AI key target.
        Assert.Equal(Password, await secrets.ReadAsync(SecretTargets.ExchangePassword));
        Assert.Equal(new[] { SecretTargets.ExchangePassword }, secrets.Targets);

        var raw = await client.GetStringAsync("/api/settings/exchange");
        Assert.DoesNotContain(Password, raw, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        Assert.True(root.GetProperty("userManaged").GetBoolean());
        Assert.Equal("UsernamePassword", root.GetProperty("authenticationMode").GetString());
        Assert.False(root.GetProperty("usesDefaultCredentials").GetBoolean());
        // The user's own account is echoed back; the password only as presence.
        Assert.Equal("mail-bot", root.GetProperty("username").GetString());
        Assert.Equal("CONTOSO", root.GetProperty("domain").GetString());
        Assert.True(root.GetProperty("hasPassword").GetBoolean());

        Assert.Equal(ExchangeOptions.UsernamePasswordMode, settings.Stored.Exchange?.Authentication);
    }

    [Fact]
    public async Task Put_WithAnIncompleteDraft_ReturnsBadRequestWithoutSideEffects()
    {
        var client = CreateClient(out var settings, out var secrets);

        var put = await client.PutAsJsonAsync("/api/settings/exchange", new
        {
            ewsUrl = Url,
            authentication = "UsernamePassword",
            password = Password,
        });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var body = await put.Content.ReadAsStringAsync();
        Assert.Contains("EXCHANGE_USERNAME", body, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, body, StringComparison.Ordinal);

        Assert.Null(settings.Stored.Exchange);
        Assert.Empty(secrets.Targets);
    }

    [Fact]
    public async Task Put_WithAnInvalidEndpoint_ReturnsBadRequestWithoutSideEffects()
    {
        var client = CreateClient(out var settings, out _);

        var put = await client.PutAsJsonAsync("/api/settings/exchange", new
        {
            ewsUrl = "not-a-url",
            authentication = "Windows",
        });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Null(settings.Stored.Exchange);
    }

    [Fact]
    public async Task Delete_RemovesTheConfigurationAndThePassword_AndFallsBackToTheDeployment()
    {
        var client = CreateClient(out var settings, out var secrets);
        await client.PutAsJsonAsync("/api/settings/exchange", new
        {
            ewsUrl = Url,
            authentication = "UsernamePassword",
            username = "mail-bot",
            password = Password,
        });

        var delete = await client.DeleteAsync("/api/settings/exchange");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        Assert.Null(settings.Stored.Exchange);
        Assert.Empty(secrets.Targets);

        var root = await GetJsonAsync(client, "/api/settings/exchange");
        Assert.False(root.GetProperty("userManaged").GetBoolean());
        Assert.Equal("http://127.0.0.1:1/EWS/Exchange.asmx", root.GetProperty("ewsUrl").GetString());
    }

    [Fact]
    public async Task Post_Test_ProbesTheDraftWithoutPersistingIt()
    {
        var tester = new RecordingExchangeConnectionTester(new ExchangeHealthStatus(true, 7, null));
        var client = CreateClient(out var settings, out var secrets, tester);

        var response = await client.PostAsJsonAsync("/api/settings/exchange/test", new
        {
            ewsUrl = Url,
            authentication = "UsernamePassword",
            username = "mail-bot",
            domain = "CONTOSO",
            password = Password,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Password, raw, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(raw);
        Assert.Equal("connected", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(7, document.RootElement.GetProperty("latencyMs").GetInt64());

        // The draft reached the tester unchanged and nothing was written anywhere.
        Assert.NotNull(tester.LastOptions);
        Assert.Equal(Url, tester.LastOptions!.EwsUrl);
        Assert.Equal("mail-bot", tester.LastOptions.Username);
        Assert.Equal(Password, tester.LastOptions.Password);
        Assert.Null(settings.Stored.Exchange);
        Assert.Empty(secrets.Targets);
    }

    [Fact]
    public async Task Post_Test_WhenTheProbeFails_ReturnsASecretFreeStatus()
    {
        var tester = new RecordingExchangeConnectionTester(new ExchangeHealthStatus(
            false,
            12,
            "Exchange authentication failed: the current Windows account was rejected."));
        var client = CreateClient(out _, out _, tester);

        var response = await client.PostAsJsonAsync("/api/settings/exchange/test", new
        {
            ewsUrl = Url,
            authentication = "Windows",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
        Assert.Contains(
            "rejected",
            document.RootElement.GetProperty("error").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_Test_WithAnIncompleteDraft_ReturnsBadRequest()
    {
        var tester = new RecordingExchangeConnectionTester(new ExchangeHealthStatus(true, 1, null));
        var client = CreateClient(out _, out _, tester);

        var response = await client.PostAsJsonAsync("/api/settings/exchange/test", new
        {
            ewsUrl = Url,
            authentication = "UsernamePassword",
            username = "mail-bot",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, tester.Calls);
    }

    [Fact]
    public async Task ExchangeSettings_NeverDisturbTheAiConfiguration()
    {
        var client = CreateClient(out var settings, out var secrets);
        await client.PutAsJsonAsync("/api/settings/ai", new
        {
            baseUrl = "https://provider.test/v1",
            model = "gpt-5",
            apiKey = "sk-ai-key",
        });

        await client.PutAsJsonAsync("/api/settings/exchange", new
        {
            ewsUrl = Url,
            authentication = "UsernamePassword",
            username = "mail-bot",
            password = Password,
        });

        // Both sections coexist in the same document and the AI provider is untouched.
        Assert.Equal("https://provider.test/v1", settings.Stored.Ai?.BaseUrl);
        Assert.Equal("gpt-5", settings.Stored.Ai?.Model);
        Assert.Equal(Url, settings.Stored.Exchange?.EwsUrl);

        var aiRaw = await client.GetStringAsync("/api/settings/ai");
        Assert.DoesNotContain(Password, aiRaw, StringComparison.Ordinal);

        var ai = await GetJsonAsync(client, "/api/settings/ai");
        Assert.Equal("https://provider.test/v1", ai.GetProperty("baseUrl").GetString());

        // The two secrets live under separate targets and are never mixed.
        Assert.Equal("sk-ai-key", await secrets.ReadAsync(SecretTargets.AiApiKey));
        Assert.Equal(Password, await secrets.ReadAsync(SecretTargets.ExchangePassword));
    }
}

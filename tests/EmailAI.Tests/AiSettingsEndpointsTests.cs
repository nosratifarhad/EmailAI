using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EmailAI.Application.AI;
using EmailAI.Application.Settings;
using EmailAI.Domain.AI;
using EmailAI.Domain.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EmailAI.Tests;

/// <summary>
/// Boots the real EmailAI.Api host with the Windows Credential Manager and the per-user
/// settings file replaced by in-memory stores, then verifies the Settings surface:
///   * GET /api/settings/ai never returns the API key (only its presence),
///   * PUT stores base URL/model overrides and/or the API key, DELETE removes the key,
///     empty/invalid requests are rejected,
///   * POST /api/settings/ai/test answers not_configured/unavailable/authentication_failed
///     without secrets,
///   * GET /api/settings/exchange returns the detected Windows identity,
///   * GET /health/ai reports not_configured for a desktop without a stored key
///     (never touching a provider).
/// Every test gets its own host + stores so test order never matters.
/// </summary>
public sealed class AiSettingsEndpointsTests : IClassFixture<SettingsHostFactory>
{
    private readonly SettingsHostFactory _factory;

    public AiSettingsEndpointsTests(SettingsHostFactory factory) => _factory = factory;

    private HttpClient CreateClient(out InMemoryAiCredentialStore store, out InMemoryAiUserSettingsStore userSettings)
    {
        var freshStore = new InMemoryAiCredentialStore();
        var freshUserSettings = new InMemoryAiUserSettingsStore();
        store = freshStore;
        userSettings = freshUserSettings;
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                RemoveRegistrations<IAiCredentialStore>(services);
                RemoveRegistrations<IAiUserSettingsStore>(services);

                services.AddSingleton<IAiCredentialStore>(freshStore);
                services.AddSingleton<IAiUserSettingsStore>(freshUserSettings);
            });
        }).CreateClient();
    }

    private static void RemoveRegistrations<TService>(IServiceCollection services)
    {
        for (var index = services.Count - 1; index >= 0; index--)
        {
            if (services[index].ServiceType == typeof(TService))
            {
                services.RemoveAt(index);
            }
        }
    }

    [Fact]
    public async Task AiSettings_Get_Put_Get_Delete_NeverEchoesTheKey()
    {
        var client = CreateClient(out var store, out _);

        // Initial state: server config present, no per-user key and no overrides.
        var before = await client.GetFromJsonAsync<AiSettingsDto>("/api/settings/ai");
        Assert.NotNull(before);
        Assert.True(before.UserManaged);
        Assert.True(before.Configured);
        Assert.False(before.HasApiKey);
        Assert.Equal("http://127.0.0.1:1", before.BaseUrl);
        Assert.Equal("gpt-test", before.Model);

        // Save per-user provider overrides + a key.
        const string secret = "sk-test-0123456789";
        var put = await client.PutAsJsonAsync("/api/settings/ai", new
        {
            baseUrl = "https://provider.test/v1",
            model = "gpt-5",
            apiKey = secret,
        });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(secret, await store.GetApiKeyAsync());

        // Presence and overrides yes, the key value never.
        var after = await client.GetFromJsonAsync<AiSettingsDto>("/api/settings/ai");
        Assert.NotNull(after);
        Assert.True(after.HasApiKey);
        Assert.Equal("https://provider.test/v1", after.BaseUrl);
        Assert.Equal("gpt-5", after.Model);

        var rawAfter = await client.GetStringAsync("/api/settings/ai");
        Assert.DoesNotContain(secret, rawAfter, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-", rawAfter, StringComparison.OrdinalIgnoreCase);

        // Remove the key: overrides are kept, the key presence is gone.
        var delete = await client.DeleteAsync("/api/settings/ai");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        var removed = await client.GetFromJsonAsync<AiSettingsDto>("/api/settings/ai");
        Assert.NotNull(removed);
        Assert.False(removed.HasApiKey);
        Assert.Equal("https://provider.test/v1", removed.BaseUrl);
    }

    [Fact]
    public async Task AiSettings_Put_NothingToSave_ReturnsBadRequest()
    {
        var client = CreateClient(out var store, out _);

        var emptyBody = await client.PutAsJsonAsync("/api/settings/ai", new { });
        Assert.Equal(HttpStatusCode.BadRequest, emptyBody.StatusCode);

        var blankKey = await client.PutAsJsonAsync("/api/settings/ai", new { apiKey = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, blankKey.StatusCode);

        Assert.Null(await store.GetApiKeyAsync());
    }

    [Fact]
    public async Task AiSettings_Put_InvalidBaseUrl_ReturnsBadRequest_WithoutSideEffects()
    {
        var client = CreateClient(out var store, out _);

        var put = await client.PutAsJsonAsync("/api/settings/ai", new
        {
            baseUrl = "not-a-url",
            model = "gpt-5",
            apiKey = "sk-invalid",
        });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Null(await store.GetApiKeyAsync());

        var settings = await client.GetFromJsonAsync<AiSettingsDto>("/api/settings/ai");
        Assert.NotNull(settings);
        Assert.Equal("http://127.0.0.1:1", settings.BaseUrl); // untouched
    }

    [Fact]
    public async Task AiSettings_Put_EmptyBaseAndModel_ClearsOverride_FallsBackToServerConfig()
    {
        var client = CreateClient(out _, out _);

        var putOverride = await client.PutAsJsonAsync("/api/settings/ai", new
        {
            baseUrl = "https://provider.test/v1",
            model = "gpt-5",
        });
        Assert.Equal(HttpStatusCode.NoContent, putOverride.StatusCode);

        var overridden = await client.GetFromJsonAsync<AiSettingsDto>("/api/settings/ai");
        Assert.NotNull(overridden);
        Assert.Equal("https://provider.test/v1", overridden.BaseUrl);

        var putClear = await client.PutAsJsonAsync("/api/settings/ai", new { baseUrl = "", model = "" });
        Assert.Equal(HttpStatusCode.NoContent, putClear.StatusCode);

        var cleared = await client.GetFromJsonAsync<AiSettingsDto>("/api/settings/ai");
        Assert.NotNull(cleared);
        Assert.Equal("http://127.0.0.1:1", cleared.BaseUrl);
        Assert.Equal("gpt-test", cleared.Model);
    }

    [Fact]
    public async Task AiSettings_Test_NotConfigured_WhenNoKeySaved_WithoutCallingProvider()
    {
        var client = CreateClient(out _, out _);
        var settings = await client.GetFromJsonAsync<AiSettingsDto>("/api/settings/ai");
        Assert.NotNull(settings);
        Assert.False(settings.HasApiKey);
        using var response = await client.PostAsync("/api/settings/ai/test", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not_configured", document.RootElement.GetProperty("status").GetString());
    }


    [Fact]
    public async Task AiSettings_Test_Unavailable_WhenProviderUnreachable_WithSavedKey()
    {
        var client = CreateClient(out var store, out _);
        await store.SaveApiKeyAsync("sk-stored");

        using var response = await client.PostAsync("/api/settings/ai/test", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unavailable", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AiSettings_Test_AuthenticationFailed_WhenProviderRejectsTheKey_WithoutExposingIt()
    {
        const string storedKey = "sk-secret-store-key";
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                RemoveRegistrations<IAiCredentialStore>(services);
                RemoveRegistrations<IAiUserSettingsStore>(services);
                RemoveRegistrations<IAiClient>(services);

                var store = new InMemoryAiCredentialStore();
                store.SaveApiKeyAsync(storedKey).GetAwaiter().GetResult();
                services.AddSingleton<IAiCredentialStore>(store);
                services.AddSingleton<IAiUserSettingsStore>(new InMemoryAiUserSettingsStore());

                var stub = new StubAiClient(new AiProbeResult(
                    false,
                    LatencyMs: 7,
                    Error: "AI service responded with HTTP 401.",
                    ErrorKind: AiErrorKind.Authentication));
                services.AddScoped<IAiClient>(_ => stub);
            });
        }).CreateClient();

        using var response = await client.PostAsync("/api/settings/ai/test", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(storedKey, raw, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(raw);
        Assert.Equal("authentication_failed", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AiSettings_Delete_And_Put_WorkAfterEachOther()
    {
        var client = CreateClient(out var store, out _);
        await store.SaveApiKeyAsync("sk-temp");

        await client.DeleteAsync("/api/settings/ai");

        var put = await client.PutAsJsonAsync("/api/settings/ai", new { apiKey = "sk-new" });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal("sk-new", await store.GetApiKeyAsync());
    }

    [Fact]
    public async Task AiSettings_EnvironmentManagedDeployment_RejectsSave_AndReportsUserManagedFalse()
    {
        // A second host configured like a server/CI deployment: AI_CREDENTIAL_SOURCE=environment.
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{AiOptions.SectionName}:CredentialSource"] = "environment",
                    [$"{AiOptions.SectionName}:ApiKey"] = "server-key",
                }));
        }).CreateClient();

        var settings = await client.GetFromJsonAsync<AiSettingsDto>("/api/settings/ai");
        Assert.NotNull(settings);
        Assert.False(settings.UserManaged);
        Assert.True(settings.HasApiKey);
        Assert.Equal("environment", settings.EffectiveSource);

        var putKey = await client.PutAsJsonAsync("/api/settings/ai", new { apiKey = "injected-key" });
        Assert.Equal(HttpStatusCode.Conflict, putKey.StatusCode);

        var putSettings = await client.PutAsJsonAsync("/api/settings/ai", new { baseUrl = "https://evil.example/v1" });
        Assert.Equal(HttpStatusCode.Conflict, putSettings.StatusCode);

        var delete = await client.DeleteAsync("/api/settings/ai");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
    }

    [Fact]
    public async Task HealthAi_NotConfigured_WhenDesktopHasNoStoredKey_WithoutTouchingProvider()
    {
        // This host is the desktop shape: provider configured, per-user store empty.
        var client = CreateClient(out _, out _);

        var response = await client.GetAsync("/health/ai");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not_configured", document.RootElement.GetProperty("status").GetString());
        Assert.Contains("Settings", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ExchangeSettings_ReturnsDetectedWindowsIdentity_AndNeverSecrets()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/settings/exchange");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        Assert.Equal("Windows", root.GetProperty("authenticationMode").GetString());
        Assert.True(root.GetProperty("usesDefaultCredentials").GetBoolean());
        Assert.True(root.TryGetProperty("detectedIdentity", out var identity));
        Assert.True(identity.TryGetProperty("userName", out var userName));
        Assert.False(string.IsNullOrWhiteSpace(userName.GetString()));
    }

    [Fact]
    public async Task ExchangeSettings_UsernamePasswordMode_ExposesModeAndPresenceOnly_NeverCredentials()
    {
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Exchange:Authentication"] = "UsernamePassword",
                    ["Exchange:Username"] = "mail-bot",
                    ["Exchange:Password"] = "super-secret-pw",
                }));
        }).CreateClient();

        var response = await client.GetAsync("/api/settings/exchange");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("super-secret-pw", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("mail-bot", raw, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        Assert.Equal("UsernamePassword", root.GetProperty("authenticationMode").GetString());
        Assert.False(root.GetProperty("usesDefaultCredentials").GetBoolean());
        Assert.True(root.GetProperty("usernameConfigured").GetBoolean());
        Assert.True(root.GetProperty("passwordConfigured").GetBoolean());
    }

    private sealed record AiSettingsDto(
        bool Configured,
        bool HasApiKey,
        bool UserManaged,
        string? EffectiveSource,
        string? BaseUrl,
        string? Model);
}



/// <summary>
/// Base host for the Settings tests: provider-shaped config on an unreachable port,
/// desktop-default "auto" credential source, and the real stores swapped for fresh
/// in-memory instances. Per-test isolation is layered on top via WithWebHostBuilder.
/// </summary>
public sealed class SettingsHostFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(FindContentRoot());
        ClearAiEnvironment();
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // A configured-shaped but guaranteed-unreachable provider (connection
                // refused is immediate) plus the desktop-default "auto" credential source.
                [$"{AiOptions.SectionName}:BaseUrl"] = "http://127.0.0.1:1",
                [$"{AiOptions.SectionName}:Model"] = "gpt-test",
                [$"{AiOptions.SectionName}:ApiKey"] = string.Empty,
                [$"{AiOptions.SectionName}:TimeoutSeconds"] = "5",
                ["Exchange:EwsUrl"] = "http://127.0.0.1:1/EWS/Exchange.asmx",
                ["Exchange:Authentication"] = "Windows",
            });
        });

        // The real per-user settings file (%APPDATA%\EmailAI\settings.json) and the Windows
        // Credential Manager are never touched by a test run: every host gets fresh
        // in-memory stores, which tests resolve through factory.Services.
        builder.ConfigureServices(services =>
        {
            RemoveRegistrations<ISecretStore>(services);
            RemoveRegistrations<IUserSettingsStore>(services);
            services.AddSingleton<ISecretStore>(new InMemorySecretStore());
            services.AddSingleton<IUserSettingsStore>(new InMemoryUserSettingsStore());
        });
    }

    /// <summary>Removes every registration of a service type (host + test overrides).</summary>
    public static void RemoveRegistrations<TService>(IServiceCollection services)
    {
        for (var index = services.Count - 1; index >= 0; index--)
        {
            if (services[index].ServiceType == typeof(TService))
            {
                services.RemoveAt(index);
            }
        }
    }

    private static void ClearAiEnvironment()
    {
        Environment.SetEnvironmentVariable("AI_BASE_URL", null);
        Environment.SetEnvironmentVariable("AI_API_KEY", null);
        Environment.SetEnvironmentVariable("AI_MODEL", null);
        Environment.SetEnvironmentVariable("AI_TIMEOUT_SECONDS", null);
        Environment.SetEnvironmentVariable("AI_CREDENTIAL_SOURCE", null);
    }

    private static string FindContentRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "EmailAI.Api");
            if (File.Exists(Path.Combine(candidate, "EmailAI.Api.csproj")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the src/EmailAI.Api content root.");
    }
}

/// <summary>
/// Deterministic <see cref="IAiClient"/> stand-in used by host tests to simulate a
/// provider verdict without any real HTTP.
/// </summary>
internal sealed class StubAiClient(AiProbeResult probeResult) : IAiClient
{
    public Task<AiChatCompletion> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException("StubAiClient.CompleteAsync is not used by these tests.");

    public Task<AiProbeResult> ProbeAsync(CancellationToken cancellationToken)
        => Task.FromResult(probeResult);
}


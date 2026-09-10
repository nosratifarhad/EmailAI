using System.Net;
using System.Text.Json;
using EmailAI.Application.AI;
using EmailAI.Domain.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EmailAI.Tests;

/// <summary>
/// GET /api/ai/readiness - the pre-flight state report the mail UI and the Settings page rely
/// on. It always answers 200 with an actionable verdict, distinguishes "not configured" from
/// "no key yet", and never returns the API key.
/// </summary>
public sealed class AiReadinessEndpointTests : IClassFixture<SettingsHostFactory>
{
    private readonly SettingsHostFactory _factory;

    public AiReadinessEndpointTests(SettingsHostFactory factory) => _factory = factory;

    private HttpClient CreateClient(params (string Key, string? Value)[] settings)
        => _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(settings.ToDictionary(
                setting => setting.Key,
                setting => setting.Value,
                StringComparer.OrdinalIgnoreCase));
        })).CreateClient();

    [Fact]
    public async Task NotConfigured_ReportsActionableGuidance_WithoutCallingAProvider()
    {
        var client = CreateClient(
            (AiKey("BaseUrl"), string.Empty),
            (AiKey("Model"), string.Empty));

        using var response = await client.GetAsync("/api/ai/readiness");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await ReadAsync(response);
        Assert.False(payload.GetProperty("ready").GetBoolean());
        Assert.Equal("NotConfigured", payload.GetProperty("status").GetString());
        Assert.Equal("ai_not_configured", payload.GetProperty("errorCode").GetString());
        Assert.True(payload.GetProperty("canOpenSettings").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("message").GetString()));
        Assert.Equal("Open Settings", payload.GetProperty("action").GetString());
    }

    [Fact]
    public async Task Ready_WhenTheDeploymentConfigurationSuppliesTheKey()
    {
        // Environment-managed credentials: the key comes from configuration, so no per-user
        // store is consulted and the cheap check needs no network request.
        var client = CreateClient(
            (AiKey("BaseUrl"), "https://provider.test/v1"),
            (AiKey("Model"), "gpt-5"),
            (AiKey("ApiKey"), "server-key"),
            (AiKey("CredentialSource"), "environment"));

        var payload = await ReadAsync(await client.GetAsync("/api/ai/readiness"));

        Assert.True(payload.GetProperty("ready").GetBoolean());
        Assert.Equal("Ready", payload.GetProperty("status").GetString());
        Assert.Equal("ai_ready", payload.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Probe_ReportsARejectedKey_AsAuthenticationFailure()
    {
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [AiKey("BaseUrl")] = "https://provider.test/v1",
                    [AiKey("Model")] = "gpt-5",
                    [AiKey("ApiKey")] = "server-key",
                    [AiKey("CredentialSource")] = "environment",
                }));

            builder.ConfigureServices(services =>
            {
                SettingsHostFactory.RemoveRegistrations<IAiClient>(services);
                services.AddScoped<IAiClient>(_ => new StubAiClient(new AiProbeResult(
                    IsAvailable: false,
                    LatencyMs: 9,
                    Error: "AI service responded with HTTP 401.",
                    ErrorKind: AiErrorKind.Authentication)));
            });
        }).CreateClient();

        var payload = await ReadAsync(await client.GetAsync("/api/ai/readiness?probe=true"));

        Assert.False(payload.GetProperty("ready").GetBoolean());
        Assert.Equal("AuthenticationFailed", payload.GetProperty("status").GetString());
        Assert.Equal("ai_authentication_failed", payload.GetProperty("errorCode").GetString());
        Assert.Contains(
            "API key",
            payload.GetProperty("message").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Response_NeverContainsTheConfiguredKey()
    {
        var client = CreateClient(
            (AiKey("BaseUrl"), "https://provider.test/v1"),
            (AiKey("Model"), "gpt-5"),
            (AiKey("ApiKey"), "super-secret-key"),
            (AiKey("CredentialSource"), "environment"));

        var raw = await client.GetStringAsync("/api/ai/readiness");

        Assert.DoesNotContain("super-secret-key", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", raw, StringComparison.OrdinalIgnoreCase);
    }

    private static string AiKey(string name) => $"{AiOptions.SectionName}:{name}";

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}

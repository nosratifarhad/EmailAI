using System.Net;
using System.Text.Json;
using EmailAI.Domain.AI;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace EmailAI.Tests;

/// <summary>
/// Boots the real EmailAI.Api host and verifies the AI health surface. No AI provider is
/// required: when no AI configuration is present the endpoint must answer 200 with
/// "not_configured" and the app itself must stay healthy.
/// </summary>
public sealed class AiHealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AiHealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(FindContentRoot());
            // appsettings.json or leftover AI_* environment variables could carry an "Ai" section (base
            // URL/model defaults for this machine), so clearing the AI_* variables
            // alone is not enough for deterministic tests. Blank the Ai section in
            // memory as well so the assertions below are deterministic, never touch the
            // provider and never depend on the presence or value of secrets on disk.
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{AiOptions.SectionName}:BaseUrl"] = string.Empty,
                    [$"{AiOptions.SectionName}:Model"] = string.Empty,
                    [$"{AiOptions.SectionName}:ApiKey"] = string.Empty,
                    // "environment" keeps these tests fully deterministic: the runtime
                    // must never consult the real Windows Credential Manager vault.
                    [$"{AiOptions.SectionName}:CredentialSource"] = "environment",
                    [$"{AiOptions.SectionName}:TimeoutSeconds"] = "10",
                });
            });
        });
        ClearAiEnvironment();
    }

    [Fact]
    public async Task HealthAi_ReportsNotConfigured_WhenAiEnvironmentIsEmpty()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health/ai");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not_configured", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Health_StaysHealthy_EvenWhenAiIsNotConfigured()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("healthy", document.RootElement.GetProperty("status").GetString());
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

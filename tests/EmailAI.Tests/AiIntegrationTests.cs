using EmailAI.Application.AI;
using EmailAI.Infrastructure.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmailAI.Tests;

/// <summary>
/// Opt-in REAL AI integration test against any OpenAI-compatible endpoint. When the
/// four variables below are missing the test reports "not configured" and completes
/// without calling any service (the normal unit suite never needs a live provider):
///   EMAILAI_AI_INTEGRATION_TEST=true
///   EMAILAI_AI_BASE_URL   EMAILAI_AI_API_KEY   EMAILAI_AI_MODEL
/// No credentials are committed to source control and nothing is printed here -
/// the variables are read from the environment only when the test actually runs.
/// </summary>
public sealed class AiIntegrationTests
{
    [Fact]
    public async Task RealOpenAiCompatibleProvider_UsesConfiguredUrlKeyModel_AndReturnsValidNonEmptyResponse()
    {
        if (!IntegrationTestEnvironment.AiConfigured)
        {
            // Not configured: report it and complete without touching any service. The
            // normal `dotnet test` run never calls an external provider and no fake
            // credentials are ever used.
            Console.WriteLine(
                "EMAILAI_AI_INTEGRATION_TEST not configured - AI integration test does not run. " +
                "Set EMAILAI_AI_INTEGRATION_TEST=true plus EMAILAI_AI_BASE_URL, " +
                "EMAILAI_AI_API_KEY and EMAILAI_AI_MODEL (any OpenAI-compatible endpoint) " +
                "to run it against a real provider.");
            return;
        }

        var options = IntegrationTestEnvironment.AiProviderOptions;
        var client = new OpenAiCompatClient(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            new StaticAiConfigurationProvider(options),
            new StaticAiCredentialProvider(options.ApiKey),
            NullLogger<OpenAiCompatClient>.Instance);

        // A real HTTP request through the configured Base URL / key / model. The key
        // is sent only in the Authorization header by the client under test.
        var completion = await client.CompleteAsync(
            new AiChatRequest(
            [
                new AiChatMessage(AiChatRole.User, "Reply with the single word: pong"),
            ]),
            CancellationToken.None);

        Assert.False(
            string.IsNullOrWhiteSpace(completion.Content),
            "The configured AI provider returned an empty/invalid completion.");
    }
}

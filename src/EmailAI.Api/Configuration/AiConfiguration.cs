using EmailAI.Domain.AI;
using Microsoft.Extensions.Options;

namespace EmailAI.Api.Configuration;

/// <summary>
/// Binds the "Ai" section from appsettings.json and applies the documented AI_*
/// environment variables on top (AI_BASE_URL, AI_MODEL, AI_TIMEOUT_SECONDS,
/// AI_API_KEY, AI_CREDENTIAL_SOURCE - see .env.example).
///
/// Secret policy: appsettings.json that ships with the installer carries NO API key.
/// On Windows desktop the per-user secure credential store is authoritative for the
/// key (see AiCredentialCoordinator / WindowsCredentialStore). AI_API_KEY remains a
/// supported server/CI/container override that the coordinator only falls back to
/// when the secure store has no entry and AI_CREDENTIAL_SOURCE is "auto".
///
/// Unlike Exchange, this is intentionally NOT ValidateOnStart: the email client must
/// start and work normally when AI is not configured. Configuration problems surface
/// as clear "AI is not configured / misconfigured" errors at call time.
/// </summary>
public static class AiConfiguration
{
    public static IServiceCollection ConfigureAiOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AiOptions>()
            .Bind(configuration.GetSection(AiOptions.SectionName))
            .Configure(ApplyEnvironmentOverrides);

        return services;
    }

    private static void ApplyEnvironmentOverrides(AiOptions options)
    {
        options.BaseUrl = Env("AI_BASE_URL") ?? options.BaseUrl;
        options.ApiKey = Env("AI_API_KEY") ?? options.ApiKey;
        options.Model = Env("AI_MODEL") ?? options.Model;
        options.CredentialSource = Env("AI_CREDENTIAL_SOURCE") ?? options.CredentialSource;

        if (int.TryParse(Env("AI_TIMEOUT_SECONDS"), out var timeout) && timeout is > 0 and <= 600)
        {
            options.TimeoutSeconds = timeout;
        }
    }

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

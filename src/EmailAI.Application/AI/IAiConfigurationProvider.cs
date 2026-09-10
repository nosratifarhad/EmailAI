using EmailAI.Domain.AI;

namespace EmailAI.Application.AI;

/// <summary>
/// Resolves the EFFECTIVE AI provider settings at runtime: the server-configured values
/// ("Ai" section + AI_* environment variables, see .env.example) merged with the per-user
/// desktop overrides (base URL/model saved from Settings) whenever the deployment is
/// user-managed.
///
/// The returned <see cref="AiOptions"/> snapshot is what the OpenAI-compatible HTTP
/// client calls the provider with. The API key is never part of this resolution - it is
/// resolved separately at request time through <see cref="IAiCredentialProvider"/>.
/// </summary>
public interface IAiConfigurationProvider
{
    /// <summary>Returns a snapshot of the effective provider settings.</summary>
    Task<AiOptions> GetEffectiveOptionsAsync(CancellationToken cancellationToken = default);
}

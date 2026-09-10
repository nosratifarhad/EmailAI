namespace EmailAI.Application.AI;

/// <summary>
/// Resolves the effective AI API key at runtime. On Windows desktop the per-user
/// secure store is authoritative (a key the user saved in Settings always wins);
/// server/CI/container deployments keep using the configured AI_API_KEY. See the
/// coordinator implementation for the exact precedence rules.
/// </summary>
public interface IAiCredentialProvider
{
    /// <summary>
    /// Returns the effective API key, or null/empty when the gateway needs none
    /// or nothing is configured yet. Never logs, caches on disk or exposes the key.
    /// </summary>
    Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default);
}

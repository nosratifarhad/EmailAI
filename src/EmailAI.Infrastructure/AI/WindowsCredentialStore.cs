using EmailAI.Application.AI;
using EmailAI.Application.Settings;

namespace EmailAI.Infrastructure.AI;

/// <summary>
/// <see cref="IAiCredentialStore"/> backed by the per-user <see cref="ISecretStore"/>
/// (Windows Credential Manager in production) under the target
/// <see cref="SecretTargets.AiApiKey"/> (<c>EmailAI/AI</c> - the long-standing target, kept
/// for backward compatibility with keys already saved by earlier versions).
///
/// The API key value is never logged, never cached to disk and never returned through any
/// API response (only <see cref="GetApiKeyAsync"/>). The Exchange password uses its own
/// target (<c>EmailAI/Exchange</c>), so the two secrets can never be confused.
/// </summary>
public sealed class WindowsCredentialStore(ISecretStore secrets) : IAiCredentialStore
{
    /// <summary>Credential Manager target that holds the AI provider API key.</summary>
    public const string TargetName = SecretTargets.AiApiKey;

    public Task<bool> HasApiKeyAsync(CancellationToken cancellationToken = default)
        => secrets.ExistsAsync(TargetName, cancellationToken);

    public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
        => secrets.ReadAsync(TargetName, cancellationToken);

    public Task SaveApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("The AI API key must not be empty.", nameof(apiKey));
        }

        return secrets.WriteAsync(TargetName, apiKey.Trim(), cancellationToken);
    }

    public Task DeleteApiKeyAsync(CancellationToken cancellationToken = default)
        => secrets.DeleteAsync(TargetName, cancellationToken);
}

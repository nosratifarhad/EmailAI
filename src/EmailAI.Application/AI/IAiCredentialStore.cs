namespace EmailAI.Application.AI;

/// <summary>
/// A per-user, OS-protected place that stores the AI API key. The desktop
/// implementation is the Windows Credential Manager (scoped to the current Windows
/// account); tests and non-Windows hosts substitute in-memory fakes. Implementations
/// must never log, echo or return the key through any channel other than
/// <see cref="GetApiKeyAsync"/>.
/// </summary>
public interface IAiCredentialStore
{
    /// <summary>True when an API key is currently saved.</summary>
    Task<bool> HasApiKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the saved API key, or null when none is saved.</summary>
    Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves (or replaces) the API key.</summary>
    Task SaveApiKeyAsync(string apiKey, CancellationToken cancellationToken = default);

    /// <summary>Removes the saved API key. Removing a missing key is a no-op.</summary>
    Task DeleteApiKeyAsync(CancellationToken cancellationToken = default);
}

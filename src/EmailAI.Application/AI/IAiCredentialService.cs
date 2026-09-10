using EmailAI.Application.Settings;

namespace EmailAI.Application.AI;

/// <summary>
/// Failure while reading/writing the per-user credential store (for example the
/// Windows Credential Manager is unavailable or access was denied). Never contains
/// the API key. Derives from <see cref="SecretStoreException"/> so callers can handle
/// every per-user secret failure uniformly.
/// </summary>
public sealed class AiCredentialStoreException : SecretStoreException
{
    public AiCredentialStoreException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Non-secret snapshot of the AI provider state, safe to return from a GET API
/// endpoint. Deliberately never contains the API key (or a masked/truncated one).
/// The BaseUrl/Model here are the EFFECTIVE values (server configuration merged with
/// any per-user desktop overrides).
/// </summary>
public sealed record AiCredentialStatus(
    bool Configured,
    bool HasApiKey,
    bool UserManaged,
    string EffectiveSource,
    string? BaseUrl,
    string? Model,
    int TimeoutSeconds);

/// <summary>
/// Management surface used by the Settings UI/API. Sits between the UI and the
/// concrete credential/user-settings stores so the UI never touches the Windows
/// Credential Manager (or the environment) directly. Never returns the API key.
/// </summary>
public interface IAiCredentialService
{
    /// <summary>Returns the current non-secret AI settings + key state.</summary>
    Task<AiCredentialStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the non-secret provider overrides (base URL/model) and optionally replaces
    /// the API key in the per-user secure store.
    ///
    /// Semantics per field (each is optional):
    ///   baseUrl - null leaves the stored override untouched; a value (trimmed) replaces
    ///             it; an empty string clears the override so the server configuration is
    ///             used again. A non-empty value must be an absolute http(s) URL.
    ///   model   - same as baseUrl (no format constraint beyond length).
    ///   apiKey  - null/empty never touches the existing key; a non-empty value replaces
    ///             the key in the secure store.
    ///
    /// Throws <see cref="InvalidOperationException"/> when the deployment sources the key
    /// from the environment (AI_CREDENTIAL_SOURCE=environment).
    /// </summary>
    Task SaveSettingsAsync(
        string? baseUrl,
        string? model,
        string? apiKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the saved API key from the per-user secure store. Removing a missing key
    /// is a no-op. Per-user base URL/model overrides are left untouched.
    /// </summary>
    Task DeleteKeyAsync(CancellationToken cancellationToken = default);
}

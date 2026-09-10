using EmailAI.Application.AI;
using EmailAI.Domain.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmailAI.Api.Configuration;

/// <summary>
/// Single runtime policy point for the AI provider connection. Implements:
///   * IAiCredentialProvider    - resolves the effective API key for every AI request
///                                (never from the browser; never logged).
///   * IAiCredentialService     - Settings/health API surface (status, save settings +
///                                key, remove key). Never returns the key.
///   * IAiConfigurationProvider - merges server configuration with per-user desktop
///                                overrides so the client calls the right provider.
///
/// <code>
///   Blazor Settings UI            (never sees the key; masked input only)
///        |
///        v
///   /api/settings/ai  ->  IAiCredentialService (this coordinator)
///        |
///        +--> IAiCredentialStore    (Windows Credential Manager, current user)
///        +--> IAiUserSettingsStore  (base URL/model overrides, current user profile)
///
///   OpenAiCompatClient  <-  IAiCredentialProvider (this coordinator)
///        |
///        +--> configured OpenAI-compatible provider (chat/completions)
/// </code>
///
/// Key precedence (AI_CREDENTIAL_SOURCE):
///   windows     - the per-user secure store is the ONLY key source; save/delete allowed.
///   environment - the configured AI_API_KEY / appsettings value is the ONLY source;
///                 save/delete are rejected (server/CI/container deployments); per-user
///                 base URL/model overrides are ignored entirely.
///   auto        - (default) the per-user secure store wins when it has an entry,
///                 otherwise the configured/environment key is used. This prevents a
///                 stale AI_API_KEY environment variable from overriding a key the user
///                 saved in Settings.
///
/// For the non-secret provider settings, "auto" and "windows" honour the per-user
/// base URL/model saved in Settings on top of the server configuration; "environment"
/// always uses the server configuration. No key value is ever logged or returned.
/// </summary>
public sealed class AiCredentialCoordinator(
    IOptionsMonitor<AiOptions> options,
    IAiCredentialStore userStore,
    IAiUserSettingsStore userSettingsStore,
    ILogger<AiCredentialCoordinator> logger)
    : IAiCredentialProvider, IAiCredentialService, IAiConfigurationProvider
{
    private const int MaxBaseUrlLength = 500;
    private const int MaxModelLength = 200;

    public async Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
    {
        var current = options.CurrentValue;

        if (current.UsesWindowsCredentialSource)
        {
            return await userStore.GetApiKeyAsync(cancellationToken);
        }

        if (current.UsesEnvironmentCredentialSource)
        {
            return string.IsNullOrWhiteSpace(current.ApiKey) ? null : current.ApiKey;
        }

        // auto: secure store is authoritative when present; otherwise configured key.
        return await userStore.HasApiKeyAsync(cancellationToken)
            ? await userStore.GetApiKeyAsync(cancellationToken)
            : string.IsNullOrWhiteSpace(current.ApiKey) ? null : current.ApiKey;
    }

    public async Task<AiCredentialStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var current = options.CurrentValue;
        var effective = await ResolveEffectiveOptionsAsync(current, cancellationToken);
        var (effectiveSource, userManaged, hasKey) = await ResolveStateAsync(current, cancellationToken);

        return new AiCredentialStatus(
            Configured: effective.IsConfigured,
            HasApiKey: hasKey,
            UserManaged: userManaged,
            EffectiveSource: effectiveSource,
            BaseUrl: effective.BaseUrl,
            Model: effective.Model,
            TimeoutSeconds: effective.TimeoutSeconds);
    }

    public async Task<AiOptions> GetEffectiveOptionsAsync(CancellationToken cancellationToken = default)
        => await ResolveEffectiveOptionsAsync(options.CurrentValue, cancellationToken);


    public async Task SaveSettingsAsync(
        string? baseUrl,
        string? model,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        var current = options.CurrentValue;
        ThrowIfEnvironmentManaged(current);

        var hasSettings = baseUrl is not null || model is not null;
        var hasKey = !string.IsNullOrWhiteSpace(apiKey);
        if (!hasSettings && !hasKey)
        {
            throw new ArgumentException("Nothing to save: provide a base URL, a model or an API key.");
        }

        // Validate before mutating anything, so a rejected request has no side effects.
        var trimmedBaseUrl = baseUrl is null ? null : baseUrl.Trim();
        var trimmedModel = model is null ? null : model.Trim();

        if (trimmedBaseUrl is { Length: > MaxBaseUrlLength })
        {
            throw new ArgumentException($"The AI base URL must be at most {MaxBaseUrlLength} characters.");
        }

        if (trimmedModel is { Length: > MaxModelLength })
        {
            throw new ArgumentException($"The AI model name must be at most {MaxModelLength} characters.");
        }

        if (!string.IsNullOrEmpty(trimmedBaseUrl))
        {
            ValidateBaseUrlOrThrow(trimmedBaseUrl!);
        }

        var existing = await userSettingsStore.GetAsync(cancellationToken);
        var storedBaseUrl = baseUrl is null ? existing.BaseUrl : NullIfBlank(trimmedBaseUrl);
        var storedModel = model is null ? existing.Model : NullIfBlank(trimmedModel);

        if (hasSettings)
        {
            await userSettingsStore.SaveAsync(
                new AiUserSettings(storedBaseUrl, storedModel),
                cancellationToken);
        }

        if (hasKey)
        {
            await userStore.SaveApiKeyAsync(apiKey!.Trim(), cancellationToken);
        }
    }

    public async Task DeleteKeyAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfEnvironmentManaged(options.CurrentValue);
        await userStore.DeleteApiKeyAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves the effective key state. Semantics used by the Settings/health surface:
    ///   * UserManaged is a capability flag, not a description of the current key: it is
    ///     true for "auto" and "windows" (the per-user secure store is writable) and
    ///     false only for "environment".
    ///   * HasApiKey is whether a key is effective right now: the secure store wins, then
    ///     (auto only) a configured/AI_API_KEY value counts as a fallback key.
    ///   * EffectiveSource describes where an effective key lives ("windows" wins over
    ///     the "environment" fallback); when no key exists yet it says where the next
    ///     key will be stored - i.e. "windows" for every store-backed source.
    /// </summary>
    private async Task<(string EffectiveSource, bool UserManaged, bool HasKey)> ResolveStateAsync(
        AiOptions current,
        CancellationToken cancellationToken)
    {
        if (current.UsesEnvironmentCredentialSource)
        {
            return (
                "environment",
                UserManaged: false,
                HasKey: !string.IsNullOrWhiteSpace(current.ApiKey));
        }

        // "windows" and "auto" are both store-backed and allow save/delete from Settings.
        var storeHasKey = await userStore.HasApiKeyAsync(cancellationToken);
        var configuredKeyPresent = !string.IsNullOrWhiteSpace(current.ApiKey);

        if (storeHasKey)
        {
            return ("windows", UserManaged: true, HasKey: true);
        }

        return current.UsesAutoCredentialSource && configuredKeyPresent
            ? ("environment", UserManaged: true, HasKey: true) // auto fallback in effect
            : ("windows", UserManaged: true, HasKey: false);   // no key yet - store next
    }

    /// <summary>
    /// Effective provider options = server configuration merged with the per-user desktop
    /// overrides (base URL / model). The API key is intentionally NOT merged here: it is
    /// resolved separately at request time via <see cref="GetApiKeyAsync"/>.
    /// </summary>
    private async Task<AiOptions> ResolveEffectiveOptionsAsync(
        AiOptions current,
        CancellationToken cancellationToken)
    {
        if (current.UsesEnvironmentCredentialSource)
        {
            // Server/container deployment: Settings is read-only, so the server
            // configuration is the whole story.
            return current;
        }

        var user = await userSettingsStore.GetAsync(cancellationToken);
        return new AiOptions
        {
            BaseUrl = FirstNonBlank(user.BaseUrl, current.BaseUrl),
            Model = FirstNonBlank(user.Model, current.Model),
            ApiKey = current.ApiKey,
            CredentialSource = current.CredentialSource,
            TimeoutSeconds = current.TimeoutSeconds,
        };
    }

    private static string FirstNonBlank(string? preferred, string fallback)
        => string.IsNullOrWhiteSpace(preferred) ? fallback : preferred!;

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ValidateBaseUrlOrThrow(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException(
                "The AI base URL must be an absolute http(s) URL " +
                "(for example https://api.openai.com/v1).");
        }
    }

    private void ThrowIfEnvironmentManaged(AiOptions current)
    {
        if (current.UsesEnvironmentCredentialSource)
        {
            logger.LogWarning(
                "A Settings save/delete of the AI API key was rejected because this deployment " +
                "sources the key from the environment (AI_CREDENTIAL_SOURCE=environment).");
            throw new InvalidOperationException(
                "This deployment's AI API key is supplied by the environment " +
                "(AI_CREDENTIAL_SOURCE=environment) and cannot be changed from Settings.");
        }
    }
}


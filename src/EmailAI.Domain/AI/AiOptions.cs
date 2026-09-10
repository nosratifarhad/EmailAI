namespace EmailAI.Domain.AI;

/// <summary>
/// Server-side settings for the OpenAI-compatible AI provider. Bound from the
/// "Ai" appsettings section and overridden by the documented AI_* environment
/// variables (see .env.example). The API key lives only in server configuration -
/// never in the browser, never in logs.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>
    /// Base URL of the OpenAI-compatible provider API root the user configures, e.g.
    /// "https://api.openai.com/v1" (a trailing slash is harmless - the client
    /// normalises it and never sends "/v1//chat/completions").
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Optional bearer token for the AI provider. Empty when no key is needed.
    /// Never stored in configuration that ships with the installer - the per-user secure
    /// credential store is the authoritative source on Windows desktop (see AiConfiguration
    /// and IAiCredentialProvider).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Model identifier to request, exactly as the provider expects it (e.g. "gpt-5").
    /// </summary>

    public string Model { get; set; } = string.Empty;

    /// <summary>Chat-completion timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Where the AI API key comes from. One of:
    ///   auto        (default) - the per-user secure credential store wins when it has an
    ///               entry; otherwise the configured/environment key is used. This makes a
    ///               desktop user's saved credential authoritative and prevents a stale
    ///               AI_API_KEY from silently overriding it.
    ///   windows     - read/save/delete only through the per-user secure credential store.
    ///   environment - read only from configuration / AI_API_KEY (CI, containers, servers);
    ///               saving from the UI is not allowed.
    /// </summary>
    public string CredentialSource { get; set; } = "auto";

    public bool UsesEnvironmentCredentialSource => IsCredentialSource("environment");
    public bool UsesWindowsCredentialSource => IsCredentialSource("windows");
    public bool UsesAutoCredentialSource => IsCredentialSource("auto");

    private bool IsCredentialSource(string value) =>
        string.Equals(CredentialSource?.Trim(), value, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when enough is configured to call the AI provider. A missing API key is not a
    /// blocker (some providers need no authentication); a missing base URL or model is.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(Model);
}

namespace EmailAI.Domain.AI;

/// <summary>
/// Non-secret, per-user overrides for the AI provider connection (base URL and model).
///
/// Desktop users configure these in Settings when they want to talk to a provider that
/// is not the server default. They are persisted per user (Windows desktop writes them
/// under the current user's profile; the file never contains the API key) and applied on
/// top of the server-configured "Ai" section / AI_* environment variables.
///
/// Empty/null values mean "no override" - the server configuration wins. The API key is
/// deliberately NOT part of this record: it lives exclusively in
/// <see cref="EmailAI.Application.AI.IAiCredentialStore"/>.
/// </summary>
public sealed record AiUserSettings(string? BaseUrl, string? Model);

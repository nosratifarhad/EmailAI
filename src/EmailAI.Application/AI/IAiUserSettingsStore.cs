using EmailAI.Domain.AI;

namespace EmailAI.Application.AI;

/// <summary>
/// Per-user persistence for the NON-SECRET AI provider overrides (base URL and model)
/// that a desktop user configures in Settings. This store must never hold the API key -
/// keys belong to <see cref="IAiCredentialStore"/>.
///
/// The Windows desktop implementation writes a small JSON file under the current user's
/// application-data directory; tests and non-Windows hosts substitute in-memory fakes.
/// Implementations must never log or expose stored values through any other channel.
/// </summary>
public interface IAiUserSettingsStore
{
    /// <summary>Returns the saved overrides (both values null when none are stored).</summary>
    Task<AiUserSettings> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves (or replaces) the overrides. Values that are null or empty mean "no
    /// override" and clear any previously saved value.
    /// </summary>
    Task SaveAsync(AiUserSettings settings, CancellationToken cancellationToken = default);
}

using EmailAI.Domain.Settings;

namespace EmailAI.Application.Settings;

/// <summary>
/// Per-user persistence for the NON-SECRET application configuration (Exchange endpoint +
/// mode + account, AI provider Base URL + model). The Windows desktop implementation
/// writes one JSON document under the current user's application-data directory
/// (<c>%APPDATA%\EmailAI\settings.json</c>); tests and non-Windows hosts substitute
/// in-memory fakes.
///
/// This store must never hold a password or an API key - secrets belong to
/// <see cref="ISecretStore"/>. Implementations must never log or otherwise expose stored
/// values and must tolerate a missing, empty or corrupt file by reporting "no settings"
/// rather than failing (a broken preference file must never break the mail client).
/// </summary>
public interface IUserSettingsStore
{
    /// <summary>
    /// Returns the stored per-user settings. A missing, empty, unreadable or corrupt file
    /// yields <see cref="UserSettings.Empty"/> instead of throwing.
    /// </summary>
    Task<UserSettings> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the whole document (creating the directory/file as needed). Callers read
    /// with <see cref="GetAsync"/> first and merge, so a save never drops the other domain.
    /// </summary>
    Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the stored document entirely. Clearing already-empty settings is a no-op.
    /// Secrets are NOT removed here - callers clear <see cref="ISecretStore"/> separately.
    /// </summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}

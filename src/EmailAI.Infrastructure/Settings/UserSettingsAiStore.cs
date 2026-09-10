using EmailAI.Application.AI;
using EmailAI.Application.Settings;
using EmailAI.Domain.AI;

namespace EmailAI.Infrastructure.Settings;

/// <summary>
/// <see cref="IAiUserSettingsStore"/> implemented on top of the unified per-user settings
/// document (<see cref="IUserSettingsStore"/> - <c>%APPDATA%\EmailAI\settings.json</c>), so
/// the AI provider overrides and the Exchange configuration live in ONE file while the two
/// secrets stay in their own Credential Manager targets.
///
/// Only the non-secret AI values (Base URL / model) are involved here; the API key never
/// passes through this adapter.
/// </summary>
public sealed class UserSettingsAiStore(IUserSettingsStore settingsStore) : IAiUserSettingsStore
{
    public async Task<AiUserSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        return settings.Ai ?? new AiUserSettings(null, null);
    }

    public async Task SaveAsync(AiUserSettings settings, CancellationToken cancellationToken = default)
    {
        var ai = new AiUserSettings(NullIfBlank(settings.BaseUrl), NullIfBlank(settings.Model));
        var current = await settingsStore.GetAsync(cancellationToken);

        // An all-empty override section is stored as "absent" so the document stays clean and
        // the server configuration is unambiguously back in charge.
        var updated = current with
        {
            Ai = ai.BaseUrl is null && ai.Model is null ? null : ai,
        };

        await settingsStore.SaveAsync(updated, cancellationToken);
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

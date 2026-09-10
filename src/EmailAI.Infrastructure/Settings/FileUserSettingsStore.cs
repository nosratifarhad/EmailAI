using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmailAI.Application.Settings;
using EmailAI.Domain.AI;
using EmailAI.Domain.Exchange;
using EmailAI.Domain.Settings;

namespace EmailAI.Infrastructure.Settings;

/// <summary>
/// <see cref="IUserSettingsStore"/> for the Windows desktop: persists the NON-SECRET
/// per-user configuration (Exchange endpoint/mode/account and AI provider Base URL/model)
/// as one JSON document in the current user's application-data directory:
///
///   <c>%APPDATA%\EmailAI\settings.json</c>
///
/// Secrets are deliberately never written here - the Exchange password and the AI API key
/// live in the Windows Credential Manager (<see cref="WindowsSecretStore"/>, targets
/// <c>EmailAI/Exchange</c> and <c>EmailAI/AI</c>).
///
/// Robustness rules:
///  * a missing file is "no settings";
///  * an empty or whitespace-only file is "no settings";
///  * a corrupt file is preserved (moved to <c>settings.json.corrupt</c> once) and reported
///    as "no settings", so a broken preferences file can never break the mail client and
///    the damaged content is not silently destroyed;
///  * writes go through a same-directory temp file + atomic replace, so the file can never
///    be observed half written;
///  * the location is per Windows user, so one account never sees another account's
///    configuration.
///
/// <b>AI migration</b>: when <c>settings.json</c> does not exist yet but the previous
/// <c>ai-settings.json</c> does, the AI Base URL/model are migrated into the new document
/// (once - the old file is left untouched for safety, and its API key is untouched because
/// it already lives in the same Credential Manager target the new architecture uses).
/// </summary>
public sealed class FileUserSettingsStore : IUserSettingsStore
{
    private const string FileName = "settings.json";
    private const string LegacyAiFileName = "ai-settings.json";
    private const string CorruptSuffix = ".corrupt";
    private const string AppDirectoryName = "EmailAI";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly string _legacyAiFilePath;

    /// <summary>Creates the store under the current user's roaming application data.</summary>
    public FileUserSettingsStore()
        : this(GetSettingsDirectory())
    {
    }

    /// <summary>Creates the store in an explicit directory (used by tests).</summary>
    public FileUserSettingsStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("The settings directory must not be empty.", nameof(directory));
        }

        _filePath = Path.Combine(directory, FileName);
        _legacyAiFilePath = Path.Combine(directory, LegacyAiFileName);
    }

    /// <summary>Absolute path of the persisted document (never contains a secret).</summary>
    public string FilePath => _filePath;

    public Task<UserSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(Read());
        }
    }

    public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            Write(settings);
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }

        return Task.CompletedTask;
    }

    private UserSettings Read()
    {
        if (!File.Exists(_filePath))
        {
            return TryMigrateLegacyAiSettings();
        }

        string json;
        try
        {
            json = File.ReadAllText(_filePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A per-user preferences file must never break the app; treat as unavailable.
            return UserSettings.Empty;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return UserSettings.Empty;
        }

        try
        {
            return Map(JsonSerializer.Deserialize<StoredUserSettings>(json, JsonOptions));
        }
        catch (JsonException)
        {
            QuarantineCorruptFile();
            return UserSettings.Empty;
        }
    }

    private static UserSettings Map(StoredUserSettings? stored)
    {
        if (stored is null)
        {
            return UserSettings.Empty;
        }

        var exchange = stored.Exchange is { } exchangeSection && !string.IsNullOrWhiteSpace(exchangeSection.EwsUrl)
            ? new ExchangeUserSettings(
                exchangeSection.EwsUrl.Trim(),
                NullIfBlank(exchangeSection.Authentication) ?? ExchangeOptions.WindowsMode,
                NullIfBlank(exchangeSection.Username),
                NullIfBlank(exchangeSection.Domain))
            : null;

        var ai = stored.Ai is { } aiSection
            ? new AiUserSettings(NullIfBlank(aiSection.BaseUrl), NullIfBlank(aiSection.Model))
            : null;
        if (ai is not null && ai.BaseUrl is null && ai.Model is null)
        {
            ai = null;
        }

        return new UserSettings(exchange, ai);
    }

    /// <summary>
    /// One-time migration of the previous per-user AI settings file. It only runs while
    /// <c>settings.json</c> does not exist, so it is idempotent and can never overwrite a
    /// migrated/updated document. The legacy file is intentionally left in place (it holds
    /// no secret: the API key has always lived in the Credential Manager) so an operator can
    /// still inspect it; deleting it would be riskier than keeping it.
    /// </summary>
    private UserSettings TryMigrateLegacyAiSettings()
    {
        if (!File.Exists(_legacyAiFilePath))
        {
            return UserSettings.Empty;
        }

        string json;
        try
        {
            json = File.ReadAllText(_legacyAiFilePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return UserSettings.Empty;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return UserSettings.Empty;
        }

        StoredAiSettings? legacy;
        try
        {
            legacy = JsonSerializer.Deserialize<StoredAiSettings>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // A corrupt legacy file must not block startup; it is simply not migrated.
            return UserSettings.Empty;
        }

        var ai = new AiUserSettings(NullIfBlank(legacy?.BaseUrl), NullIfBlank(legacy?.Model));
        if (ai.BaseUrl is null && ai.Model is null)
        {
            return UserSettings.Empty;
        }

        var migrated = new UserSettings(null, ai);
        Write(migrated);
        return migrated;
    }

    private void Write(UserSettings settings)
    {
        var directory = Path.GetDirectoryName(_filePath)
                        ?? throw new InvalidOperationException("Could not resolve the EmailAI settings directory.");
        Directory.CreateDirectory(directory);

        // Never overwrite a damaged file without preserving a copy of it first.
        if (File.Exists(_filePath) && !IsValidJson(_filePath))
        {
            QuarantineCorruptFile();
        }

        var stored = new StoredUserSettings
        {
            Exchange = settings.Exchange is { } exchange
                ? new StoredExchange
                {
                    EwsUrl = NullIfBlank(exchange.EwsUrl),
                    Authentication = NullIfBlank(exchange.Authentication),
                    Username = NullIfBlank(exchange.Username),
                    Domain = NullIfBlank(exchange.Domain),
                }
                : null,
            Ai = settings.Ai is { } ai
                ? new StoredAi { BaseUrl = NullIfBlank(ai.BaseUrl), Model = NullIfBlank(ai.Model) }
                : null,
        };

        if (stored.Exchange is null && stored.Ai is null)
        {
            Clear();
            return;
        }

        var json = JsonSerializer.Serialize(stored, JsonOptions);
        var tempPath = _filePath + ".tmp";

        try
        {
            File.WriteAllText(tempPath, json, Utf8NoBom);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // A leftover temp file is harmless; it is overwritten by the next save.
                }
            }
        }
    }

    private static bool IsValidJson(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return true;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Preserves a corrupt document as <c>settings.json.corrupt</c> (only the first damaged
    /// version is kept) and removes the unreadable original so valid settings can be written
    /// again. Never throws - the app must keep working even when the quarantine fails.
    /// </summary>
    private void QuarantineCorruptFile()
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var backup = _filePath + CorruptSuffix;
            if (!File.Exists(backup))
            {
                File.Move(_filePath, backup);
            }
            else
            {
                File.Delete(_filePath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Worst case the corrupt file stays; Read() still reports "no settings".
        }
    }

    private void Clear()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }

    private static string GetSettingsDirectory()
    {
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppContext.BaseDirectory,
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return Path.Combine(candidate, AppDirectoryName);
            }
        }

        return Path.Combine(AppContext.BaseDirectory, AppDirectoryName);
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class StoredUserSettings
    {
        [JsonPropertyName("exchange")]
        public StoredExchange? Exchange { get; set; }

        [JsonPropertyName("ai")]
        public StoredAi? Ai { get; set; }
    }

    private sealed class StoredExchange
    {
        [JsonPropertyName("ewsUrl")]
        public string? EwsUrl { get; set; }

        [JsonPropertyName("authentication")]
        public string? Authentication { get; set; }

        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("domain")]
        public string? Domain { get; set; }
    }

    private sealed class StoredAi
    {
        [JsonPropertyName("baseUrl")]
        public string? BaseUrl { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }
    }

    /// <summary>Shape of the legacy <c>ai-settings.json</c> read once during migration.</summary>
    private sealed class StoredAiSettings
    {
        [JsonPropertyName("baseUrl")]
        public string? BaseUrl { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }
    }
}

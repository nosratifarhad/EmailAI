using System.Text.Json;
using EmailAI.Domain.AI;
using EmailAI.Domain.Exchange;
using EmailAI.Domain.Settings;
using EmailAI.Infrastructure.Settings;

namespace EmailAI.Tests;

/// <summary>
/// The per-user, non-secret settings document in a temporary directory (the real
/// %APPDATA%\EmailAI\settings.json is never touched): round-trips, tolerance for a missing,
/// empty or corrupt file, the one-time legacy ai-settings.json migration and the guarantee
/// that no secret value is ever written to JSON.
/// </summary>
public sealed class FileUserSettingsStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "EmailAI-tests-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    private string LegacyPath => Path.Combine(_directory, "ai-settings.json");

    private FileUserSettingsStore CreateStore() => new(_directory);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory must never fail a test run.
        }
    }

    private void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, content);
    }

    [Fact]
    public async Task Get_WhenNothingWasSaved_ReturnsEmpty()
    {
        var settings = await CreateStore().GetAsync();

        Assert.Null(settings.Exchange);
        Assert.Null(settings.Ai);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public async Task SaveThenGet_RoundTripsBothDomains()
    {
        var store = CreateStore();
        var document = new UserSettings(
            new ExchangeUserSettings(
                "https://mail.contoso.com/EWS/Exchange.asmx",
                ExchangeOptions.UsernamePasswordMode,
                "mail-bot",
                "CONTOSO"),
            new AiUserSettings("https://provider.test/v1", "gpt-5"));

        await store.SaveAsync(document);
        var loaded = await CreateStore().GetAsync();

        Assert.Equal(document.Exchange, loaded.Exchange);
        Assert.Equal(document.Ai, loaded.Ai);
    }

    [Fact]
    public async Task Save_WritesOnlyNonSecretFields()
    {
        var store = CreateStore();
        await store.SaveAsync(new UserSettings(
            new ExchangeUserSettings(
                "https://mail.contoso.com/EWS/Exchange.asmx",
                ExchangeOptions.UsernamePasswordMode,
                "mail-bot",
                null),
            new AiUserSettings("https://provider.test/v1", "gpt-5")));

        var json = File.ReadAllText(SettingsPath);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var exchange = root.GetProperty("exchange");
        Assert.Equal("https://mail.contoso.com/EWS/Exchange.asmx", exchange.GetProperty("ewsUrl").GetString());
        Assert.Equal("UsernamePassword", exchange.GetProperty("authentication").GetString());
        Assert.Equal("mail-bot", exchange.GetProperty("username").GetString());
        Assert.False(exchange.TryGetProperty("password", out _));

        var ai = root.GetProperty("ai");
        Assert.Equal("https://provider.test/v1", ai.GetProperty("baseUrl").GetString());
        Assert.Equal("gpt-5", ai.GetProperty("model").GetString());
        Assert.False(ai.TryGetProperty("apiKey", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public async Task Get_WhenTheFileIsEmpty_ReturnsEmpty(string content)
    {
        WriteFile(SettingsPath, content);

        var settings = await CreateStore().GetAsync();

        Assert.Null(settings.Exchange);
        Assert.Null(settings.Ai);
    }

    [Fact]
    public async Task Get_WhenTheFileIsCorrupt_QuarantinesItAndReportsEmpty()
    {
        WriteFile(SettingsPath, "{ this is not json");

        var settings = await CreateStore().GetAsync();

        // The damaged file is preserved once (never silently destroyed) and reported as
        // "no settings" so a broken preferences file cannot break the mail client.
        Assert.Null(settings.Exchange);
        Assert.Null(settings.Ai);
        Assert.False(File.Exists(SettingsPath));
        Assert.True(File.Exists(SettingsPath + ".corrupt"));
        Assert.Equal("{ this is not json", File.ReadAllText(SettingsPath + ".corrupt"));
    }

    [Fact]
    public async Task Save_WhenTheFileIsCorrupt_QuarantinesItBeforeOverwriting()
    {
        WriteFile(SettingsPath, "not json at all");

        await CreateStore().SaveAsync(new UserSettings(null, new AiUserSettings("https://provider.test/v1", null)));

        Assert.Equal("not json at all", File.ReadAllText(SettingsPath + ".corrupt"));
        var loaded = await CreateStore().GetAsync();
        Assert.Equal("https://provider.test/v1", loaded.Ai?.BaseUrl);
    }

    [Fact]
    public async Task Get_WhenOnlyTheLegacyAiFileExists_MigratesItOnceAndKeepsTheLegacyFile()
    {
        WriteFile(LegacyPath, """{"baseUrl":"https://legacy.test/v1","model":"legacy-model"}""");

        var migrated = await CreateStore().GetAsync();

        Assert.Null(migrated.Exchange);
        Assert.Equal("https://legacy.test/v1", migrated.Ai?.BaseUrl);
        Assert.Equal("legacy-model", migrated.Ai?.Model);

        // The migration is persisted so it only ever runs once, and the legacy file (which
        // never held the API key) is deliberately left in place.
        Assert.True(File.Exists(SettingsPath));
        Assert.True(File.Exists(LegacyPath));

        // A second read uses settings.json; changing the legacy file cannot change anything.
        WriteFile(LegacyPath, """{"baseUrl":"https://other.test/v1"}""");
        var second = await CreateStore().GetAsync();
        Assert.Equal("https://legacy.test/v1", second.Ai?.BaseUrl);
    }

    [Fact]
    public async Task Get_WhenSettingsJsonExists_DoesNotMigrateTheLegacyFile()
    {
        WriteFile(SettingsPath, """{"ai":{"baseUrl":"https://current.test/v1"}}""");
        WriteFile(LegacyPath, """{"baseUrl":"https://legacy.test/v1"}""");

        var settings = await CreateStore().GetAsync();

        Assert.Equal("https://current.test/v1", settings.Ai?.BaseUrl);
    }

    [Fact]
    public async Task Get_WhenTheLegacyFileIsCorrupt_ReportsEmptyWithoutThrowing()
    {
        WriteFile(LegacyPath, "{ not json");

        var settings = await CreateStore().GetAsync();

        Assert.Null(settings.Ai);
    }

    [Theory]
    [InlineData("""{"exchange":{"authentication":"Windows"}}""")]
    [InlineData("""{"exchange":{"ewsUrl":"   ","authentication":"Windows"}}""")]
    public async Task Get_WhenTheExchangeSectionHasNoEndpoint_TreatsItAsNotConfigured(string json)
    {
        WriteFile(SettingsPath, json);

        var settings = await CreateStore().GetAsync();

        Assert.Null(settings.Exchange);
    }

    [Fact]
    public async Task Save_ReplacesTheExchangeSectionAndKeepsTheAiSection()
    {
        var store = CreateStore();
        await store.SaveAsync(new UserSettings(null, new AiUserSettings("https://provider.test/v1", "gpt-5")));
        var current = await store.GetAsync();

        await store.SaveAsync(current with
        {
            Exchange = new ExchangeUserSettings(
                "https://mail.contoso.com/EWS/Exchange.asmx",
                ExchangeOptions.WindowsMode,
                null,
                null),
        });

        var loaded = await CreateStore().GetAsync();

        Assert.Equal("https://mail.contoso.com/EWS/Exchange.asmx", loaded.Exchange?.EwsUrl);
        Assert.Equal("https://provider.test/v1", loaded.Ai?.BaseUrl);
    }

    [Fact]
    public async Task Save_WhenEverythingIsCleared_RemovesTheFile()
    {
        var store = CreateStore();
        await store.SaveAsync(new UserSettings(
            new ExchangeUserSettings("https://mail.contoso.com/EWS/Exchange.asmx", ExchangeOptions.WindowsMode, null, null),
            null));

        await store.SaveAsync(UserSettings.Empty);

        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public async Task Clear_RemovesTheFile()
    {
        var store = CreateStore();
        await store.SaveAsync(new UserSettings(null, new AiUserSettings("https://provider.test/v1", null)));

        await store.ClearAsync();

        Assert.False(File.Exists(SettingsPath));
        var loaded = await store.GetAsync();
        Assert.Null(loaded.Ai);
    }
}

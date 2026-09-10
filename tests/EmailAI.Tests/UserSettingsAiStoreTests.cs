using EmailAI.Domain.AI;
using EmailAI.Domain.Exchange;
using EmailAI.Domain.Settings;
using EmailAI.Infrastructure.Settings;

namespace EmailAI.Tests;

/// <summary>
/// The AI overrides adapter on top of the unified per-user document: only the non-secret Base
/// URL/model are involved, an all-empty override is stored as "absent" (so the server
/// configuration is unambiguously back in charge) and the Exchange section is never touched.
/// </summary>
public sealed class UserSettingsAiStoreTests
{
    [Fact]
    public async Task Get_WithoutStoredAiOverrides_ReturnsEmptyValues()
    {
        var store = new UserSettingsAiStore(new InMemoryUserSettingsStore());

        var settings = await store.GetAsync();

        Assert.Null(settings.BaseUrl);
        Assert.Null(settings.Model);
    }

    [Fact]
    public async Task SaveThenGet_RoundTripsTheOverrides()
    {
        var document = new InMemoryUserSettingsStore();
        var store = new UserSettingsAiStore(document);

        await store.SaveAsync(new AiUserSettings("https://provider.test/v1", "gpt-5"));

        Assert.Equal("https://provider.test/v1", document.Stored.Ai?.BaseUrl);
        Assert.Equal("gpt-5", document.Stored.Ai?.Model);
        var loaded = await store.GetAsync();
        Assert.Equal("https://provider.test/v1", loaded.BaseUrl);
    }

    [Fact]
    public async Task Save_WithBlankValues_RemovesTheAiSectionEntirely()
    {
        var document = new InMemoryUserSettingsStore();
        var store = new UserSettingsAiStore(document);
        await store.SaveAsync(new AiUserSettings("https://provider.test/v1", "gpt-5"));

        await store.SaveAsync(new AiUserSettings("   ", null));

        Assert.Null(document.Stored.Ai);
    }

    [Fact]
    public async Task Save_TrimsValuesAndKeepsTheExchangeSection()
    {
        var document = new InMemoryUserSettingsStore();
        await document.SaveAsync(new UserSettings(
            new ExchangeUserSettings(
                "https://mail.contoso.com/EWS/Exchange.asmx",
                ExchangeOptions.WindowsMode,
                null,
                null),
            null));
        var store = new UserSettingsAiStore(document);

        await store.SaveAsync(new AiUserSettings("  https://provider.test/v1  ", " gpt-5 "));

        Assert.Equal("https://provider.test/v1", document.Stored.Ai?.BaseUrl);
        Assert.Equal("gpt-5", document.Stored.Ai?.Model);
        Assert.Equal(
            "https://mail.contoso.com/EWS/Exchange.asmx",
            document.Stored.Exchange?.EwsUrl);
    }
}

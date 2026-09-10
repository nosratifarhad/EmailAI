using EmailAI.Api.Configuration;
using EmailAI.Application.AI;
using EmailAI.Domain.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmailAI.Tests;

/// <summary>
/// Unit tests for the precedence policy implemented by <see cref="AiCredentialCoordinator"/>
/// (the single runtime coordinator behind IAiCredentialProvider, IAiCredentialService and
/// IAiConfigurationProvider): key source precedence plus per-user Base URL/Model overrides.
/// </summary>
public sealed class AiCredentialCoordinatorTests
{
    private static AiCredentialCoordinator CreateCoordinator(
        AiOptions options,
        InMemoryAiCredentialStore? store = null,
        InMemoryAiUserSettingsStore? userSettings = null)
        => new(
            new StaticOptionsMonitor<AiOptions>(options),
            store ?? new InMemoryAiCredentialStore(),
            userSettings ?? new InMemoryAiUserSettingsStore(),
            NullLogger<AiCredentialCoordinator>.Instance);

    // ------------------------------------------------------------------
    // auto (default): secure store authoritative, env/config as fallback
    // ------------------------------------------------------------------

    [Fact]
    public async Task Auto_EmptyStore_NoConfigKey_ReturnsNull_AndAllowsSaving()
    {
        var options = TestOptions.Ai(apiKey: null);
        var coordinator = CreateCoordinator(options);

        var status = await coordinator.GetStatusAsync();

        Assert.True(status.UserManaged);
        Assert.False(status.HasApiKey);
        Assert.Equal("windows", status.EffectiveSource); // where the next key will be stored
        Assert.Null(await coordinator.GetApiKeyAsync());

        await coordinator.SaveSettingsAsync(null, null, "stored-key");
        Assert.Equal("stored-key", await coordinator.GetApiKeyAsync());
        Assert.True((await coordinator.GetStatusAsync()).HasApiKey);
    }

    [Fact]
    public async Task Auto_StoreWins_OverConfiguredKey()
    {
        var store = new InMemoryAiCredentialStore();
        await store.SaveApiKeyAsync("store-key");
        var coordinator = CreateCoordinator(TestOptions.Ai(apiKey: "config-key"), store);

        Assert.Equal("store-key", await coordinator.GetApiKeyAsync());

        var status = await coordinator.GetStatusAsync();
        Assert.True(status.UserManaged);
        Assert.True(status.HasApiKey);
        Assert.Equal("windows", status.EffectiveSource);
    }

    [Fact]
    public async Task Auto_ConfiguredKeyUsed_WhenStoreEmpty_AndStatusReportsFallback()
    {
        var coordinator = CreateCoordinator(TestOptions.Ai(apiKey: "config-key"));

        Assert.Equal("config-key", await coordinator.GetApiKeyAsync());

        var status = await coordinator.GetStatusAsync();
        Assert.True(status.UserManaged);          // store is still writable
        Assert.True(status.HasApiKey);            // effective key exists (config fallback)
        Assert.Equal("environment", status.EffectiveSource); // the fallback is in effect
    }

    [Fact]
    public async Task Auto_AfterSavingInStore_TheConfiguredKeyIsNoLongerUsed()
    {
        var store = new InMemoryAiCredentialStore();
        var coordinator = CreateCoordinator(TestOptions.Ai(apiKey: "config-key"), store);

        await coordinator.SaveSettingsAsync(null, null, "new-store-key");

        Assert.Equal("new-store-key", await coordinator.GetApiKeyAsync());
        var status = await coordinator.GetStatusAsync();
        Assert.Equal("windows", status.EffectiveSource);
    }

    // ------------------------------------------------------------------
    // windows: store is the only source
    // ------------------------------------------------------------------

    [Fact]
    public async Task Windows_IgnoresConfiguredKey_AndSavesDeletesThroughStore()
    {
        var store = new InMemoryAiCredentialStore();
        var options = TestOptions.Ai(apiKey: "config-key");
        options.CredentialSource = "windows";
        var coordinator = CreateCoordinator(options, store);

        Assert.Null(await coordinator.GetApiKeyAsync()); // config key must not leak through

        await coordinator.SaveSettingsAsync(null, null, "stored-key");
        Assert.Equal("stored-key", await coordinator.GetApiKeyAsync());

        await coordinator.DeleteKeyAsync();
        Assert.Null(await coordinator.GetApiKeyAsync());
        Assert.True(store.DeleteCalled);
    }

    // ------------------------------------------------------------------
    // environment: read-only from configuration, save/delete rejected
    // ------------------------------------------------------------------

    [Fact]
    public async Task Environment_ReturnsConfiguredKey_AndRejectsSaveAndDelete()
    {
        var store = new InMemoryAiCredentialStore();
        var options = TestOptions.Ai(apiKey: "env-key");
        options.CredentialSource = "environment";
        var coordinator = CreateCoordinator(options, store);

        Assert.Equal("env-key", await coordinator.GetApiKeyAsync());

        var status = await coordinator.GetStatusAsync();
        Assert.False(status.UserManaged);
        Assert.True(status.HasApiKey);
        Assert.Equal("environment", status.EffectiveSource);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SaveSettingsAsync(null, null, "new-key"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SaveSettingsAsync("https://x.example/v1", null, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.DeleteKeyAsync());
        Assert.Null(await store.GetApiKeyAsync()); // nothing may reach the store
    }

    [Fact]
    public async Task Environment_NoConfiguredKey_ReportsNoKey()
    {
        var options = TestOptions.Ai(apiKey: null);
        options.CredentialSource = "environment";
        var coordinator = CreateCoordinator(options);

        var status = await coordinator.GetStatusAsync();

        Assert.False(status.UserManaged);
        Assert.False(status.HasApiKey);
        Assert.Null(await coordinator.GetApiKeyAsync());
    }

    [Fact]
    public async Task Environment_IgnoresPerUserOverrides_ForEffectiveSettings()
    {
        var options = TestOptions.Ai(baseUrl: "https://server.example/v1", model: "server-model", apiKey: "env-key");
        options.CredentialSource = "environment";
        var userSettings = new InMemoryAiUserSettingsStore();
        await userSettings.SaveAsync(new AiUserSettings("https://user.example/v1", "user-model"));
        var coordinator = CreateCoordinator(options, userSettings: userSettings);

        var effective = await coordinator.GetEffectiveOptionsAsync();

        Assert.Equal("https://server.example/v1", effective.BaseUrl);
        Assert.Equal("server-model", effective.Model);
    }


    // ------------------------------------------------------------------
    // Per-user Base URL / Model overrides (auto/windows)
    // ------------------------------------------------------------------

    [Fact]
    public async Task UserOverride_WinsOverServerConfiguration_InAutoMode()
    {
        var options = TestOptions.Ai(baseUrl: "https://server.example/v1", model: "server-model", apiKey: null);
        var userSettings = new InMemoryAiUserSettingsStore();
        await userSettings.SaveAsync(new AiUserSettings("https://user.example/v1", "user-model"));
        var coordinator = CreateCoordinator(options, userSettings: userSettings);

        var effective = await coordinator.GetEffectiveOptionsAsync();
        Assert.Equal("https://user.example/v1", effective.BaseUrl);
        Assert.Equal("user-model", effective.Model);

        var status = await coordinator.GetStatusAsync();
        Assert.True(status.Configured);
        Assert.Equal("https://user.example/v1", status.BaseUrl);
        Assert.Equal("user-model", status.Model);
    }

    [Fact]
    public async Task UserOverride_Cleared_FallsBackToServerConfiguration()
    {
        var options = TestOptions.Ai(baseUrl: "https://server.example/v1", model: "server-model");
        var userSettings = new InMemoryAiUserSettingsStore();
        await userSettings.SaveAsync(new AiUserSettings(null, null)); // nothing stored
        var coordinator = CreateCoordinator(options, userSettings: userSettings);

        var effective = await coordinator.GetEffectiveOptionsAsync();
        Assert.Equal("https://server.example/v1", effective.BaseUrl);
        Assert.Equal("server-model", effective.Model);
    }

    [Fact]
    public async Task SaveSettingsAsync_PersistsBaseUrlModelAndKey_ThenClearsOverride()
    {
        var options = TestOptions.Ai(baseUrl: "https://server.example/v1", model: "server-model", apiKey: null);
        var store = new InMemoryAiCredentialStore();
        var userSettings = new InMemoryAiUserSettingsStore();
        var coordinator = CreateCoordinator(options, store, userSettings);

        await coordinator.SaveSettingsAsync("https://user.example/v1", "user-model", "new-key");

        Assert.Equal("https://user.example/v1", userSettings.Stored.BaseUrl);
        Assert.Equal("user-model", userSettings.Stored.Model);
        Assert.Equal("new-key", await store.GetApiKeyAsync());
        Assert.Equal("new-key", await coordinator.GetApiKeyAsync());

        var status = await coordinator.GetStatusAsync();
        Assert.Equal("https://user.example/v1", status.BaseUrl);
        Assert.Equal("user-model", status.Model);
        Assert.True(status.HasApiKey);

        // Clearing the override puts the server configuration back in charge.
        await coordinator.SaveSettingsAsync(string.Empty, string.Empty, null);

        Assert.Null(userSettings.Stored.BaseUrl);
        Assert.Null(userSettings.Stored.Model);
        var afterClear = await coordinator.GetStatusAsync();
        Assert.Equal("https://server.example/v1", afterClear.BaseUrl);
        Assert.Equal("server-model", afterClear.Model);
        Assert.True(afterClear.HasApiKey); // key was not touched by clearing the override
    }

    [Fact]
    public async Task SaveSettingsAsync_KeyOnly_LeavesOverridesUntouched()
    {
        var userSettings = new InMemoryAiUserSettingsStore();
        await userSettings.SaveAsync(new AiUserSettings("https://user.example/v1", "user-model"));
        var coordinator = CreateCoordinator(TestOptions.Ai(), userSettings: userSettings);

        await coordinator.SaveSettingsAsync(null, null, "only-a-key");

        Assert.Equal("https://user.example/v1", userSettings.Stored.BaseUrl);
        Assert.Equal("user-model", userSettings.Stored.Model);
        Assert.Equal("only-a-key", await coordinator.GetApiKeyAsync());
    }


    // ------------------------------------------------------------------
    // Validation
    // ------------------------------------------------------------------

    [Fact]
    public async Task SaveSettingsAsync_EmptyKeyAndNoSettings_ThrowsArgumentException_WithoutTouchingStores()
    {
        var store = new InMemoryAiCredentialStore();
        var userSettings = new InMemoryAiUserSettingsStore();
        var coordinator = CreateCoordinator(TestOptions.Ai(), store, userSettings);

        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.SaveSettingsAsync(null, null, "   "));
        Assert.Null(await store.GetApiKeyAsync());
        Assert.Null(userSettings.Stored.BaseUrl);
    }

    [Fact]
    public async Task SaveSettingsAsync_InvalidBaseUrl_Throws_WithoutTouchingStores()
    {
        var store = new InMemoryAiCredentialStore();
        var userSettings = new InMemoryAiUserSettingsStore();
        var coordinator = CreateCoordinator(TestOptions.Ai(), store, userSettings);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => coordinator.SaveSettingsAsync("not-a-url", "m", "k"));

        Assert.Contains("http(s)", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await store.GetApiKeyAsync());
        Assert.Null(userSettings.Stored.BaseUrl);
    }

    [Fact]
    public async Task SaveSettingsAsync_WhitespaceBaseUrl_ClearsTheOverride()
    {
        var userSettings = new InMemoryAiUserSettingsStore();
        await userSettings.SaveAsync(new AiUserSettings("https://user.example/v1", "user-model"));
        var coordinator = CreateCoordinator(TestOptions.Ai(), userSettings: userSettings);

        await coordinator.SaveSettingsAsync("   ", "   ", null);

        Assert.Null(userSettings.Stored.BaseUrl);
        Assert.Null(userSettings.Stored.Model);
    }

    [Fact]
    public async Task DeleteKey_WhenNothingStored_IsANoOp()
    {
        var coordinator = CreateCoordinator(TestOptions.Ai(apiKey: null));

        await coordinator.DeleteKeyAsync(); // must not throw
    }
}


using EmailAI.Application.AI;

namespace EmailAI.Tests;

/// <summary>
/// "Can the AI assistant be used right now, and why not?" - the pre-flight check the mail UI
/// runs before an AI operation. Every state must be a documented verdict with a user-facing
/// message and a fixing action, and the service must never throw for an environment problem.
/// </summary>
public sealed class AiReadinessServiceTests
{
    [Fact]
    public async Task NotConfigured_ExplainsThatNoServiceIsConfigured()
    {
        var readiness = await Service(Status(configured: false, baseUrl: null, model: null))
            .GetReadinessAsync(probe: false);

        Assert.Equal(AiReadinessStatus.NotConfigured, readiness.Status);
        Assert.Equal("ai_not_configured", readiness.ErrorCode);
        Assert.False(readiness.IsReady);
        Assert.True(readiness.CanOpenSettings);
        Assert.Equal(AiUserMessages.OpenSettingsAction, readiness.Action);
        Assert.Contains("No AI service is configured", readiness.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingApiKey_IsDistinguishedFromNotConfigured()
    {
        var readiness = await Service(Status(hasApiKey: false, userManaged: true))
            .GetReadinessAsync(probe: false);

        Assert.Equal(AiReadinessStatus.MissingApiKey, readiness.Status);
        Assert.False(readiness.IsReady);
        Assert.Contains("no API key is saved", readiness.Message, StringComparison.Ordinal);
        Assert.Equal(AiUserMessages.OpenSettingsAction, readiness.Action);
    }

    [Fact]
    public async Task MissingModel_IsReported_EvenWhenTheKeyExists()
    {
        var readiness = await Service(Status(model: "  "))
            .GetReadinessAsync(probe: false);

        Assert.Equal(AiReadinessStatus.MissingModel, readiness.Status);
        Assert.Contains("No model is selected", readiness.Message, StringComparison.Ordinal);
        Assert.Equal(AiUserMessages.OpenSettingsAction, readiness.Action);
    }

    [Fact]
    public async Task InvalidBaseUrl_IsRejectedAsAConfigurationProblem()
    {
        var readiness = await Service(Status(baseUrl: "not-a-url"))
            .GetReadinessAsync(probe: false);

        Assert.Equal(AiReadinessStatus.InvalidConfiguration, readiness.Status);
        Assert.Equal("ai_invalid_configuration", readiness.ErrorCode);
        Assert.Contains("not a valid http(s) address", readiness.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ready_WithoutAProbe_WhenEverythingIsConfigured()
    {
        var client = new RecordingProbeClient(new AiProbeResult(true, 12, null));
        var service = new AiReadinessService(new StubCredentialService(Status()), client);

        var readiness = await service.GetReadinessAsync(probe: false);

        Assert.True(readiness.IsReady);
        Assert.Equal(AiReadinessStatus.Ready, readiness.Status);
        Assert.Equal("ai_ready", readiness.ErrorCode);
        // The cheap check must not touch the provider.
        Assert.Equal(0, client.ProbeCalls);
    }

    [Fact]
    public async Task Ready_WhenTheProbeSucceeds()
    {
        var readiness = await Service(Status(), new AiProbeResult(true, 25, null))
            .GetReadinessAsync(probe: true);

        Assert.True(readiness.IsReady);
    }

    [Fact]
    public async Task EnvironmentManagedDeploymentWithoutAStoredKey_IsReady_WhenTheKeyComesFromConfiguration()
    {
        // userManaged: false (AI_CREDENTIAL_SOURCE=environment): the key lives in the
        // deployment configuration, so the per-user store is irrelevant and nothing is fixable
        // in Settings.
        var readiness = await Service(Status(hasApiKey: true, userManaged: false, effectiveSource: "environment"))
            .GetReadinessAsync(probe: false);

        Assert.True(readiness.IsReady);
        Assert.False(readiness.CanOpenSettings);
    }

    [Theory]
    [InlineData(AiErrorKind.Authentication, AiReadinessStatus.AuthenticationFailed, "ai_authentication_failed")]
    [InlineData(AiErrorKind.RateLimited, AiReadinessStatus.RateLimited, "ai_rate_limited")]
    [InlineData(AiErrorKind.Timeout, AiReadinessStatus.Timeout, "ai_timeout")]
    [InlineData(AiErrorKind.Unavailable, AiReadinessStatus.Unreachable, "ai_unavailable")]
    [InlineData(AiErrorKind.InvalidConfiguration, AiReadinessStatus.InvalidConfiguration, "ai_invalid_configuration")]
    public async Task ProbeFailures_AreClassified_IntoAnActionableState(
        AiErrorKind kind,
        AiReadinessStatus expectedStatus,
        string expectedCode)
    {
        var service = Service(Status(), new AiProbeResult(false, null, "probe failed", kind));

        var readiness = await service.GetReadinessAsync(probe: true);

        Assert.Equal(expectedStatus, readiness.Status);
        Assert.Equal(expectedCode, readiness.ErrorCode);
        Assert.False(readiness.IsReady);
        Assert.False(string.IsNullOrWhiteSpace(readiness.Message));
    }

    [Fact]
    public async Task ProbeThatThrows_BecomesUnreachable_NotACrash()
    {
        var service = Service(Status(), new AiException(AiErrorKind.Unavailable, "connect refused"));

        var readiness = await service.GetReadinessAsync(probe: true);

        Assert.Equal(AiReadinessStatus.Unreachable, readiness.Status);
        // The internal/transport message never reaches the user.
        Assert.DoesNotContain("connect refused", readiness.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CredentialStoreFailure_IsReportedAsAStoreProblem()
    {
        var service = new AiReadinessService(
            new ThrowingCredentialService(new AiCredentialStoreException("store unavailable")),
            new RecordingProbeClient(new AiProbeResult(true, 1, null)));

        var readiness = await service.GetReadinessAsync(probe: false);

        Assert.Equal("ai_credential_store_failed", readiness.ErrorCode);
        Assert.Equal(AiReadinessStatus.MissingApiKey, readiness.Status);
        Assert.Contains("Windows Credential Manager", readiness.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedFailure_IsReported_WithoutExposingTheException()
    {
        var service = new AiReadinessService(
            new ThrowingCredentialService(new InvalidOperationException("internal detail")),
            new RecordingProbeClient(new AiProbeResult(true, 1, null)));

        var readiness = await service.GetReadinessAsync(probe: true);

        Assert.Equal(AiReadinessStatus.InvalidConfiguration, readiness.Status);
        Assert.DoesNotContain("internal detail", readiness.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static AiReadinessService Service(AiCredentialStatus status, AiProbeResult? probe = null)
        => new(
            new StubCredentialService(status),
            new RecordingProbeClient(probe ?? new AiProbeResult(true, 5, null)));

    private static AiReadinessService Service(AiCredentialStatus status, Exception probeFailure)
        => new(new StubCredentialService(status), new ThrowingProbeClient(probeFailure));

    private static AiCredentialStatus Status(
        bool configured = true,
        bool hasApiKey = true,
        bool userManaged = true,
        string? baseUrl = "https://provider.test/v1",
        string? model = "gpt-5",
        string effectiveSource = "windows")
        => new(configured, hasApiKey, userManaged, effectiveSource, baseUrl, model, 60);

    private sealed class StubCredentialService(AiCredentialStatus status) : IAiCredentialService
    {
        public Task<AiCredentialStatus> GetStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(status);

        public Task SaveSettingsAsync(string? baseUrl, string? model, string? apiKey, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteKeyAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingCredentialService(Exception exception) : IAiCredentialService
    {
        public Task<AiCredentialStatus> GetStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromException<AiCredentialStatus>(exception);

        public Task SaveSettingsAsync(string? baseUrl, string? model, string? apiKey, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteKeyAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingProbeClient(AiProbeResult result) : IAiClient
    {
        public int ProbeCalls { get; private set; }

        public Task<AiChatCompletion> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<AiProbeResult> ProbeAsync(CancellationToken cancellationToken)
        {
            ProbeCalls++;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingProbeClient(Exception exception) : IAiClient
    {
        public Task<AiChatCompletion> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<AiProbeResult> ProbeAsync(CancellationToken cancellationToken)
            => Task.FromException<AiProbeResult>(exception);
    }
}

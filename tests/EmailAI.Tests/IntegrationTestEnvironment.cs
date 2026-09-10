using EmailAI.Domain.AI;
using EmailAI.Domain.Exchange;

namespace EmailAI.Tests;

/// <summary>
/// Reads the opt-in REAL integration-test environment. When the variables below are
/// absent the tests report "not configured" and complete without touching any
/// external service, so the normal unit suite never requires a live provider or
/// real credentials. Real credentials are read from the environment at run time
/// only and are never printed, asserted or committed.
/// </summary>
internal static class IntegrationTestEnvironment
{
    public const string AiEnabledVariable = "EMAILAI_AI_INTEGRATION_TEST";
    public const string AiBaseUrlVariable = "EMAILAI_AI_BASE_URL";
    public const string AiApiKeyVariable = "EMAILAI_AI_API_KEY";
    public const string AiModelVariable = "EMAILAI_AI_MODEL";

    public const string ExchangeEnabledVariable = "EMAILAI_EXCHANGE_INTEGRATION_TEST";

    /// <summary>True when all four AI integration variables are present (flag must be "true").</summary>
    public static bool AiConfigured =>
        IsTrue(AiEnabledVariable)
        && IsSet(AiBaseUrlVariable)
        && IsSet(AiApiKeyVariable)
        && IsSet(AiModelVariable);

    /// <summary>Configured AI provider inputs. Callers must check <see cref="AiConfigured"/> first.</summary>
    public static AiOptions AiProviderOptions => new()
    {
        BaseUrl = GetRequired(AiBaseUrlVariable),
        ApiKey = GetRequired(AiApiKeyVariable),
        Model = GetRequired(AiModelVariable),
        TimeoutSeconds = 60,
    };

    /// <summary>
    /// True when the Exchange integration flag is on and the base EWS endpoint (plus,
    /// for UsernamePassword mode, the explicit credential variables) are present.
    /// </summary>
    public static bool ExchangeConfigured(string mode)
    {
        if (!IsTrue(ExchangeEnabledVariable) || !IsSet("EXCHANGE_EWS_URL"))
        {
            return false;
        }

        if (string.Equals(mode, ExchangeOptions.UsernamePasswordMode, StringComparison.OrdinalIgnoreCase))
        {
            return IsSet("EXCHANGE_USERNAME") && IsSet("EXCHANGE_PASSWORD");
        }

        return true;
    }

    public static ExchangeOptions ExchangeOptionsFor(string mode)
    {
        var options = new ExchangeOptions
        {
            EwsUrl = GetRequired("EXCHANGE_EWS_URL"),
            Authentication = mode,
            ExchangeVersion = Environment.GetEnvironmentVariable("EXCHANGE_VERSION") is { Length: > 0 } version
                ? version
                : "Exchange2013_SP1",
            TimeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("EXCHANGE_TIMEOUT_SECONDS"), out var timeout)
                ? timeout
                : 30,
        };

        if (string.Equals(mode, ExchangeOptions.UsernamePasswordMode, StringComparison.OrdinalIgnoreCase))
        {
            options.Domain = NullIfBlank(Environment.GetEnvironmentVariable("EXCHANGE_DOMAIN"));
            options.Username = GetRequired("EXCHANGE_USERNAME");
            options.Password = GetRequired("EXCHANGE_PASSWORD");
        }

        return options;
    }

    private static bool IsTrue(string variable)
        => bool.TryParse(Environment.GetEnvironmentVariable(variable), out var value) && value;

    private static bool IsSet(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string GetRequired(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"The integration-test environment variable '{variable}' is not set.");
        }

        return value;
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}

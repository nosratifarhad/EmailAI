using EmailAI.Domain.Exchange;
using Microsoft.Extensions.Options;

namespace EmailAI.Api.Configuration;

public static class ExchangeConfiguration
{
    /// <summary>
    /// Binds the "Exchange" section from appsettings.json and applies the
    /// documented EXCHANGE_* environment variables on top. No secrets live
    /// in source control; credentials are supplied through the environment.
    /// </summary>
    public static IServiceCollection ConfigureExchangeOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ExchangeOptions>()
            .Bind(configuration.GetSection(ExchangeOptions.SectionName))
            .Configure(ApplyEnvironmentOverrides)
            .ValidateOnStart();

        return services;
    }

    private static void ApplyEnvironmentOverrides(ExchangeOptions options)
    {
        options.EwsUrl = Env("EXCHANGE_EWS_URL") ?? options.EwsUrl;
        options.Authentication = Env("EXCHANGE_AUTHENTICATION") ?? options.Authentication;
        options.Domain = Env("EXCHANGE_DOMAIN") ?? options.Domain;
        options.Username = Env("EXCHANGE_USERNAME") ?? options.Username;
        options.Password = Env("EXCHANGE_PASSWORD") ?? options.Password;
        options.Mailbox = Env("EXCHANGE_MAILBOX") ?? options.Mailbox;
        options.ExchangeVersion = Env("EXCHANGE_VERSION") ?? options.ExchangeVersion;

        if (int.TryParse(Env("EXCHANGE_TIMEOUT_SECONDS"), out var timeout) && timeout > 0)
        {
            options.TimeoutSeconds = timeout;
        }

        // Explicit-mode validation happens after environment overrides are applied and
        // before any Exchange attempt: an unsupported mode, or a UsernamePassword mode
        // missing its username/password, fails configuration immediately.
        var error = ExchangeOptions.GetConfigurationError(options);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }
    }

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

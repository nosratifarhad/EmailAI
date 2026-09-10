using EmailAI.Domain.Exchange;

namespace EmailAI.Application.Settings;

/// <summary>
/// Probes one candidate Exchange configuration WITHOUT persisting it. Used by
/// <c>POST /api/settings/exchange/test</c> so a user can verify a draft before saving.
///
/// Implementations must authenticate with exactly the single configured mode (Windows or
/// UsernamePassword) and must never fall back to another credential mechanism, never
/// mutate configuration and never surface a credential in the returned error text.
/// </summary>
public interface IExchangeConnectionTester
{
    /// <summary>Runs one connectivity probe and reports a secret-free result.</summary>
    Task<ExchangeHealthStatus> TestAsync(
        ExchangeOptions options,
        CancellationToken cancellationToken = default);
}

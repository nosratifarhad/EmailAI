namespace EmailAI.Domain.Exchange;

/// <summary>
/// The AUTHORITATIVE identity of the mailbox EmailAI is operating on - the person the
/// application is helping ("the current user").
///
/// It is derived from the Exchange context the application already authenticates with
/// (the configured/impersonated mailbox account, resolved through the Exchange directory)
/// and only falls back to the local Windows account when Exchange cannot answer. It is
/// never inferred from email content: an email talking about this person in the third
/// person is DATA the AI may interpret, while this record is the trusted identity the AI
/// is given.
///
/// No secret is ever part of this record (no password, no token) - only the display name,
/// the SMTP address and the account name, which the Settings page already shows.
/// </summary>
/// <param name="DisplayName">Directory display name, e.g. <c>Alex Doe</c>.</param>
/// <param name="SmtpAddress">Primary SMTP address, e.g. <c>alex@contoso.com</c>.</param>
/// <param name="AccountName">Account used to reach Exchange (SMTP/UPN/<c>DOMAIN\user</c>).</param>
/// <param name="Source">Where the identity came from (see the <c>*Source</c> constants).</param>
public sealed record MailboxIdentity(
    string? DisplayName,
    string? SmtpAddress,
    string? AccountName,
    string Source)
{
    /// <summary>Resolved through the Exchange directory (ResolveNames) - the strongest source.</summary>
    public const string ExchangeDirectorySource = "ExchangeDirectory";

    /// <summary>
    /// The mailbox configured for EWS impersonation (<c>Exchange:Mailbox</c>): the mailbox
    /// whose mail is being read, even when the authenticating account is a service account.
    /// </summary>
    public const string ConfiguredMailboxSource = "ConfiguredMailbox";

    /// <summary>The account configured for UsernamePassword authentication.</summary>
    public const string ConfiguredAccountSource = "ConfiguredAccount";

    /// <summary>The Windows/process account EmailAI runs as (Windows authentication).</summary>
    public const string WindowsIdentitySource = "WindowsIdentity";

    /// <summary>Nothing could be determined (Exchange is not configured and no account is known).</summary>
    public const string UnknownSource = "Unknown";

    /// <summary>An identity with no values at all.</summary>
    public static MailboxIdentity None { get; } = new(null, null, null, UnknownSource);

    /// <summary>Creates a normalised identity (blank values become null).</summary>
    public static MailboxIdentity Create(
        string? displayName,
        string? smtpAddress,
        string? accountName,
        string source)
        => new(
            Clean(displayName),
            Clean(smtpAddress),
            Clean(accountName),
            string.IsNullOrWhiteSpace(source) ? UnknownSource : source.Trim());

    /// <summary>True when at least one identifying value is known.</summary>
    public bool IsKnown => DisplayName is not null || SmtpAddress is not null || AccountName is not null;

    /// <summary>
    /// Names this identity may be referred to by in an email (display-name parts, account
    /// name parts, SMTP local part). Derived ONLY from the authoritative values above, so
    /// the AI never has to guess the user's name from the email content.
    /// </summary>
    public IReadOnlyList<string> Aliases => MailboxAliases.For(DisplayName, AccountName, SmtpAddress);

    /// <summary>Secret-free one-line description for logs/UI (never contains a credential).</summary>
    public string Describe()
        => IsKnown
            ? $"{DisplayName ?? AccountName} ({SmtpAddress ?? AccountName}) via {Source}"
            : $"unknown ({Source})";

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        // A display name can arrive wrapped as "Name <address>" - keep only the name part.
        var angle = trimmed.IndexOf('<');
        if (angle > 0 && trimmed.EndsWith('>'))
        {
            trimmed = trimmed[..angle].Trim().Trim('"');
        }

        return trimmed.Length == 0 ? null : trimmed;
    }
}

/// <summary>
/// Builds the alias list for the current user from authoritative identity values only.
/// The AI gets these as CONTEXT for reasoning (so "ask Alex" can be understood as
/// "the current user"); they are deliberately not used for mechanical string matching.
/// </summary>
public static class MailboxAliases
{
    /// <summary>Maximum number of aliases handed to the AI (keeps prompts small and focused).</summary>
    public const int MaxAliases = 8;

    private static readonly char[] Separators =
        [' ', '.', '-', '_', '\\', '/', '@', ',', ';', '(', ')'];

    /// <summary>
    /// Aliases for a known identity, most specific first (never empty when an identity is
    /// known). Values shorter than two characters are dropped so initials and noise
    /// ("a", "x") never become aliases.
    /// </summary>
    public static IReadOnlyList<string> For(string? displayName, string? accountName, string? smtpAddress)
    {
        var aliases = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            if (aliases.Count >= MaxAliases || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var candidate = value.Trim();
            if (candidate.Length < 2 || !seen.Add(candidate))
            {
                return;
            }

            aliases.Add(candidate);
        }

        void AddParts(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            foreach (var part in value.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                Add(part);
            }
        }

        Add(displayName);
        Add(accountName);
        Add(smtpAddress);
        AddParts(displayName);
        AddParts(accountName);
        AddParts(LocalPart(smtpAddress));

        return aliases;
    }

    private static string? LocalPart(string? smtpAddress)
    {
        if (string.IsNullOrWhiteSpace(smtpAddress))
        {
            return null;
        }

        var at = smtpAddress.IndexOf('@');
        return at > 0 ? smtpAddress[..at] : smtpAddress;
    }
}

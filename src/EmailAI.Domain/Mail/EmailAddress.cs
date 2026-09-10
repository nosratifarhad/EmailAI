namespace EmailAI.Domain.Mail;

/// <summary>
/// A display name / email address pair, independent of any transport technology
/// (EWS, SMTP, ...). Exchange stays the source of truth, but the domain layer
/// must not know about SOAP/EWS types.
/// </summary>
public sealed record EmailAddress(string? Name, string? Address)
{
    /// <summary>Formats as "Name &lt;address&gt;", falling back to the raw address.</summary>
    public override string ToString() =>
        string.IsNullOrWhiteSpace(Name)
            ? Address ?? string.Empty
            : $"{Name} <{Address}>";
}

public enum EmailImportance
{
    Low = 0,
    Normal = 1,
    High = 2,
}

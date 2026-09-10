using System.Net;
using System.Text.RegularExpressions;

namespace EmailAI.Application.Mail;

/// <summary>
/// Defence-in-depth sanitisation for UNTRUSTED email HTML before it is shown to the user.
///
/// The primary protection is the UI: the body is rendered inside a sandboxed, script-less
/// <c>&lt;iframe&gt;</c> with a restrictive Content-Security-Policy, so nothing in the mail
/// can script the application, reach the parent document, or leak a referrer. This type adds
/// a second layer that removes the constructs a mail client should never carry into the
/// document at all (scripts, nested frames, forms, event handlers, dangerous URL schemes)
/// while keeping the formatting users expect: paragraphs, headings, lists, links, emphasis,
/// inline styles and images.
///
/// It is deliberately a tolerant, "good enough" filter, not a full HTML parser: it is never
/// the only barrier (see above), and it must never throw or drop the whole body.
/// </summary>
public static partial class MailHtmlSanitizer
{
    /// <summary>Content-Security-Policy applied inside the sandbox document.</summary>
    public const string SandboxContentSecurityPolicy =
        "default-src 'none'; img-src data: cid: http: https:; style-src 'unsafe-inline'; " +
        "font-src data:; media-src 'none'; script-src 'none'; object-src 'none'; " +
        "frame-src 'none'; base-uri 'none'; form-action 'none'";

    /// <summary>
    /// Upper bound on sanitisation passes. Normal mail stabilises after one pass (the second
    /// one changes nothing); the bound only exists so adversarial input can never loop.
    /// </summary>
    private const int MaxPasses = 5;

    /// <summary>
    /// Sanitises email HTML. Returns an empty string for null/blank input, so callers can
    /// treat "no body" uniformly. The filter runs until it reaches a stable result: nesting
    /// tricks that would re-form a dangerous tag after a single pass (for example
    /// <c>&lt;scr&lt;script&gt;ipt&gt;</c>) therefore cannot survive.
    /// </summary>
    public static string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var output = html;
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var next = SanitizeOnce(output);
            if (string.Equals(next, output, StringComparison.Ordinal))
            {
                break;
            }

            output = next;
        }

        return output;
    }

    /// <summary>One sanitisation pass (the pass list below is ordered deliberately).</summary>
    private static string SanitizeOnce(string html)
    {
        var output = html;

        // Elements whose content is executable or would embed another document.
        output = DangerousContainerRegex().Replace(output, string.Empty);

        // Leftover opening/closing/void tags of the same families (for example a stray
        // "<iframe src=...>" that has no closing tag).
        output = DangerousTagRegex().Replace(output, string.Empty);

        // Server-side includes / conditional comments can smuggle markup.
        output = CommentRegex().Replace(output, string.Empty);

        // Inline event handlers (onclick, onerror, onload, ...).
        output = EventHandlerAttributeRegex().Replace(output, string.Empty);

        // Dangerous URL schemes in the attributes that can navigate or load.
        output = UrlAttributeRegex().Replace(output, NeutralizeDangerousUrl);

        // Links must not navigate the (sandboxed) frame away from the message.
        output = AnchorTagRegex().Replace(output, RewriteAnchor);

        return output;
    }

    /// <summary>
    /// Wraps sanitised body HTML in the standalone document used by the sandboxed iframe:
    /// UTF-8, a mail-friendly default stylesheet and the restrictive CSP above.
    /// Returns an empty string when there is no body to show.
    /// </summary>
    public static string ToSandboxDocument(string? html)
    {
        var body = Sanitize(html);
        if (body.Length == 0)
        {
            return string.Empty;
        }

        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"/>"
             + $"<meta http-equiv=\"Content-Security-Policy\" content=\"{SandboxContentSecurityPolicy}\"/>"
             + "<style>html{background:#ffffff}body{font-family:Segoe UI,Roboto,Arial,sans-serif;"
             + "font-size:14px;line-height:1.55;color:#1b1f24;margin:0;padding:14px 18px;"
             + "overflow-wrap:break-word;}a{color:#0b57d0;}img{max-width:100%;height:auto;}"
             + "table{max-width:100%;}</style>"
             + $"</head><body>{body}</body></html>";
    }

    /// <summary>
    /// Keeps a URL attribute only when its value is safe; a dangerous scheme becomes an empty
    /// value (never a broken page, never an injection).
    /// </summary>
    private static string NeutralizeDangerousUrl(Match match)
    {
        var value = match.Groups["value"].Value.Trim();
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1];
        }

        return IsSafeUrl(value)
            ? match.Value
            : match.Groups["name"].Value + "\"\"";
    }

    /// <summary>
    /// True for http(s), mailto, tel, cid, relative and fragment targets plus inline
    /// <c>data:image</c>. Everything else (javascript:, vbscript:, data:text/html, file:,
    /// ...) is refused, including obfuscated spellings such as "java\tscript:" or
    /// "&#106;avascript:".
    /// </summary>
    private static bool IsSafeUrl(string value)
    {
        var probe = WebUtility.HtmlDecode(WebUtility.HtmlDecode(value));
        probe = ControlAndSpaceRegex().Replace(probe, string.Empty).ToLowerInvariant();

        if (probe.Length == 0 || probe.StartsWith('#') || probe.StartsWith('/'))
        {
            return true;
        }

        if (probe.StartsWith("data:", StringComparison.Ordinal))
        {
            return probe.StartsWith("data:image/", StringComparison.Ordinal);
        }

        var colon = probe.IndexOf(':');
        if (colon <= 0)
        {
            // No scheme at all: a relative or protocol-relative target.
            return true;
        }

        var scheme = probe[..colon];
        return scheme is "http" or "https" or "mailto" or "tel" or "cid";
    }

    /// <summary>
    /// Opens links in a new window instead of navigating the message frame, and prevents the
    /// opened page from touching the opener.
    /// </summary>
    private static string RewriteAnchor(Match match)
    {
        var attributes = match.Groups["attributes"].Value;
        if (attributes.IndexOf("href", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return match.Value;
        }

        if (attributes.IndexOf("target", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return match.Value;
        }

        return $"<a{attributes} target=\"_blank\" rel=\"noopener noreferrer\">";
    }

    // ------------------------------------------------------------------
    // Patterns (kept separate for readability and reviewability)
    // ------------------------------------------------------------------

    /// <summary>"&lt;script&gt;...&lt;/script&gt;" and the other executable/embedded containers.</summary>
    [GeneratedRegex(
        """<(script|iframe|object|applet|form|noframes)\b[^>]*>.*?</\1\s*>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DangerousContainerRegex();

    /// <summary>Remaining tags of the same families (void tags, orphans, closing tags).</summary>
    [GeneratedRegex(
        """</?(?:script|iframe|frame|frameset|noframes|object|embed|applet|form|meta|link|base|input|button|textarea|select|option)\b[^>]*>""",
        RegexOptions.IgnoreCase)]
    private static partial Regex DangerousTagRegex();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    /// <summary>Inline event handlers: on* attributes in any quoting style.</summary>
    [GeneratedRegex(
        """\son[a-z-]+\s*=\s*("[^"]*"|'[^']*'|[^\s>]+)""",
        RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerAttributeRegex();

    /// <summary>Attributes that can load or navigate, captured for URL-scheme review.</summary>
    [GeneratedRegex(
        """(?<name>\b(?:href|src|srcset|action|formaction|background|poster|xlink:href)\s*=\s*)(?<value>"[^"]*"|'[^']*'|[^\s>]+)""",
        RegexOptions.IgnoreCase)]
    private static partial Regex UrlAttributeRegex();

    [GeneratedRegex(@"<a\b(?<attributes>[^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex AnchorTagRegex();

    /// <summary>Whitespace and control characters used to obfuscate a URL scheme.</summary>
    [GeneratedRegex(@"[\u0000-\u0020\u007f\u00a0]+")]
    private static partial Regex ControlAndSpaceRegex();
}

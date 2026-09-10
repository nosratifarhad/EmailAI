using System.Net;
using System.Text.RegularExpressions;
using EmailAI.Domain.Mail;

namespace EmailAI.Application.AI;

/// <summary>
/// Converts an <see cref="EmailMessage"/> into the plain text that is safe to send to
/// the AI model. Email HTML is untrusted and may be malformed, so this is deliberately
/// a tolerant "good enough" extractor, not an HTML parser: block tags become line
/// breaks, script/style blocks and comments are dropped, entities are decoded.
/// </summary>
public static partial class MailTextExtractor
{
    /// <summary>
    /// Returns the message body as plain text: prefers the server-provided TextBody,
    /// falls back to stripping the HTML body, and finally returns an empty string.
    /// </summary>
    public static string ToPlainText(EmailMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.BodyText))
        {
            return message.BodyText;
        }

        return StripHtml(message.BodyHtml);
    }

    /// <summary>Removes HTML tags while preserving readable line structure.</summary>
    public static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var text = BlockContentRegex().Replace(html, string.Empty);          // <script>...</script>
        text = StyleContentRegex().Replace(text, string.Empty);              // <style>...</style>
        text = CommentsRegex().Replace(text, string.Empty);                  // <!-- ... -->
        text = BreakTagRegex().Replace(text, "\n");                          // <br>, <br/>
        text = ClosingBlockTagRegex().Replace(text, "\n");                   // </p>, </div>, </li>, ...
        text = AnyTagRegex().Replace(text, string.Empty);                    // remaining tags
        text = WebUtility.HtmlDecode(text);
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        text = WhitespaceBeforeNewlineRegex().Replace(text, "\n");           // trailing spaces per line
        text = BlankLineRunRegex().Replace(text, "\n\n");                    // collapse 3+ blank lines
        return text.Trim();
    }

    // Separate expressions (rather than one giant regex) for readability and safety.

    [GeneratedRegex(@"<script\b[^>]*>.*?</script\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BlockContentRegex();

    [GeneratedRegex(@"<style\b[^>]*>.*?</style\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleContentRegex();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentsRegex();

    [GeneratedRegex(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTagRegex();

    [GeneratedRegex(@"</(?:p|div|li|tr|ul|ol|table|blockquote|h[1-6]|section|article|header|footer|pre)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex ClosingBlockTagRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTagRegex();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex WhitespaceBeforeNewlineRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLineRunRegex();
}

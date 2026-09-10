using EmailAI.Application.Mail;

namespace EmailAI.Tests;

/// <summary>
/// Email HTML is UNTRUSTED. The sanitizer is the second layer behind the sandboxed iframe:
/// it must remove everything executable/embeddable while keeping the formatting a reader
/// expects, and it must never throw or drop a whole body.
/// </summary>
public sealed class MailHtmlSanitizerTests
{
    [Theory]
    [InlineData("<p>Hi</p><script>alert('xss')</script><p>Bye</p>")]
    [InlineData("<SCRIPT type=\"text/javascript\">alert('xss')</SCRIPT>")]
    [InlineData("<script src=\"https://evil.example/x.js\"></script>")]
    [InlineData("<scr<script>ipt>alert('xss')</scr<script>ipt>")]
    public void Sanitize_RemovesScripts(string html)
    {
        var sanitized = MailHtmlSanitizer.Sanitize(html);

        Assert.DoesNotContain("script", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitize_SurvivesNestingTricksThatReFormATagAfterOnePass()
    {
        // One pass over "<scr<script>ipt>" leaves a REAL <script> tag behind, which is why the
        // filter is applied until the result is stable.
        var sanitized = MailHtmlSanitizer.Sanitize("<scr<script>ipt>alert('xss')</script>");

        Assert.DoesNotContain("<script", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitize_IsStable_ANormalBodyIsUnchangedByASecondPass()
    {
        const string html = "<p>Hello <strong>there</strong></p><a href=\"https://contoso.com\">link</a>";

        var once = MailHtmlSanitizer.Sanitize(html);
        var twice = MailHtmlSanitizer.Sanitize(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Sanitize_KeepsTheTextAroundARemovedScript()
    {
        var sanitized = MailHtmlSanitizer.Sanitize("<p>Hi</p><script>alert('xss')</script><p>Bye</p>");

        Assert.Contains("Hi", sanitized, StringComparison.Ordinal);
        Assert.Contains("Bye", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("alert", sanitized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>")]
    [InlineData("<object data=\"evil.swf\"></object>")]
    [InlineData("<embed src=\"evil.swf\">")]
    [InlineData("<form action=\"https://evil.example\"><input name=\"x\"></form>")]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=https://evil.example\">")]
    [InlineData("<base href=\"https://evil.example/\">")]
    public void Sanitize_RemovesEmbeddingAndNavigationElements(string html)
    {
        var sanitized = MailHtmlSanitizer.Sanitize(html);

        Assert.DoesNotContain("evil.example", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<object", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<embed", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<form", sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<img src=\"x.png\" onerror=\"alert('xss')\">")]
    [InlineData("<body onload=\"steal()\">")]
    [InlineData("<div ONCLICK='steal()'>click</div>")]
    public void Sanitize_RemovesInlineEventHandlers(string html)
    {
        var sanitized = MailHtmlSanitizer.Sanitize(html);

        Assert.DoesNotContain("onerror", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onload", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("steal()", sanitized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<a href=\"javascript:alert('xss')\">click</a>")]
    [InlineData("<a href=\"java\tscript:alert('xss')\">click</a>")]
    [InlineData("<a href=\"&#106;avascript:alert('xss')\">click</a>")]
    [InlineData("<img src=\"vbscript:msgbox(1)\">")]
    [InlineData("<img src=\"data:text/html;base64,PHNjcmlwdD4=\">")]
    public void Sanitize_RefusesDangerousUrlSchemes(string html)
    {
        var sanitized = MailHtmlSanitizer.Sanitize(html);

        Assert.DoesNotContain("javascript", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbscript", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:text/html", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitize_KeepsFormattingImagesAndLinks()
    {
        const string html =
            "<h1>Title</h1><p>Hello <strong>world</strong> <em>again</em></p>" +
            "<ul><li>one</li></ul><blockquote>quote</blockquote>" +
            "<img src=\"https://cdn.example/logo.png\" alt=\"logo\">" +
            "<a href=\"https://contoso.com/report\">report</a>" +
            "<span style=\"color:#f00\">styled</span>";

        var sanitized = MailHtmlSanitizer.Sanitize(html);

        Assert.Contains("<h1>Title</h1>", sanitized, StringComparison.Ordinal);
        Assert.Contains("<strong>world</strong>", sanitized, StringComparison.Ordinal);
        Assert.Contains("<em>again</em>", sanitized, StringComparison.Ordinal);
        Assert.Contains("<li>one</li>", sanitized, StringComparison.Ordinal);
        Assert.Contains("<blockquote>quote</blockquote>", sanitized, StringComparison.Ordinal);
        Assert.Contains("https://cdn.example/logo.png", sanitized, StringComparison.Ordinal);
        Assert.Contains("style=\"color:#f00\"", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_KeepsInlineDataImages_ButNotDataDocuments()
    {
        const string inlineImage = "<img src=\"data:image/png;base64,iVBORw0KGgo=\">";
        const string dataDocument = "<img src=\"data:text/html;base64,PHNjcmlwdD4=\">";

        Assert.Contains("data:image/png", MailHtmlSanitizer.Sanitize(inlineImage), StringComparison.Ordinal);
        Assert.DoesNotContain("base64", MailHtmlSanitizer.Sanitize(dataDocument), StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_HardensLinks_SoTheyCannotNavigateTheMessageFrame()
    {
        const string html = "<a href=\"https://contoso.com\">open</a><a href=\"/relative\">relative</a>";

        var sanitized = MailHtmlSanitizer.Sanitize(html);

        Assert.Contains("target=\"_blank\"", sanitized, StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener noreferrer\"", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_RemovesComments()
    {
        var sanitized = MailHtmlSanitizer.Sanitize("<p>Hi</p><!-- <script>alert(1)</script> -->");

        Assert.DoesNotContain("<!--", sanitized, StringComparison.Ordinal);
        Assert.Contains("Hi", sanitized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_BlankInput_ReturnsEmpty(string? html)
        => Assert.Equal(string.Empty, MailHtmlSanitizer.Sanitize(html));

    [Fact]
    public void ToSandboxDocument_WrapsTheBodyInARestrictiveCspDocument()
    {
        var document = MailHtmlSanitizer.ToSandboxDocument("<p>Hello</p><script>alert(1)</script>");

        Assert.StartsWith("<!DOCTYPE html>", document, StringComparison.Ordinal);
        Assert.Contains("charset=\"utf-8\"", document, StringComparison.Ordinal);
        Assert.Contains("Content-Security-Policy", document, StringComparison.Ordinal);
        Assert.Contains("script-src 'none'", document, StringComparison.Ordinal);
        Assert.Contains("frame-src 'none'", document, StringComparison.Ordinal);
        Assert.Contains("<body><p>Hello</p></body>", document, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", document, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToSandboxDocument_WithoutABody_ReturnsEmpty(string? html)
        => Assert.Equal(string.Empty, MailHtmlSanitizer.ToSandboxDocument(html));

    [Fact]
    public void ToSandboxDocument_ContainsTheDeclaredPolicyVerbatim()
    {
        var document = MailHtmlSanitizer.ToSandboxDocument("<p>Hello</p>");

        Assert.Contains(MailHtmlSanitizer.SandboxContentSecurityPolicy, document, StringComparison.Ordinal);
    }
}

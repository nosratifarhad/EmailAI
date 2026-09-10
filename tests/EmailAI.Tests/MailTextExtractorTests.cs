using EmailAI.Application.AI;
using EmailAI.Domain.Mail;

namespace EmailAI.Tests;

public class MailTextExtractorTests
{
    [Fact]
    public void ToPlainText_PrefersTheTextBody_OverHtml()
    {
        var message = new EmailMessage
        {
            Id = "1",
            BodyText = "Plain version",
            BodyHtml = "<p><b>Html version</b></p>",
        };

        Assert.Equal("Plain version", MailTextExtractor.ToPlainText(message));
    }

    [Fact]
    public void StripHtml_RemovesTagsScriptsAndStyles_AndDecodesEntities()
    {
        const string html =
            "<html><head><style>p{color:red}</style></head><body>" +
            "<p>Hello &amp; welcome</p>" +
            "<div>Line one<br/>Line two</div>" +
            "<ul><li>First</li><li>Second</li></ul>" +
            "<script>alert('xss')</script>" +
            "<!-- comment -->" +
            "</body></html>";

        var text = MailTextExtractor.StripHtml(html);

        Assert.DoesNotContain("<", text);
        Assert.DoesNotContain("script", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("color:red", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hello & welcome", text);
        Assert.Contains("Line one", text);
        Assert.Contains("Line two", text);
        Assert.Contains("First", text);
        Assert.Contains("Second", text);
    }

    [Fact]
    public void StripHtml_NullOrEmpty_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, MailTextExtractor.StripHtml(null));
        Assert.Equal(string.Empty, MailTextExtractor.StripHtml(""));
        Assert.Equal(string.Empty, MailTextExtractor.StripHtml("   "));
    }
}

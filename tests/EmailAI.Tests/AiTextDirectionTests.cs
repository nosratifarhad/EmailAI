using EmailAI.Application.AI;

namespace EmailAI.Tests;

/// <summary>
/// Writing direction for AI output and the reply composer. The explicit language choice wins;
/// in Auto mode the direction follows the real text, so Persian (RTL) is laid out right while
/// English stays left - without the UI hardcoding "Persian means right".
/// </summary>
public sealed class AiTextDirectionTests
{
    [Fact]
    public void Persian_IsRightToLeft_AndEnglish_IsLeftToRight()
    {
        Assert.Equal(AiTextDirection.Rtl, AiTextDirectionResolver.Resolve(AiLanguage.Persian, "hello"));
        Assert.Equal(AiTextDirection.Ltr, AiTextDirectionResolver.Resolve(AiLanguage.English, "سلام"));
    }

    [Theory]
    [InlineData("سلام، این یک پاسخ است.")]
    [InlineData("مرحبا بكم")]          // Arabic shares the RTL script range
    [InlineData("שלום")]               // Hebrew too
    public void Auto_DetectsRightToLeftScripts(string text)
        => Assert.Equal(AiTextDirection.Rtl, AiTextDirectionResolver.Resolve(AiLanguage.Auto, text));

    [Theory]
    [InlineData("Here is the summary of the thread.")]
    [InlineData("Привет")]             // Cyrillic is left-to-right
    [InlineData("Γειά σου")]           // Greek too
    public void Auto_DetectsLeftToRightScripts(string text)
        => Assert.Equal(AiTextDirection.Ltr, AiTextDirectionResolver.Resolve(AiLanguage.Auto, text));

    [Fact]
    public void Auto_WithPersianAndLatin_Mixed_UsesTheDominantScript()
    {
        var mostlyPersian = "این یک متن فارسی است که با یک word مخلوط شده است";
        var mostlyEnglish = "This reply mentions سلام once";

        Assert.Equal(AiTextDirection.Rtl, AiTextDirectionResolver.Resolve(AiLanguage.Auto, mostlyPersian));
        Assert.Equal(AiTextDirection.Ltr, AiTextDirectionResolver.Resolve(AiLanguage.Auto, mostlyEnglish));
    }

    [Fact]
    public void Auto_IgnoresDigitsAndPunctuation_SoAnEnglishNumberCannotOutvotePersian()
        => Assert.Equal(
            AiTextDirection.Rtl,
            AiTextDirectionResolver.Resolve(AiLanguage.Auto, "کد 12345 - 2026/03/01 (100%)"));

    [Fact]
    public void BlankOrMissingText_FallsBackToLeftToRight()
    {
        Assert.Equal(AiTextDirection.Ltr, AiTextDirectionResolver.Resolve(AiLanguage.Auto));
        Assert.Equal(AiTextDirection.Ltr, AiTextDirectionResolver.Resolve(AiLanguage.Auto, null, "", "   "));
    }

    [Fact]
    public void Auto_MergesEverySuppliedText_SoAnEmptyResultFallsBackToTheEmail()
    {
        // The UI passes the AI result first and the source email as a fallback: an empty
        // result leaves the direction to the email.
        Assert.Equal(AiTextDirection.Rtl, AiTextDirectionResolver.Resolve(AiLanguage.Auto, null, "متن ایمیل اصلی"));
        Assert.Equal(AiTextDirection.Rtl, AiTextDirectionResolver.Resolve(AiLanguage.Auto, "   ", "متن ایمیل اصلی"));

        // Both texts contribute (the email has more Persian letters than the English result).
        Assert.Equal(
            AiTextDirection.Rtl,
            AiTextDirectionResolver.Resolve(AiLanguage.Auto, "Draft reply", "متن ایمیل اصلی"));

        // An English-only result stays left-to-right.
        Assert.Equal(AiTextDirection.Ltr, AiTextDirectionResolver.Resolve(AiLanguage.Auto, "Draft reply"));
    }
}

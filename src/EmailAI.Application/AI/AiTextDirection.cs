namespace EmailAI.Application.AI;

/// <summary>
/// Writing direction of text the UI has to lay out (Persian/Arabic/Hebrew are right-to-left).
/// Kept as an explicit value so the UI never hardcodes "Persian means right".
/// </summary>
public enum AiTextDirection
{
    /// <summary>Left-to-right (Latin and most other scripts).</summary>
    Ltr = 0,

    /// <summary>Right-to-left (Persian/Farsi, Arabic, Hebrew, ...).</summary>
    Rtl = 1,
}

/// <summary>
/// Decides the writing direction the UI should use for an AI result.
///
/// The chosen response language is authoritative when it is explicit (Persian =&gt; RTL,
/// English =&gt; LTR); for <see cref="AiLanguage.Auto"/> the direction follows the actual
/// text (the email being answered or the produced answer), which keeps the layout correct
/// for languages added later without touching the components.
/// </summary>
public static class AiTextDirectionResolver
{
    /// <summary>
    /// Resolves the direction for <paramref name="language"/>, using
    /// <paramref name="texts"/> only when the language is <see cref="AiLanguage.Auto"/>.
    /// A blank/indeterminate text resolves to LTR (the neutral default).
    /// </summary>
    public static AiTextDirection Resolve(AiLanguage language, params string?[] texts)
        => language switch
        {
            AiLanguage.Persian => AiTextDirection.Rtl,
            AiLanguage.English => AiTextDirection.Ltr,
            _ => Detect(texts),
        };

    /// <summary>
    /// Direction of mixed text: RTL when the right-to-left letters dominate (or are the only
    /// letters), LTR otherwise.
    /// </summary>
    public static AiTextDirection Detect(IEnumerable<string?>? texts)
    {
        var rtl = 0;
        var ltr = 0;

        foreach (var text in texts ?? [])
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            foreach (var rune in text.EnumerateRunes())
            {
                var value = rune.Value;
                if (IsRightToLeft(value))
                {
                    rtl++;
                }
                else if (IsLeftToRightLetter(value))
                {
                    ltr++;
                }
            }
        }

        if (rtl == 0)
        {
            return AiTextDirection.Ltr;
        }

        return ltr == 0 || rtl > ltr ? AiTextDirection.Rtl : AiTextDirection.Ltr;
    }

    /// <summary>
    /// RTL script ranges (Hebrew, Arabic, Arabic Supplement/Extended, Syriac, Thaana,
    /// NKo, Samaritan, Mandaic, and the Arabic presentation forms).
    /// </summary>
    private static bool IsRightToLeft(int value) => value switch
    {
        >= 0x0590 and <= 0x05FF => true, // Hebrew
        >= 0x0600 and <= 0x06FF => true, // Arabic
        >= 0x0700 and <= 0x074F => true, // Syriac
        >= 0x0750 and <= 0x077F => true, // Arabic Supplement
        >= 0x0780 and <= 0x07BF => true, // Thaana / NKo
        >= 0x07C0 and <= 0x08FF => true, // NKo, Samaritan, Mandaic, Arabic Extended-A
        >= 0xFB1D and <= 0xFDFF => true, // Hebrew/Arabic presentation forms
        >= 0xFE70 and <= 0xFEFF => true, // Arabic presentation forms-B
        _ => false,
    };

    /// <summary>
    /// Left-to-right letters (Latin, Greek, Cyrillic). Digits, punctuation and whitespace
    /// are ignored so an English number never outvotes Persian text.
    /// </summary>
    private static bool IsLeftToRightLetter(int value) => value switch
    {
        >= 0x0041 and <= 0x005A => true,
        >= 0x0061 and <= 0x007A => true,
        >= 0x00C0 and <= 0x024F => true, // Latin-1 supplement / Latin extended
        >= 0x0370 and <= 0x03FF => true, // Greek
        >= 0x0400 and <= 0x04FF => true, // Cyrillic
        _ => false,
    };
}

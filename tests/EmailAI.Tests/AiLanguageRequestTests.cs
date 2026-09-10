using System.Text.Json;
using EmailAI.Api.Web;
using EmailAI.Application.AI;

namespace EmailAI.Tests;

/// <summary>
/// The requested response language is part of the public API contract: the NAME
/// (<c>{"language":"Persian"}</c>) is what the documentation and the Blazor UI send, the numeric
/// enum value is kept for compatibility, and anything unusable is rejected with a message the
/// user can act on instead of the framework's empty 400.
/// </summary>
public class AiLanguageRequestTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void MissingRequestOrOmittedLanguage_IsAuto()
    {
        Assert.True(AiLanguageRequest.TryResolve(null, out var fromNull, out var nullError));
        Assert.Equal(AiLanguage.Auto, fromNull);
        Assert.Null(nullError);

        Assert.True(AiLanguageRequest.TryResolve(Parse("{}"), out var fromEmpty, out _));
        Assert.Equal(AiLanguage.Auto, fromEmpty);

        Assert.True(AiLanguageRequest.TryResolve(Parse("""{"language":null}"""), out _, out _));
        Assert.True(AiLanguageRequest.TryResolve(Parse("""{"language":"   "}"""), out _, out _));
    }

    [Theory]
    [InlineData("English", AiLanguage.English)]
    [InlineData("english", AiLanguage.English)]
    [InlineData("PERSIAN", AiLanguage.Persian)]
    [InlineData("  Persian  ", AiLanguage.Persian)]
    [InlineData("Auto", AiLanguage.Auto)]
    public void DocumentedLanguageNames_AreAccepted(string name, AiLanguage expected)
    {
        Assert.True(AiLanguageRequest.TryResolve(
            Parse($$"""{"language":"{{name}}"}"""), out var language, out var error));

        Assert.Equal(expected, language);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("0", AiLanguage.Auto)]
    [InlineData("1", AiLanguage.English)]
    [InlineData("2", AiLanguage.Persian)]
    public void NumericValues_KeepWorking(string value, AiLanguage expected)
    {
        Assert.True(AiLanguageRequest.TryResolve(
            Parse($$"""{"language":{{value}}}"""), out var language, out _));

        Assert.Equal(expected, language);
    }

    [Fact]
    public void NumericString_IsStillUnderstood()
    {
        Assert.True(AiLanguageRequest.TryResolve(Parse("""{"language":"2"}"""), out var language, out _));
        Assert.Equal(AiLanguage.Persian, language);
    }

    [Theory]
    [InlineData("\"Klingon\"", "Klingon")]
    [InlineData("\"English-ish\"", "English-ish")]
    [InlineData("7", "7")]
    [InlineData("1.5", "1.5")]
    public void UnsupportedValues_AreRejected_WithAnActionableMessage(string token, string echoed)
    {
        Assert.False(AiLanguageRequest.TryResolve(
            Parse($$"""{"language":{{token}}}"""), out var language, out var error));

        Assert.Equal(AiLanguage.Auto, language);
        Assert.NotNull(error);
        Assert.Contains(echoed, error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("English", error, StringComparison.Ordinal);
        Assert.Contains("Persian", error, StringComparison.Ordinal);
    }

    [Fact]
    public void NonScalarToken_IsRejected_WithAMessage()
    {
        Assert.False(AiLanguageRequest.TryResolve(
            Parse("""{"language":{"name":"English"}}"""), out _, out var error));

        Assert.Equal(AiLanguageRequest.UnsupportedTokenMessage, error);
    }

    [Fact]
    public void For_WritesTheLanguageName_NotTheNumber()
    {
        var json = JsonSerializer.Serialize(AiOperationRequest.For(AiLanguage.Persian), WebJson);

        Assert.Contains("\"Persian\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain(":2", json, StringComparison.Ordinal);
    }

    private static AiOperationRequest Parse(string json)
        => JsonSerializer.Deserialize<AiOperationRequest>(json, WebJson)!;
}

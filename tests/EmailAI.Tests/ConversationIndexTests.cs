using EmailAI.Api.Web;
using EmailAI.Domain.Mail;

namespace EmailAI.Tests;

/// <summary>
/// The conversation state the message surfaces share. The component only supplies the asynchronous
/// part (when to ask Exchange), so "which conversations are known", "which rows still need an answer"
/// and "an unknown conversation is never a thread" are pinned here without a browser.
/// </summary>
public sealed class ConversationIndexTests
{
    private static MessageSummary Row(string id, string? conversationId)
        => new() { Id = id, ConversationId = conversationId };

    private static ConversationSummary Thread(string conversationId, int messageCount)
        => new(conversationId, "Topic", messageCount, []);

    [Fact]
    public void AnUnknownConversation_KeepsItsIdentity_ButIsNeverClaimedToBeAThread()
    {
        var index = new ConversationIndex();

        var state = index.Get("conv-1", "Topic");

        Assert.Equal("conv-1", state.ConversationId);
        Assert.False(state.IsKnown);
        Assert.False(state.IsThread);
        Assert.Null(index.TryGet("conv-1"));
    }

    [Fact]
    public void AConversationWithNoIdentity_IsNeverAThread_HoweverOftenItIsAskedAbout()
    {
        var index = new ConversationIndex();

        Assert.False(index.Get(null, "Subject").IsThread);
        Assert.False(index.Get("   ", "Subject").IsThread);
    }

    [Fact]
    public void ApplyingWhatExchangeReported_MakesTheThreadKnowable()
    {
        var index = new ConversationIndex();

        index.Apply([new ConversationSummary(
            "conv-1", "Topic", 4, [new EmailAddress("Sara", "sara@contoso.com")])]);

        var state = index.Get("conv-1", null);

        Assert.True(state.IsThread);
        Assert.Equal(4, state.MessageCount);
        Assert.Single(state.Participants);
        Assert.Equal(1, index.KnownCount);
        Assert.Equal(1, index.ThreadCount);
    }

    [Fact]
    public void ASummaryWithoutAnIdentity_IsIgnored()
    {
        var index = new ConversationIndex();

        index.Apply([ConversationSummary.NotLoaded(null, "Topic"), new ConversationSummary(" ", "Topic", 5, [])]);

        Assert.Equal(0, index.KnownCount);
    }

    [Fact]
    public void ARepeatedAnswer_ReplacesTheEarlierOne()
    {
        var index = new ConversationIndex();
        index.Apply([Thread("conv-1", 1)]);

        index.Apply([Thread("conv-1", 3)]);

        Assert.Equal(3, index.Get("conv-1", null).MessageCount);
        Assert.Equal(1, index.KnownCount);
    }

    [Fact]
    public void MissingIds_AskOnlyForWhatIsNotKnown_InRowOrder_WithoutDuplicates()
    {
        var index = new ConversationIndex();
        index.Apply([Thread("conv-b", 2)]);

        var missing = index.MissingIds(
        [
            Row("1", "conv-a"),
            Row("2", "conv-b"),   // already known
            Row("3", "conv-a"),   // duplicate of the first row
            Row("4", null),       // nothing to ask about
            Row("5", "   "),
            Row("6", "conv-c"),
        ]);

        Assert.Equal(["conv-a", "conv-c"], missing);
    }

    [Fact]
    public void MissingIds_AreBounded_ToWhatOneLookupMayAskFor()
    {
        var index = new ConversationIndex();
        var rows = Enumerable.Range(0, ConversationSummary.MaxLookupBatch + 10)
            .Select(index => Row(index.ToString(), $"conv-{index}"))
            .ToArray();

        var missing = index.MissingIds(rows);

        Assert.Equal(ConversationSummary.MaxLookupBatch, missing.Count);
        Assert.Equal("conv-0", missing[0]);
    }

    [Fact]
    public void MissingIds_OfAnEmptyPage_AskForNothing()
    {
        var index = new ConversationIndex();

        Assert.Empty(index.MissingIds([]));
    }

    [Fact]
    public void Reset_ForgetsEveryConversation_WhenTheMailboxChanged()
    {
        var index = new ConversationIndex();
        index.Apply([Thread("conv-1", 4)]);

        index.Reset();

        Assert.Equal(0, index.KnownCount);
        Assert.Equal(0, index.ThreadCount);
        Assert.Null(index.TryGet("conv-1"));
        Assert.False(index.Get("conv-1", null).IsKnown);
    }
}

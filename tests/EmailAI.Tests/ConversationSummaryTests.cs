using EmailAI.Domain.Mail;

namespace EmailAI.Tests;

/// <summary>
/// The one definition of a thread. Every thread-aware surface - the conversation badge in the list,
/// the reading pane's conversation view and "Summarize thread" - reads it, so these tests pin what it
/// means: a conversation Exchange reports as holding more than one message (an original plus at least
/// one reply). Never a subject, never the message the user happened to open, and never a guess while
/// Exchange has not answered.
/// </summary>
public sealed class ConversationSummaryTests
{
    private static MessageSummary Sender(string id, string? name, string? address)
        => new()
        {
            Id = id,
            From = name is null && address is null ? null : new EmailAddress(name, address),
        };

    [Fact]
    public void AnUnreadConversation_IsKnownToBeUnknown_AndIsNeverAThread()
    {
        var state = ConversationSummary.NotLoaded("conv-1", "Project deadline update");

        Assert.Equal("conv-1", state.ConversationId);
        Assert.True(state.HasConversation);
        Assert.False(state.IsKnown);
        Assert.False(state.IsThread);
        Assert.Null(state.MessageCount);
        Assert.Empty(state.Participants);
    }

    [Fact]
    public void AConversationOfOne_IsNotAThread()
    {
        var state = new ConversationSummary("conv-1", "Subject", 1, []);

        Assert.True(state.IsKnown);
        Assert.False(state.IsThread);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    public void AConversationWithMoreThanOneMessage_IsAThread(int messageCount)
    {
        var state = new ConversationSummary("conv-1", "Subject", messageCount, []);

        Assert.True(state.IsThread);
    }

    [Fact]
    public void AMessageExchangeKeepsOutsideAConversation_IsNeverAThread()
    {
        var state = ConversationSummary.NotLoaded(null, null);

        Assert.False(state.HasConversation);
        Assert.False(state.IsThread);
    }

    [Fact]
    public void TheSameIdentityMakesEveryMessageOfTheChainAThread_EvenWhenTheSubjectChanged()
    {
        // Exchange groups by conversation id, so a reply with an edited subject (or a different RE:/FW:
        // prefix) stays in the same conversation - and the original message is a thread too.
        var original = new ConversationSummary("conv-9", "Project deadline", 3, []);
        var renamedReply = new ConversationSummary("conv-9", "Re: something else entirely", 3, []);

        Assert.True(original.IsThread);
        Assert.True(renamedReply.IsThread);
        Assert.Equal(original.MessageCount, renamedReply.MessageCount);
    }

    [Fact]
    public void TwoUnrelatedMessagesThatShareASubject_StayTwoConversations()
    {
        var mine = new ConversationSummary("conv-a", "Status update", 1, []);
        var theirs = new ConversationSummary("conv-b", "Status update", 1, []);

        Assert.False(mine.IsThread);
        Assert.False(theirs.IsThread);
        Assert.NotEqual(mine.ConversationId, theirs.ConversationId);
    }

    [Fact]
    public void Participants_AreTheDistinctSendersInOrder_AndSurviveADraftWithNoSender()
    {
        var participants = ConversationSummary.SendersOf(
        [
            Sender("1", "Farhad", "farhad@contoso.com"),
            Sender("2", "Alice", "alice@contoso.com"),
            Sender("3", "Farhad N.", "farhad@contoso.com"),      // same address, different display name
            Sender("4", "FARHAD", "FARHAD@contoso.com"),         // same address, different casing
            Sender("5", null, null),                             // no sender at all (a draft)
            Sender("6", "   ", "   "),                           // Exchange's empty address object
            Sender("7", "Draft name only", null),                // no address: the name is the identity
        ]);

        Assert.Equal(3, participants.Count);
        Assert.Equal("farhad@contoso.com", participants[0].Address);
        Assert.Equal("alice@contoso.com", participants[1].Address);
        Assert.Equal("Draft name only", participants[2].Name);
    }

    [Fact]
    public void Participants_AreBounded_SoACrowdedConversationStaysReadable()
    {
        var messages = Enumerable.Range(1, 20)
            .Select(index => Sender(index.ToString(), $"Person {index}", $"person{index}@contoso.com"))
            .ToArray();

        var participants = ConversationSummary.SendersOf(messages);

        Assert.Equal(ConversationSummary.MaxParticipants, participants.Count);
        Assert.Equal("person1@contoso.com", participants[0].Address);
    }

    [Fact]
    public void ALoadedThread_ProjectsTheConversationTheOtherSurfacesRead()
    {
        var thread = new MessageThread
        {
            ConversationId = "conv-7",
            Topic = "Budget review",
            TotalCount = 3,
            Messages =
            [
                new ThreadMessage { Id = "1", From = new EmailAddress("Sara", "sara@contoso.com") },
                new ThreadMessage { Id = "2", From = new EmailAddress("Ali", "ali@contoso.com") },
                new ThreadMessage { Id = "3", From = new EmailAddress("Sara", "sara@contoso.com") },
            ],
        };

        var conversation = thread.Conversation;

        Assert.Equal("conv-7", conversation.ConversationId);
        Assert.Equal(3, conversation.MessageCount);
        Assert.True(conversation.IsThread);
        Assert.Equal(["sara@contoso.com", "ali@contoso.com"], conversation.Participants.Select(p => p.Address));
    }

    [Fact]
    public void AThreadOfOneLoadedMessage_IsNotAThread()
    {
        var thread = new MessageThread
        {
            ConversationId = "conv-1",
            Topic = "One off",
            TotalCount = 1,
            Messages = [new ThreadMessage { Id = "1", From = new EmailAddress("Sara", "sara@contoso.com") }],
        };

        Assert.True(thread.Conversation.IsKnown);
        Assert.False(thread.Conversation.IsThread);
    }
}

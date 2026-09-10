using System.Collections.Concurrent;
using System.Net;
using System.Text;
using EmailAI.Application.AI;
using EmailAI.Application.Exceptions;
using EmailAI.Application.Exchange;
using EmailAI.Application.Settings;
using EmailAI.Domain.AI;
using EmailAI.Domain.Exchange;
using EmailAI.Domain.Mail;
using EmailAI.Domain.Settings;
using Microsoft.Extensions.Options;

namespace EmailAI.Tests;

/// <summary>IOptionsMonitor that always returns the same pre-built value.</summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>Immutable snapshot of what was sent, safe to assert on after disposal.</summary>
internal sealed record CapturedRequest(
    HttpMethod Method,
    Uri Url,
    string? Authorization,
    string? Body);

/// <summary>
/// In-memory <see cref="IAiCredentialProvider"/> returning a fixed key (or null).
/// Kept deliberately simple: coordinator precedence itself is covered by the
/// AiCredentialCoordinatorTests / settings endpoint tests.
/// </summary>
internal sealed class StaticAiCredentialProvider(string? apiKey) : IAiCredentialProvider
{
    public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(apiKey);
}

/// <summary>
/// In-memory <see cref="IAiConfigurationProvider"/> returning a fixed effective
/// <see cref="AiOptions"/> snapshot. Used by the client tests to stand in for the
/// coordinator's server-configuration + per-user-overrides merge.
/// </summary>
internal sealed class StaticAiConfigurationProvider(AiOptions options) : IAiConfigurationProvider
{
    public Task<AiOptions> GetEffectiveOptionsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(options);
}

/// <summary>
/// In-memory <see cref="IAiCredentialStore"/> for unit tests. Mirrors the
/// WindowsCredentialStore contract (missing key reads as null, delete of a missing
/// key is a no-op) so the coordinator and endpoints can be tested deterministically.
/// </summary>
internal sealed class InMemoryAiCredentialStore : IAiCredentialStore
{
    private string? _apiKey;

    public bool DeleteCalled { get; private set; }

    public Task<bool> HasApiKeyAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(!string.IsNullOrWhiteSpace(_apiKey));

    public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_apiKey);

    public Task SaveApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("The AI API key must not be empty.", nameof(apiKey));
        }

        _apiKey = apiKey;
        return Task.CompletedTask;
    }

    public Task DeleteApiKeyAsync(CancellationToken cancellationToken = default)
    {
        _apiKey = null;
        DeleteCalled = true;
        return Task.CompletedTask;
    }
}

/// <summary>
/// In-memory <see cref="IAiUserSettingsStore"/> for unit/host tests so the real
/// per-user file (which lives under %APPDATA%\EmailAI on this machine) is never
/// touched by a test run.
/// </summary>
internal sealed class InMemoryAiUserSettingsStore : IAiUserSettingsStore
{
    private AiUserSettings _settings = new(null, null);

    /// <summary>The currently stored overrides (readable for assertions).</summary>
    public AiUserSettings Stored => _settings;

    public Task<AiUserSettings> GetAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_settings);

    public Task SaveAsync(AiUserSettings settings, CancellationToken cancellationToken = default)
    {
        _settings = settings;
        return Task.CompletedTask;
    }
}

/// <summary>
/// HttpMessageHandler for tests: records every request (reading the body up front so
/// assertions work after HttpClient disposes the request) and delegates the response
/// to a test-provided function.
/// </summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;
    private readonly ConcurrentQueue<CapturedRequest> _requests = new();

    public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : this((request, _) => Task.FromResult(responder(request)))
    {
    }

    public StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    public IReadOnlyList<CapturedRequest> Requests => _requests.ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string? body = null;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        _requests.Enqueue(new CapturedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            body));

        return await _responder(request, cancellationToken);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
}

/// <summary>
/// In-memory <see cref="IExchangeConfigurationProvider"/> returning a fixed effective
/// snapshot. Used where the real per-user settings document (%APPDATA%\EmailAI) and the
/// Windows Credential Manager must never be touched by a test.
/// </summary>
internal sealed class StaticExchangeConfigurationProvider(ExchangeOptions options)
    : IExchangeConfigurationProvider
{
    public Task<ExchangeOptions> GetEffectiveOptionsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(options);
}

/// <summary>
/// In-memory <see cref="ISecretStore"/> mirroring the Windows Credential Manager contract:
/// each target is independent, a missing target reads as null and deleting a missing target
/// is a no-op. Optionally fails every operation to exercise the failure paths.
/// </summary>
internal sealed class InMemorySecretStore(bool fail = false) : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>The currently stored targets (names only - used to prove isolation).</summary>
    public IReadOnlyCollection<string> Targets => _values.Keys;

    public Task<bool> ExistsAsync(string target, CancellationToken cancellationToken = default)
        => Task.FromResult(Read(target) is not null);

    public Task<string?> ReadAsync(string target, CancellationToken cancellationToken = default)
        => Task.FromResult(Read(target));

    public Task WriteAsync(string target, string value, CancellationToken cancellationToken = default)
    {
        ThrowIfFailed();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The secret value must not be empty.", nameof(value));
        }

        _values[target] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string target, CancellationToken cancellationToken = default)
    {
        ThrowIfFailed();
        _values.Remove(target);
        return Task.CompletedTask;
    }

    private string? Read(string target)
    {
        ThrowIfFailed();
        return _values.TryGetValue(target, out var value) ? value : null;
    }

    private void ThrowIfFailed()
    {
        if (fail)
        {
            throw new SecretStoreException("The credential store is unavailable (test).");
        }
    }
}

/// <summary>
/// In-memory <see cref="IUserSettingsStore"/> so unit tests can assert exactly which
/// document would be written (and prove no secret is ever part of it).
/// </summary>
internal sealed class InMemoryUserSettingsStore : IUserSettingsStore
{
    private UserSettings _settings = UserSettings.Empty;

    /// <summary>The currently stored document (readable for assertions).</summary>
    public UserSettings Stored => _settings;

    public Task<UserSettings> GetAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_settings);

    public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        _settings = settings;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _settings = UserSettings.Empty;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Deterministic <see cref="IExchangeConnectionTester"/> that records the snapshot it was
/// asked to probe and returns a canned, secret-free result.
/// </summary>
internal sealed class RecordingExchangeConnectionTester(ExchangeHealthStatus result)
    : IExchangeConnectionTester
{
    /// <summary>The last snapshot passed to <see cref="TestAsync"/> (null until called).</summary>
    public ExchangeOptions? LastOptions { get; private set; }

    public int Calls { get; private set; }

    public Task<ExchangeHealthStatus> TestAsync(ExchangeOptions options, CancellationToken cancellationToken = default)
    {
        LastOptions = options;
        Calls++;
        return Task.FromResult(result);
    }
}

/// <summary>
/// In-memory <see cref="IExchangeMailService"/> so host/unit tests can exercise the mail
/// list, the new-mail notification feed and the initial inbox load without Exchange (and
/// without touching credentials). Folders are arranged per key; the newest message is
/// listed first, exactly like the real EWS query.
/// </summary>
internal sealed class FakeExchangeMailService : IExchangeMailService
{
    private readonly Dictionary<string, List<MessageSummary>> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EmailMessage> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MailFolder>> _childFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ConversationSummary> _conversations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MessageThread> _threads = new(StringComparer.Ordinal);

    /// <summary>Number of list queries served (lets tests assert on polling behaviour).</summary>
    public int ListCalls { get; private set; }

    /// <summary>Number of child-folder queries served (lets tests assert on lazy discovery).</summary>
    public int FolderListCalls { get; private set; }

    /// <summary>Number of conversation queries served (lets tests assert on lazy conversation state).</summary>
    public int ConversationLookupCalls { get; private set; }

    /// <summary>Every conversation-id set requested, in call order.</summary>
    public List<IReadOnlyList<string>> RequestedConversations { get; } = [];

    /// <summary>
    /// When set, the NEXT conversation query fails with this typed Exchange error instead of answering
    /// (used for "the conversation state could not be read").
    /// </summary>
    public ExchangeMailErrorKind? FailNextConversationLookupKind { get; set; }

    /// <summary>Every parent key whose child folders were requested, in call order.</summary>
    public List<string> RequestedFolderParents { get; } = [];

    /// <summary>
    /// When set, the NEXT list query fails with this typed Exchange error instead of answering
    /// (used for folder-gone / access-denied / unavailable paths).
    /// </summary>
    public ExchangeMailErrorKind? FailNextListKind { get; set; }

    /// <summary>
    /// When set, the NEXT child-folder query fails with this typed Exchange error instead of
    /// answering (used for "folder discovery failed" / "Exchange unavailable").
    /// </summary>
    public ExchangeMailErrorKind? FailNextChildFoldersKind { get; set; }

    /// <summary>
    /// Number of UPCOMING list queries that answer with a transient Exchange failure instead of
    /// data. Lets a test prove that the UI retries a hiccup once (and still reports a real outage
    /// after that).
    /// </summary>
    public int FailNextListCalls { get; set; }

    /// <summary>Every folder key that was listed, in call order.</summary>
    public List<string> RequestedFolders { get; } = [];

    /// <summary>Replaces the content of one folder.</summary>
    public void Arrange(string folderKey, params MessageSummary[] items)
        => _folders[folderKey] = [.. items];

    /// <summary>Adds one header to the top of a folder (newest first).</summary>
    public MessageSummary Add(
        string folderKey,
        string id,
        DateTimeOffset receivedAt,
        string? subject = "New message",
        string? fromName = "Sara",
        string? fromAddress = "sara@example.com",
        string? conversationId = null,
        string? conversationTopic = null)
    {
        var summary = new MessageSummary
        {
            Id = id,
            Subject = subject,
            From = new EmailAddress(fromName, fromAddress),
            ReceivedAt = receivedAt,
            ConversationId = conversationId,
            ConversationTopic = conversationTopic ?? subject,
        };

        if (!_folders.TryGetValue(folderKey, out var list))
        {
            list = [];
            _folders[folderKey] = list;
        }

        list.Insert(0, summary);
        return summary;
    }

    /// <summary>Arranges the full message returned by <see cref="GetMessageAsync"/>.</summary>
    public void ArrangeMessage(EmailMessage message) => _messages[message.Id] = message;

    /// <summary>
    /// Arranges what Exchange reports for a conversation: how many messages it holds and who is in it.
    /// A conversation that is not arranged is unknown - exactly like one Exchange no longer holds.
    /// </summary>
    public void ArrangeConversation(
        string conversationId,
        int messageCount,
        params EmailAddress[] participants)
        => _conversations[conversationId] = new ConversationSummary(
            conversationId,
            "Fake conversation",
            messageCount,
            participants);

    /// <summary>Arranges the conversation <see cref="GetThreadAsync"/> answers for one message.</summary>
    public void ArrangeThread(MessageThread thread, params string[] itemIds)
    {
        foreach (var itemId in itemIds)
        {
            _threads[itemId] = thread;
        }
    }

    /// <summary>Builds a conversation of the given messages (oldest first), as GetThread returns them.</summary>
    public static MessageThread Thread(
        string conversationId,
        string topic,
        params MessageSummary[] chronological)
        => new()
        {
            ConversationId = conversationId,
            Topic = topic,
            TotalCount = chronological.Length,
            FocusMessageId = chronological.LastOrDefault()?.Id,
            Messages = [.. chronological.Select((message, index) => new ThreadMessage
            {
                Id = message.Id,
                Subject = message.Subject,
                From = message.From,
                To = message.To,
                ReceivedAt = message.ReceivedAt,
                IsRead = message.IsRead,
                HasAttachments = message.HasAttachments,
                Importance = message.Importance,
                ConversationId = conversationId,
                ConversationTopic = topic,
                Depth = index,
                ParentId = index == 0 ? null : chronological[index - 1].Id,
                Preview = "Preview of " + (message.Subject ?? "the message"),
            })],
        };

    /// <summary>A standalone message (or one of a conversation of one) as Exchange returns it.</summary>
    public static MessageSummary Message(
        string id,
        string subject,
        string fromName = "Sara Ahmadi",
        string fromAddress = "sara@example.com")
        => new()
        {
            Id = id,
            Subject = subject,
            From = new EmailAddress(fromName, fromAddress),
            ConversationId = null,
        };

    /// <summary>
    /// Arranges the folders nested directly below a parent key, exactly like Exchange reports them
    /// (direct children only, alphabetical). A parent with no arrangement has no children.
    /// </summary>
    public void ArrangeChildren(string parentKey, params MailFolder[] children)
        => _childFolders[parentKey] = [.. children.OrderBy(child => child.DisplayName, StringComparer.OrdinalIgnoreCase)];

    /// <summary>A custom child folder arranged for the fake mailbox.</summary>
    public static MailFolder Child(
        string id,
        string displayName,
        string parentKey = "inbox",
        bool hasChildren = false,
        string? wellKnownType = null)
        => new()
        {
            Id = id,
            DisplayName = displayName,
            ParentId = parentKey,
            HasChildren = hasChildren,
            WellKnownType = wellKnownType ?? MailFolderTypes.Custom,
        };

    /// <summary>Custom-folder keys exactly as the REST surface hands them back.</summary>
    public static string CustomKey(string uniqueId)
        => EmailAI.Infrastructure.Exchange.CustomFolderKey.FromUniqueId(uniqueId);

    public Task<MessagePage> GetMessagesAsync(
        string folderKey,
        int offset,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ListCalls++;
        RequestedFolders.Add(folderKey);

        if (FailNextListKind is { } kind)
        {
            FailNextListKind = null;
            throw new EmailAI.Application.Exceptions.ExchangeMailException(
                kind, "Injected typed Exchange failure (test double).");
        }

        if (FailNextListCalls > 0)
        {
            FailNextListCalls--;
            throw new EmailAI.Application.Exceptions.ExchangeMailException(
                EmailAI.Application.Exceptions.ExchangeMailErrorKind.Connectivity,
                "Injected transient Exchange failure (test double).");
        }

        var ordered = (_folders.TryGetValue(folderKey, out var list) ? list : [])
            .OrderByDescending(item => item.ReceivedAt)
            .ToArray();
        var page = ordered.Skip(offset).Take(pageSize).ToArray();

        return Task.FromResult(new MessagePage
        {
            Items = page,
            Offset = offset,
            PageSize = pageSize,
            TotalCount = ordered.Length,
            HasMore = offset + page.Length < ordered.Length,
        });
    }

    public Task<IReadOnlyList<MailFolder>> GetChildFoldersAsync(
        string parentKey,
        CancellationToken cancellationToken)
    {
        FolderListCalls++;
        RequestedFolderParents.Add(parentKey);

        if (FailNextChildFoldersKind is { } kind)
        {
            FailNextChildFoldersKind = null;
            throw new ExchangeMailException(kind, "Injected typed Exchange failure (test double).");
        }

        // The fake enforces the same folder-key contract as the EWS service, so a host test sees the
        // real 400 for an unsupported key instead of a silently empty folder list.
        if (!EmailAI.Infrastructure.Exchange.FolderMapping.IsSupported(parentKey))
        {
            throw new ExchangeMailException(
                ExchangeMailErrorKind.BadRequest,
                $"Unsupported folder '{parentKey}'.");
        }

        return Task.FromResult<IReadOnlyList<MailFolder>>(
            _childFolders.TryGetValue(parentKey, out var children) ? children : []);
    }

    public Task<EmailMessage> GetMessageAsync(string itemId, CancellationToken cancellationToken)
        => _messages.TryGetValue(itemId, out var message)
            ? Task.FromResult(message)
            : Task.FromException<EmailMessage>(
                new InvalidOperationException($"No fake message '{itemId}' is arranged."));
    /// <summary>
    /// The conversation arranged for a message, or the same empty conversation the fake has always
    /// answered with (which the AI endpoint tests rely on for "the conversation is empty").
    /// </summary>
    public Task<MessageThread> GetThreadAsync(string itemId, CancellationToken cancellationToken)
        => Task.FromResult(_threads.TryGetValue(itemId, out var thread)
            ? thread
            : new MessageThread
            {
                ConversationId = "fake-conversation",
                Topic = "Fake conversation",
                TotalCount = 0,
                FocusMessageId = itemId,
                Messages = [],
            });

    public Task<IReadOnlyList<ConversationSummary>> GetConversationSummariesAsync(
        IReadOnlyList<string> conversationIds,
        CancellationToken cancellationToken)
    {
        ConversationLookupCalls++;
        RequestedConversations.Add(conversationIds);

        if (FailNextConversationLookupKind is { } kind)
        {
            FailNextConversationLookupKind = null;
            throw new ExchangeMailException(kind, "Injected typed Exchange failure (test double).");
        }

        // Only arranged conversations are reported, exactly like Exchange omitting one it does not
        // hold: a test can therefore pin "unknown means no badge - and never a thread".
        return Task.FromResult<IReadOnlyList<ConversationSummary>>([.. conversationIds
            .Where(id => _conversations.ContainsKey(id))
            .Select(id => _conversations[id])]);
    }

    public Task<ReplyResult> ReplyAsync(string itemId, ReplyDraft draft, CancellationToken cancellationToken)
        => throw new NotSupportedException("FakeExchangeMailService never sends mail.");

    public Task<ExchangeHealthStatus> CheckHealthAsync(CancellationToken cancellationToken)
        => Task.FromResult(new ExchangeHealthStatus(IsHealthy: true, LatencyMs: 1, Error: null));
}

internal static class TestOptions
{
    /// <summary>Fully configured options. Pass null to simulate a missing value.</summary>
    public static AiOptions Ai(
        string? baseUrl = "https://provider.test/v1",
        string? apiKey = "secret-key",
        string? model = "gpt-5",
        int timeoutSeconds = 120)
        => new()
        {
            BaseUrl = baseUrl ?? string.Empty,
            ApiKey = apiKey ?? string.Empty,
            Model = model ?? string.Empty,
            TimeoutSeconds = timeoutSeconds,
        };
}

/// <summary>
/// Deterministic <see cref="EmailAI.Application.AI.IAiService"/> stand-in that records what each
/// AI operation was asked for: which operation, the requested output language and the current
/// user handed to it. Host tests use it to prove the language contract and the trusted-identity
/// path without contacting a provider.
/// </summary>
internal sealed class RecordingAiService : EmailAI.Application.AI.IAiService
{
    private readonly List<(string Operation, EmailAI.Application.AI.AiLanguage Language, MailboxIdentity? CurrentUser)> _calls = [];

    /// <summary>Every recorded call, in order.</summary>
    public IReadOnlyList<(string Operation, EmailAI.Application.AI.AiLanguage Language, MailboxIdentity? CurrentUser)> Calls => _calls;

    public Task<EmailAI.Application.AI.AiContent> SummarizeMessageAsync(
        EmailMessage message,
        EmailAI.Application.AI.AiLanguage language,
        MailboxIdentity? currentUser,
        CancellationToken cancellationToken)
    {
        _calls.Add(("summarize", language, currentUser));
        return Task.FromResult(new EmailAI.Application.AI.AiContent("summary"));
    }

    public Task<EmailAI.Application.AI.AiContent> SummarizeThreadAsync(
        IReadOnlyList<EmailMessage> messages,
        EmailAI.Application.AI.AiLanguage language,
        MailboxIdentity? currentUser,
        CancellationToken cancellationToken)
    {
        _calls.Add(("thread-summarize", language, currentUser));
        return Task.FromResult(new EmailAI.Application.AI.AiContent("thread summary"));
    }

    public Task<EmailAI.Application.AI.AiContent> SuggestReplyAsync(
        EmailMessage target,
        IReadOnlyList<EmailMessage> history,
        EmailAI.Application.AI.AiLanguage language,
        MailboxIdentity? currentUser,
        CancellationToken cancellationToken)
    {
        _calls.Add(("suggest-reply", language, currentUser));
        return Task.FromResult(new EmailAI.Application.AI.AiContent("suggestion"));
    }

    public Task<EmailAI.Application.AI.AiContent> GenerateReplyAsync(
        EmailMessage target,
        IReadOnlyList<EmailMessage> history,
        EmailAI.Application.AI.AiLanguage language,
        MailboxIdentity? currentUser,
        CancellationToken cancellationToken)
    {
        _calls.Add(("generate-reply", language, currentUser));
        return Task.FromResult(new EmailAI.Application.AI.AiContent("draft"));
    }
}

/// <summary>
/// Identity provider stand-in for host tests: returns one fixed identity, exactly like an
/// Exchange-enabled host would after resolving the mailbox through the directory.
/// </summary>
internal sealed class StubMailboxIdentityProvider(MailboxIdentity identity)
    : EmailAI.Application.Exchange.IMailboxIdentityProvider
{
    public Task<MailboxIdentity> GetCurrentUserAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(identity);
}


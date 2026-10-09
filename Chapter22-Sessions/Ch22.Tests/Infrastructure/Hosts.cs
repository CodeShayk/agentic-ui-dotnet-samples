using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AGUI.Client;
using Beacon.AI;
using Beacon.Client.Core;
using Ch22.Beacon.Agent;
using Ch22.Beacon.Agent.Compaction;
using Ch22.Beacon.Desk.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch22.Tests.Infrastructure;

/// <summary>A folder for one test's conversations. Two hosts over the same folder are a restart.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ch22-tests", Guid.NewGuid().ToString("N"));

    // Windows can hold a file that was just written for a moment (indexers, antivirus), so a recursive delete
    // occasionally leaves an empty folder behind. Try a few times; a leftover temp folder isn't a test failure.
    public void Dispose()
    {
        for (var attempt = 1; attempt <= 5 && Directory.Exists(Path); attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }
}

/// <summary>The scripted model, recording every request it gets: the pipeline's and compaction's.</summary>
public sealed class ModelSpy(IChatClient inner) : DelegatingChatClient(inner)
{
    public ConcurrentQueue<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = new();

    /// <summary>The requests the agent's pipeline made, without compaction's summaries.</summary>
    public IReadOnlyList<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> RunCalls =>
        [.. Calls.Where(c => !IsSummary(c.Messages))];

    public IReadOnlyList<IReadOnlyList<ChatMessage>> SummaryCalls => [.. Calls.Where(c => IsSummary(c.Messages)).Select(c => c.Messages)];

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatMessage> sent = [.. messages];
        Calls.Enqueue((sent, options));
        await foreach (var update in base.GetStreamingResponseAsync(sent, options, cancellationToken))
        {
            yield return update;
        }
    }

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    private static bool IsSummary(IReadOnlyList<ChatMessage> messages) =>
        messages is [var only] && only.Text.StartsWith(HistoryCompactor.PromptMarker, StringComparison.Ordinal);
}

/// <summary>
/// Chapter 22's agent in memory, exactly as <c>Program.Main</c> builds it, over a conversation folder the test
/// owns, with the scripted model streaming at full speed and recorded.
/// </summary>
public sealed class AgentHost(
    string folder,
    IReadOnlyDictionary<string, string?>? settings = null,
    TimeProvider? time = null,
    TimeSpan? chunkDelay = null,
    IEnumerable<ScriptRule>? rules = null)
    : WebApplicationFactory<Program>
{
    public ModelSpy Model { get; } = new(new ScriptedChatClient(rules ?? SessionScripts.Create(), new ScriptedChatClientOptions { ChunkDelay = chunkDelay ?? TimeSpan.Zero }));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Beacon:Conversations:Directory", folder);
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.AddKeyedSingleton<IChatClient>(Program.ModelKey, Model);
            if (time is not null)
            {
                services.AddSingleton(time);
            }
        });
    }

    /// <summary>A small budget, so a few turns are enough to compact.</summary>
    public static IReadOnlyDictionary<string, string?> TightBudget(int tokens = 400, int keepRecentTurns = 1) => new Dictionary<string, string?>
    {
        ["Beacon:Compaction:MaxContextTokens"] = tokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Beacon:Compaction:KeepRecentTurns"] = keepRecentTurns.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

/// <summary>Beacon Desk's client stack against a host: tap, AGUIChatClient, the conversation and the thread API.</summary>
public sealed class DeskStack : IDisposable
{
    public DeskStack(AgentHost host, string threadId)
    {
        Http = host.CreateClient();
        Tap = WireEventTap.ForHttp(Http, "/agent");
        Client = new AGUIChatClient(new AGUIChatClientOptions { Transport = Tap });
        Conversation = new AgentConversation(Client, new ConversationOptions { ThreadId = threadId, Tap = Tap });
        Threads = new ThreadClient(Http, "/agent/threads");
        Sessions = new ConversationSessions(Conversation, Threads);
    }

    public HttpClient Http { get; }

    public WireEventTap Tap { get; }

    public AGUIChatClient Client { get; }

    public AgentConversation Conversation { get; }

    public ThreadClient Threads { get; }

    public ConversationSessions Sessions { get; }

    public ConversationView View => Conversation.View;

    public void Dispose()
    {
        Conversation.Dispose();
        Client.Dispose();
        Http.Dispose();
    }
}

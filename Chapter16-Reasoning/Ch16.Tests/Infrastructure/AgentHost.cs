using System.Collections.Concurrent;
using Beacon.AI;
using Ch16.Beacon.Agent;
using Ch16.Beacon.Agent.Reasoning;
using Ch16.Beacon.Agent.Scripted;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch16.Tests.Infrastructure;

/// <summary>Records what the model was asked, then lets the scripted model answer.</summary>
public sealed class RecordingChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    private readonly ConcurrentQueue<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> _calls = new();

    public IReadOnlyList<IReadOnlyList<ChatMessage>> Calls => [.. _calls.Select(c => c.Messages)];

    public IReadOnlyList<ChatOptions?> Options => [.. _calls.Select(c => c.Options)];

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        _calls.Enqueue(([.. messages], options));
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }
}

/// <summary>
/// Ch16.Beacon.Agent in memory, built as its Program.Main builds it, with the scripted model streaming
/// without pauses and a chosen reasoning disclosure.
/// </summary>
public sealed class AgentHost(ReasoningDisclosure disclosure = ReasoningDisclosure.Summary, int? maxShownCharacters = null)
    : WebApplicationFactory<Program>
{
    private RecordingChatClient? _model;

    /// <summary>The model as the pipeline sees it (below the policy and function invocation).</summary>
    public RecordingChatClient Model => _model ?? throw new InvalidOperationException("The host hasn't started.");

    /// <summary>An HttpClient into the host that records every raw request and response body.</summary>
    public (HttpClient Http, RawHttpRecorder Recorder) RecordingClient()
    {
        var recorder = new RawHttpRecorder();
        return (CreateDefaultClient(recorder), recorder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Beacon:Reasoning:Disclosure", disclosure.ToString());
        if (maxShownCharacters is { } max)
        {
            builder.UseSetting("Beacon:Reasoning:MaxShownCharacters", max.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        builder.ConfigureTestServices(services =>
        {
            services.AddKeyedSingleton<IChatClient>(Program.ModelKey, (sp, _) => _model = new RecordingChatClient(new ScriptedChatClient(
                [.. sp.GetRequiredService<DiagnosisScripts>().Rules, .. BeaconScripts.Default],
                new ScriptedChatClientOptions { ChunkDelay = TimeSpan.Zero })));
        });
    }
}

/// <summary>Keeps every request body and response body exactly as they crossed the wire.</summary>
public sealed class RawHttpRecorder : DelegatingHandler
{
    private readonly ConcurrentQueue<(string Request, string Response)> _exchanges = new();

    public IReadOnlyList<string> Requests => [.. _exchanges.Select(e => e.Request)];

    public IReadOnlyList<string> Responses => [.. _exchanges.Select(e => e.Response)];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = string.Empty;
        if (request.Content is not null)
        {
            await request.Content.LoadIntoBufferAsync(cancellationToken);
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        var response = await base.SendAsync(request, cancellationToken);

        // Buffer the stream so it can be both recorded and read by the client.
        await response.Content.LoadIntoBufferAsync(cancellationToken);
        _exchanges.Enqueue((body, await response.Content.ReadAsStringAsync(cancellationToken)));
        return response;
    }
}

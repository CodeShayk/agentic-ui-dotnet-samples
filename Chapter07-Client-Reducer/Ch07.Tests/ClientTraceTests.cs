using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;
using Beacon.Client.Core;
using Ch07.Beacon.Desk.Components.Chat;
using Microsoft.Extensions.AI;

namespace Ch07.Tests;

/// <summary>
/// Section 7.2's trace: the recording behind Wire 7.1 replayed through the real <c>AGUIChatClient</c>
/// over a transport that prints each event as it passes, with each update the client yields printed
/// beneath. Run it with <c>dotnet test --project Ch07.Tests --filter-method "*Trace*" --output Detailed</c>
/// to see the lines. Also the Desk's screen-reader announcement, projected from the same kind of replay.
/// </summary>
public sealed class ClientTraceTests
{
    [Fact]
    public async Task Trace_sixteen_events_in_twelve_updates_out()
    {
        List<string> lines = [];
        var wire = new TracingTransport(new ReplayTransport(await Recordings.LoadAsync("agent-wo-1001-status.sse")), lines);
        using var client = new AGUIChatClient(new AGUIChatClientOptions { Transport = wire });

        var updates = 0;
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "What is the status of WO-1001?") { MessageId = "user-1" }],
            new ChatOptions { RawRepresentationFactory = _ => new RunAgentInput { ThreadId = "t-1" } },
            TestContext.Current.CancellationToken))
        {
            updates++;
            var raw = (update.RawRepresentation as BaseEvent)?.Type ?? "-";
            var content = update.Contents.Count == 0 ? "(no content)" : string.Join(", ", update.Contents.Select(Describe));
            lines.Add($"      update {updates,2}: raw={raw,-25}{content}  messageId={update.MessageId}");
        }

        foreach (var line in lines)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(line);
        }

        Assert.Equal(16, lines.Count(l => l.StartsWith("wire:", StringComparison.Ordinal)));
        Assert.Equal(12, updates);
    }

    [Fact]
    public async Task The_desk_announces_a_running_tool_once_and_clears_it_when_the_result_arrives()
    {
        using var harness = await Harness.ReplayAsync("tour-tools.sse");

        List<string> announced = [];
        foreach (var (_, view) in harness.Published)
        {
            var text = ToolLine.Announcement(view);
            if (announced.Count == 0 || announced[^1] != text)
            {
                announced.Add(text);
            }
        }

        Assert.Equal(["", "Beacon is looking up WO-1001…", ""], announced);
    }

    private static string Describe(AIContent content) => content switch
    {
        TextContent text => $"TextContent \"{(text.Text.Length > 22 ? text.Text[..22] + "…" : text.Text)}\"",
        FunctionCallContent call => $"FunctionCallContent {call.Name} informational={call.InformationalOnly}",
        FunctionResultContent result => $"FunctionResultContent {result.CallId}",
        _ => content.GetType().Name,
    };

    // Prints each event as it comes off the transport, before AGUIChatClient sees it.
    private sealed class TracingTransport(IAGUITransport inner, List<string> lines) : IAGUITransport
    {
        public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var evt in inner.SendAsync(input, cancellationToken).ConfigureAwait(false))
            {
                lines.Add($"wire: {evt.Type}");
                yield return evt;
            }
        }
    }
}

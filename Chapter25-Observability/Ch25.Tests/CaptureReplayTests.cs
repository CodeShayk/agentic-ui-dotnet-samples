using System.Net.ServerSentEvents;
using AGUI.Client;
using Ch25.Beacon.Console;
using Microsoft.Extensions.AI;

namespace Ch25.Tests;

/// <summary>A captured run replays through AGUIChatClient exactly as it arrived.</summary>
public sealed class CaptureReplayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_captured_run_replays_to_the_same_answer_and_usage_without_a_server()
    {
        await using var host = TracedAgentHost.Start();
        var folder = Directory.CreateTempSubdirectory("ch25-capture-").FullName;

        try
        {
            // Live: the console's capture handler sits in the client's HttpClient.
            using var http = new HttpClient(new WireCaptureHandler(folder) { InnerHandler = host.Server.CreateHandler() })
            {
                BaseAddress = host.Server.BaseAddress,
            };
            using IChatClient live = new AGUIChatClient(new AGUIChatClientOptions(http, new Uri(host.Server.BaseAddress, "agent").ToString()));
            var liveUpdates = await AskAsync(live);

            var capture = Assert.Single(Directory.GetFiles(folder, "*.sse"));
            Assert.Single(Directory.GetFiles(folder, "*.request.json"));

            // The capture is the raw stream: SSE frames ending in RUN_FINISHED.
            await using (var file = File.OpenRead(capture))
            {
                List<string> frames = [];
                await foreach (var item in SseParser.Create(file).EnumerateAsync(Ct))
                {
                    frames.Add(item.Data);
                }

                Assert.StartsWith("{\"type\":\"RUN_STARTED\"", frames[0], StringComparison.Ordinal);
                Assert.StartsWith("{\"type\":\"RUN_FINISHED\"", frames[^1], StringComparison.Ordinal);
            }

            // Replay: the same client code, a file instead of a server.
            List<string> replayedTypes = [];
            using IChatClient replay = new AGUIChatClient(new AGUIChatClientOptions
            {
                Transport = new ReplayTransport(capture, evt => replayedTypes.Add(evt.Type)),
            });
            var replayedUpdates = await AskAsync(replay);

            Assert.Equal(Text(liveUpdates), Text(replayedUpdates));
            Assert.Equal(Usage(liveUpdates), Usage(replayedUpdates));
            Assert.Equal("RUN_STARTED", replayedTypes[0]);
            Assert.Equal("RUN_FINISHED", replayedTypes[^1]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task<List<ChatResponseUpdate>> AskAsync(IChatClient client)
    {
        List<ChatResponseUpdate> updates = [];
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "What is the status of WO-1001?") { MessageId = "user-1" }], cancellationToken: Ct))
        {
            updates.Add(update);
        }

        return updates;
    }

    private static string Text(List<ChatResponseUpdate> updates) => string.Concat(updates.Select(u => u.Text));

    private static string Usage(List<ChatResponseUpdate> updates) => string.Join(", ", updates
        .SelectMany(u => u.Contents.OfType<UsageContent>().Select(c => $"{u.ModelId}:{c.Details.InputTokenCount}/{c.Details.OutputTokenCount}")));
}

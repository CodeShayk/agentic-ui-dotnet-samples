using System.Net;
using System.Text;
using Beacon.Client.Core;
using Ch19.Beacon.Adaptive.Versioning;
using Ch19.Tests.Infrastructure;

namespace Ch19.Tests;

/// <summary>
/// A 1.0 client meeting a pre-1.0 producer: no protocolVersion on RUN_STARTED, reasoning in the retired
/// THINKING_* shapes and a null where 1.0 omits the field. The .NET client survives it; whether it
/// keeps the reasoning is the question.
/// </summary>
public sealed class LegacyPeerTests
{
    private const string PreOneZeroStream =
        """
        data: {"type":"RUN_STARTED","threadId":"t-old","runId":"r-old"}

        data: {"type":"THINKING_START","title":"Checking the unit"}

        data: {"type":"THINKING_TEXT_MESSAGE_START"}

        data: {"type":"THINKING_TEXT_MESSAGE_CONTENT","delta":"Short-cycling usually means the compressor trips."}

        data: {"type":"THINKING_TEXT_MESSAGE_END"}

        data: {"type":"THINKING_END"}

        data: {"type":"TEXT_MESSAGE_START","messageId":"m-old","role":"assistant"}

        data: {"type":"TEXT_MESSAGE_CONTENT","messageId":"m-old","delta":"Check the run capacitor first."}

        data: {"type":"TEXT_MESSAGE_END","messageId":"m-old"}

        data: {"type":"RUN_FINISHED","threadId":"t-old","runId":"r-old","result":null}


        """;

    [Fact]
    public async Task The_answer_survives_and_the_retired_reasoning_shapes_are_skipped_not_translated()
    {
        using var http = new HttpClient(new CannedStream(PreOneZeroStream));
        using var conversation = ClientStack.Conversation(http, new Uri("http://legacy.example/agent"));

        await conversation.SendAsync("Why is EQ-501 short-cycling?", cancellationToken: TestContext.Current.CancellationToken);
        var view = conversation.View;

        Assert.Equal(ConversationStatus.Idle, view.Status);
        Assert.Equal("Check the run capacitor first.", view.Messages.OfType<AssistantMessageView>().Single().Text);

        // AGUI.Client 1.0.0 has no THINKING_* translation (the TypeScript client does): the events are
        // unrecognized, so they are dropped, and the reasoning they carried is lost.
        Assert.Empty(view.Messages.OfType<ReasoningMessageView>());

        Assert.Null(view.CurrentRun!.ProtocolVersion);
        Assert.Equal(VersionVerdict.Absent, ProducerVersionCheck.From(view.CurrentRun.ProtocolVersion).Verdict);
    }

    private sealed class CannedStream(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body.ReplaceLineEndings("\n"), Encoding.UTF8, "text/event-stream"),
            });
    }
}

using System.Net;
using System.Text;
using Beacon.Client.Core;
using Ch19.Tests.Infrastructure;

namespace Ch19.Tests;

/// <summary>
/// A 1.0 client meeting a newer producer. The spec says a consumer strips a union member it doesn't recognize
/// and carries on; AGUI.Client 1.0.0 throws on one instead, so a legal 1.x addition ends the turn.
/// </summary>
public sealed class ForwardCompatibilityTests
{
    // A run from a hypothetical 1.1 agent: everything 1.0 knows, then an outcome type 1.0 doesn't define.
    private const string NewerStream =
        """
        data: {"type":"RUN_STARTED","threadId":"t-new","runId":"r-new","protocolVersion":"1.1"}

        data: {"type":"TEXT_MESSAGE_START","messageId":"m-new","role":"assistant"}

        data: {"type":"TEXT_MESSAGE_CONTENT","messageId":"m-new","delta":"Handing you to the warranty desk."}

        data: {"type":"TEXT_MESSAGE_END","messageId":"m-new"}

        data: {"type":"RUN_FINISHED","threadId":"t-new","runId":"r-new","outcome":{"type":"handoff","to":"warranty-desk"}}


        """;

    [Fact]
    public async Task An_unknown_outcome_ends_the_turn_on_a_1_0_0_client_but_what_arrived_before_it_stays()
    {
        using var http = new HttpClient(new CannedStream(NewerStream));
        using var conversation = ClientStack.Conversation(http, new Uri("http://newer.example/agent"));

        await conversation.SendAsync("Who covers EQ-501?", cancellationToken: TestContext.Current.CancellationToken);
        var view = conversation.View;

        // The SDK's JsonException surfaces as an unexpected failure, not as a protocol violation: the producer broke no rule.
        Assert.Equal(ConversationStatus.Failed, view.Status);
        Assert.Equal(FailureKind.Unexpected, view.Failure!.Kind);
        Assert.Equal("Handing you to the warranty desk.", view.Messages.OfType<AssistantMessageView>().Single().Text);
        Assert.Equal("1.1", view.CurrentRun!.ProtocolVersion);
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

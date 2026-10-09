using System.Net;
using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;
using Beacon.Client.Core;
using Bunit;
using Ch10.Beacon.Desk.Components.Chat;
using Ch10.Beacon.Desk.Screen;
using Ch10.Beacon.Field;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch10.Tests;

/// <summary>What each client says when a turn fails: a 4xx instead of a stream, or a stream that breaks a rule.</summary>
public sealed class FailureWordingTests : BunitContext
{
    [Fact]
    public async Task The_Desk_names_a_refused_request_and_its_status()
    {
        var chat = await AskAsync(new RefusingTransport(HttpStatusCode.Conflict));

        chat.WaitForAssertion(() => Assert.Equal(
            "Beacon's agent refused the request (HTTP 409). Start a new chat if it happens again.",
            chat.Find(".bd-status__error").TextContent));
    }

    [Fact]
    public async Task The_Desk_names_a_broken_protocol_rule_as_one()
    {
        // A STATE_DELTA must be a JSON array of operations; this one is a lone operation.
        using var patch = System.Text.Json.JsonDocument.Parse("""{"op":"add","path":"/status","value":"done"}""");
        var chat = await AskAsync(new EventsTransport(
            new RunStartedEvent { ThreadId = "t", RunId = "r" },
            new StateDeltaEvent { Delta = patch.RootElement.Clone() },
            new RunFinishedEvent { ThreadId = "t", RunId = "r" }));

        chat.WaitForAssertion(() => Assert.Equal(
            "Beacon's answer broke a protocol rule, so the Desk stopped reading it.",
            chat.Find(".bd-status__error").TextContent));
    }

    [Theory]
    [InlineData(FailureKind.Rejected, "Beacon refused the request. Start a new chat if it happens again.")]
    [InlineData(FailureKind.Unreachable, "Beacon can't be reached.")]
    [InlineData(FailureKind.AgentError, "Something went wrong. Try again.")]
    public void The_Field_names_each_failure_in_the_technicians_words(FailureKind kind, string expected)
    {
        var view = ConversationView.Empty with { Status = ConversationStatus.Failed, Failure = new RunFailure(kind, "details") };

        Assert.Equal(expected, FieldStatus.Describe(view));
    }

    private async Task<IRenderedComponent<DeskChat>> AskAsync(IAGUITransport transport)
    {
        Services.AddLogging();
        Services.AddSingleton<DeskScreen>();
        Services.AddSingleton<IChatClient>(new AGUIChatClient(new AGUIChatClientOptions { Transport = transport }));
        Services.AddSingleton(services => new AgentConversation(services.GetRequiredService<IChatClient>()));
        JSInterop.Mode = JSRuntimeMode.Loose;
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));

        var chat = Render<DeskChat>();
        await Renderer.Dispatcher.InvokeAsync(() =>
        {
            chat.Find("input.bd-composer__input").Input("Open WO-1001");
            chat.Find("form.bd-composer").Submit();
        });

        return chat;
    }

    private sealed class EventsTransport(params BaseEvent[] events) : IAGUITransport
    {
        public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var evt in events)
            {
                await Task.Yield();
                yield return evt;
            }
        }
    }

    // What AGUIChatClient's HTTP transport throws for a non-2xx status.
    private sealed class RefusingTransport(HttpStatusCode status) : IAGUITransport
    {
        public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new HttpRequestException($"Response status code does not indicate success: {(int)status}.", null, status);
#pragma warning disable CS0162 // Unreachable code: the iterator needs a yield to be one.
            yield break;
#pragma warning restore CS0162
        }
    }
}

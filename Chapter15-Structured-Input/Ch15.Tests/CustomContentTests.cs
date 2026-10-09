using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AGUI.Abstractions;
using AGUI.Server;
using Beacon.Domain;
using Beacon.Testing;
using Ch15.Beacon.Agent;
using Ch15.Beacon.Agent.Input;
using Microsoft.Extensions.AI;

namespace Ch15.Tests;

/// <summary>
/// AGUI.Server 1.0.0 serializes every update it converts (for rawEvent), so content the pipeline invents
/// must be serializable by the endpoint's JSON options, or the run fails before its interrupt is written.
/// </summary>
public sealed class CustomContentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unregistered_custom_content_ends_the_run_in_RUN_ERROR()
    {
        var events = await ConvertAsync(register: false);

        var error = Assert.IsType<RunErrorEvent>(events[^1]);
        Assert.DoesNotContain(events, e => e is RunFinishedEvent);
        Assert.Equal("StreamingError", error.Code);
    }

    [Fact]
    public async Task Registered_custom_content_becomes_an_interrupt_through_MapInterrupt()
    {
        var events = await ConvertAsync(register: true);

        var finished = Assert.IsType<RunFinishedEvent>(events[^1]);
        var interrupt = Assert.Single(Assert.IsType<RunFinishedInterruptOutcome>(finished.Outcome).Interrupts);
        Assert.Equal(("inp_test", InterruptReasons.InputRequired), (interrupt.Id, interrupt.Reason));
        Assert.Equal("2026-11-16T16:35:00Z", interrupt.ExpiresAt);
        Assert.Null(interrupt.ToolCallId);
    }

    private static async Task<IReadOnlyList<BaseEvent>> ConvertAsync(bool register)
    {
        // The endpoint's JSON configuration as AddAGUIHosting leaves it, with or without the line Program adds.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        options.TypeInfoResolverChain.Insert(0, AGUIJsonUtilities.DefaultTypeInfoResolver);
        options.TypeInfoResolverChain.Add(AIJsonUtilities.DefaultOptions.TypeInfoResolver!);
        AGUIJsonUtilities.RegisterInterruptContentTypes(options);
        if (register)
        {
            options.AddAIContentType<InputRequestedContent>(Ch15Json.InputRequestedDiscriminator);
        }

        var question = new SerialNumberForm(new InMemoryFieldServiceStore()).Ask(new Dictionary<string, object?> { ["equipmentId"] = "EQ-601" })!;
        var request = new PendingInput
        {
            Id = "inp_test",
            ThreadId = "thread-1",
            RunId = "run-1",
            ToolCallId = "call-1",
            ToolName = SerialNumberForm.Name,
            Arguments = Wire.Json("""{"equipmentId":"EQ-601"}"""),
            Question = question,
            ProposedAt = AgentHost.Start,
            ExpiresAt = AgentHost.Start.AddMinutes(15),
        };

        ChatResponseUpdate[] updates =
        [
            new(ChatRole.Assistant, "I need the serial number.") { MessageId = "msg-1" },
            new(ChatRole.Assistant, [new InputRequestedContent { InputId = request.Id, Request = request }]) { MessageId = "msg-1" },
        ];

        var context = AGUIWire.UserRun("Check EQ-601's warranty.").ToChatRequestContext(options, InputInterrupts.StreamOptions());
        List<BaseEvent> events = [];
        await foreach (var evt in updates.ToAsyncEnumerable().AsAGUIEventStreamAsync(context, Ct))
        {
            events.Add(evt);
        }

        return events;
    }
}

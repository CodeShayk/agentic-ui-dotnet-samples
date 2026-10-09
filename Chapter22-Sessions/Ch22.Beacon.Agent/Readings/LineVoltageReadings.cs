using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using Ch22.Beacon.Agent.Conversations;
using Ch22.Beacon.Agent.Runs;
using Microsoft.Extensions.AI;

namespace Ch22.Beacon.Agent.Readings;

/// <summary>
/// One question only a technician on site can answer: the three line-to-line voltages at a compressor
/// contactor. The model asks by calling <see cref="ToolName"/>; the run ends with an input interrupt
/// (Chapter 15 builds the general form of this), and the answer goes back to the model as the call's result.
/// What matters in this chapter is where the open question lives: in the run's record on the server, so it
/// survives a closed app, a restart and compaction.
/// </summary>
public sealed class LineVoltageReadings(TimeProvider time)
{
    public const string ToolName = "ask_for_line_voltages";

    /// <summary>How long the technician has to answer: about a shift.</summary>
    public static readonly TimeSpan TimeToLive = TimeSpan.FromHours(8);

    private static readonly JsonElement ResponseSchema = Parse("""
        {
          "type": "object",
          "properties": {
            "l1l2": { "type": "number", "title": "L1-L2 (V)", "minimum": 0, "maximum": 700 },
            "l2l3": { "type": "number", "title": "L2-L3 (V)", "minimum": 0, "maximum": 700 },
            "l1l3": { "type": "number", "title": "L1-L3 (V)", "minimum": 0, "maximum": 700 }
          },
          "required": ["l1l2", "l2l3", "l1l3"],
          "additionalProperties": false
        }
        """);

    private static readonly string[] Legs = ["l1l2", "l2l3", "l1l3"];

    // Microsoft.Extensions.AI's defaults indent; the call's arguments are stored and restated compactly.
    private static readonly JsonSerializerOptions CompactArguments = new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };

    /// <summary>The tool the model is offered. It has no implementation: a person answers it.</summary>
    public AIFunctionDeclaration Declaration { get; } = AIFunctionFactory.CreateDeclaration(
        ToolName,
        "Asks the technician on site to measure the three line-to-line voltages at the compressor contactor. " +
        "The run pauses until they answer; the readings come back as this call's result.",
        Parse("""{"type":"object","properties":{"workOrderId":{"type":"string","description":"The work order, for example WO-1005."}},"required":["workOrderId"]}"""));

    /// <summary>Turns the model's call into the interrupt the client sees, and remembers the call.</summary>
    public InterruptRequestContent Ask(FunctionCallContent call, RunScope run)
    {
        var interruptId = $"inp_{Guid.NewGuid():N}"[..28];
        var expiresAt = time.GetUtcNow() + TimeToLive;
        var arguments = JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>(), CompactArguments.GetTypeInfo(typeof(IDictionary<string, object?>)));
        var workOrderId = call.Arguments?.TryGetValue("workOrderId", out var wo) == true ? wo?.ToString() : null;

        run.PendingInputs.Add(new PendingInput(interruptId, call.CallId, call.Name, arguments, expiresAt));

        return new InterruptRequestContent(interruptId)
        {
            Reason = InterruptReasons.InputRequired,
            Message = $"Measure the line-to-line voltage at the compressor contactor{(workOrderId is null ? "" : $" for {workOrderId}")} " +
                      "(L1-L2, L2-L3 and L1-L3) and enter the three readings in volts.",
            ResponseSchema = ResponseSchema,
            ExpiresAt = expiresAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// Checks the resume entries against the interrupts of the run being continued and turns each answer into
    /// the call and result the model needs. Entries for interrupts that run didn't raise are ignored; an open
    /// interrupt with no entry, an expired answer or a payload that doesn't fit the schema refuses the run.
    /// </summary>
    public (IReadOnlyList<AGUIMessage> Exchange, RunRejection? Rejection) Answer(RunRecord? continued, IList<AGUIResume> resume)
    {
        var open = continued is { IsInterrupted: true } ? continued.PendingInputs : [];
        List<AGUIMessage> exchange = [];

        foreach (var pending in open)
        {
            var entry = resume.FirstOrDefault(r => r.InterruptId == pending.InterruptId);
            if (entry is null)
            {
                return ([], new RunRejection(StatusCodes.Status409Conflict, "Interrupt not answered",
                    $"The agent is waiting for an answer to {pending.InterruptId}. Answer it or abandon it in the resume list."));
            }

            string result;
            if (entry.Status == ResumeStatus.Cancelled)
            {
                result = """{"cancelled":true,"note":"The technician didn't take the readings."}""";
            }
            else if (time.GetUtcNow() >= pending.ExpiresAt)
            {
                return ([], new RunRejection(StatusCodes.Status409Conflict, "Interrupt expired",
                    $"{pending.InterruptId} expired at {pending.ExpiresAt:u}. It can only be abandoned."));
            }
            else if (Validate(entry.Payload) is { } problem)
            {
                return ([], new RunRejection(StatusCodes.Status400BadRequest, "Readings not valid", problem));
            }
            else
            {
                result = string.Create(CultureInfo.InvariantCulture,
                    $$"""{"l1l2":{{Leg(entry.Payload!.Value, "l1l2")}},"l2l3":{{Leg(entry.Payload.Value, "l2l3")}},"l1l3":{{Leg(entry.Payload.Value, "l1l3")}}}""");
            }

            // The model asked by calling a tool, so the answer goes back as that call's result. Both messages are
            // the server's; the tool message carries the call's ID, as every tool result on the wire does.
            exchange.Add(new AGUIAssistantMessage
            {
                Id = $"{pending.InterruptId}-request",
                ToolCalls = [new AGUIToolCall
                {
                    Id = pending.CallId,
                    Type = "function",
                    Function = new AGUIToolCallFunction { Name = pending.ToolName, Arguments = pending.ArgumentsJson },
                }],
            });
            exchange.Add(new AGUIToolMessage { Id = pending.CallId, ToolCallId = pending.CallId, Content = result });
        }

        return (exchange, null);
    }

    private static string? Validate(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } readings)
        {
            return "The answer must be an object with l1l2, l2l3 and l1l3 in volts.";
        }

        foreach (var property in readings.EnumerateObject())
        {
            if (!Legs.Contains(property.Name))
            {
                return $"'{property.Name}' is not one of the readings asked for.";
            }
        }

        foreach (var leg in Legs)
        {
            if (!readings.TryGetProperty(leg, out var value) || value.ValueKind != JsonValueKind.Number
                || value.GetDecimal() is < 0 or > 700)
            {
                return $"'{leg}' must be a number of volts from 0 to 700.";
            }
        }

        return null;
    }

    private static decimal Leg(JsonElement readings, string leg) => readings.GetProperty(leg).GetDecimal();

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>
/// Sits between <c>FunctionInvokingChatClient</c> and the model, so it sees the model's calls first. A call to
/// <see cref="LineVoltageReadings.ToolName"/> becomes an interrupt; function invocation never sees it as a call,
/// so the turn ends there and the SDK finishes the run with the interrupt outcome.
/// </summary>
public sealed class ReadingRequestChatClient(IChatClient innerClient, LineVoltageReadings readings) : DelegatingChatClient(innerClient)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var run = RunScope.From(options);

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            if (run is not null && update.Contents.Any(c => c is FunctionCallContent { Name: LineVoltageReadings.ToolName }))
            {
                update.Contents = [.. update.Contents.Select(c => c is FunctionCallContent { Name: LineVoltageReadings.ToolName } call ? readings.Ask(call, run) : c)];
            }

            yield return update;
        }
    }

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);
}

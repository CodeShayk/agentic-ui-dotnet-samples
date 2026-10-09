using System.Text.Json;
using AGUI.Abstractions;
using static Ch03.EventTour.TourRun;

namespace Ch03.EventTour;

/// <summary>
/// One scripted Beacon run per event family. Each script yields the events a producer would emit,
/// as the AGUI.Abstractions types that serialize to them, so the wire name and the .NET type of
/// every event sit side by side. The data matches Beacon's seed data (WO-1001, EQ-501, CAP-45-5).
/// </summary>
internal static class Scripts
{
    private static readonly TokenUsage Usage = new()
    {
        Provider = "beacon",
        Model = "beacon-scripted",
        InputTokens = 1240,
        OutputTokens = 96,
        TotalTokens = 1336,
        CachedInputTokens = 1024,
    };

    // RUN_STARTED, TEXT_MESSAGE_*, RUN_FINISHED, as in Chapter 2, plus the envelope's optional fields:
    // a timestamp on the run's boundaries and metadata that merges into the message.
    public static IEnumerable<object> Text(TourRun run)
    {
        var message = run.Id("msg");

        var started = run.Started();
        started.Timestamp = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();
        yield return started;

        yield return new TextMessageStartEvent
        {
            MessageId = message,
            Role = AGUIRoles.Assistant,
            Metadata = Json("""{"contoso.model":"beacon-scripted"}"""),
        };

        foreach (var delta in Chunk("I'm Beacon, Contoso Climate's field-service assistant. Ask me about a work order, a part or today's schedule."))
        {
            yield return new TextMessageContentEvent { MessageId = message, Delta = delta };
        }

        yield return new TextMessageEndEvent { MessageId = message, Metadata = Json("""{"contoso.finishReason":"stop"}""") };

        var finished = run.Finished(); // No outcome: absent means success.
        finished.Timestamp = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();
        yield return finished;
    }

    // Steps, a backend tool call with its result, two text messages and token usage.
    public static IEnumerable<object> Tools(TourRun run)
    {
        var intro = run.Id("msg", 1);
        var call = run.Id("call");
        var answer = run.Id("msg", 2);

        yield return run.Started();
        yield return new StepStartedEvent { StepName = "look-up" };

        foreach (var evt in Say(intro, "Let me pull up WO-1001."))
        {
            yield return evt;
        }

        yield return new ToolCallStartEvent { ToolCallId = call, ToolCallName = "get_work_order", ParentMessageId = intro };
        yield return new ToolCallArgsEvent { ToolCallId = call, Delta = "{\"workOrderId\":" };
        yield return new ToolCallArgsEvent { ToolCallId = call, Delta = "\"WO-1001\"}" };
        yield return new ToolCallEndEvent { ToolCallId = call };
        yield return new ToolCallResultEvent
        {
            MessageId = run.Id("result"),
            ToolCallId = call,
            Content = new AGUIContent("""{"id":"WO-1001","status":"Scheduled","priority":"High","technicianId":"TECH-1","scheduledFor":"2026-11-16T09:00:00-05:00"}"""),
        };
        yield return new StepFinishedEvent { StepName = "look-up" };

        foreach (var evt in Say(answer,
            "WO-1001 is scheduled for 9:00 today with Priya Raman. Harbor View Dental's air conditioner is short-cycling and blowing warm air."))
        {
            yield return evt;
        }

        yield return run.Finished(new RunFinishedSuccessOutcome(), [Usage]);
    }

    // STATE_SNAPSHOT replaces the shared state; STATE_DELTA amends it with a JSON Patch.
    public static IEnumerable<object> State(TourRun run)
    {
        yield return run.Started();
        yield return new StateSnapshotEvent
        {
            Snapshot = Json("""
                {"workOrderId":"WO-1001","checklist":[
                  {"id":"C1","item":"Check thermostat settings and wiring","done":false},
                  {"id":"C2","item":"Measure refrigerant pressures","done":false},
                  {"id":"C3","item":"Inspect run capacitor","done":false}]}
                """),
        };
        yield return new StateDeltaEvent
        {
            Delta = Json("""[{"op":"add","path":"/checklist/-","value":{"id":"C4","item":"Test compressor amperage","done":false}}]"""),
        };

        foreach (var evt in Say(run.Id("msg"),
            "I've opened the WO-1001 checklist and added a compressor amperage test, since the unit short-cycles."))
        {
            yield return evt;
        }

        yield return run.Finished();
    }

    // Two runs on one stream: a replayed run restates history as a snapshot, then the requested run.
    public static IEnumerable<object> History(TourRun run)
    {
        var earlier = new TourRun(run.ThreadId, $"{run.RunId}-prev");
        var call = earlier.Id("call");

        yield return earlier.Started();
        yield return new MessagesSnapshotEvent
        {
            Messages =
            [
                new AGUIUserMessage { Id = earlier.Id("user"), Content = new AGUIContent("What is the status of WO-1001?") },
                new AGUIAssistantMessage
                {
                    Id = earlier.Id("msg", 1),
                    Content = "Let me pull up WO-1001.",
                    ToolCalls =
                    [
                        new AGUIToolCall
                        {
                            Id = call,
                            Type = "function",
                            Function = new AGUIToolCallFunction { Name = "get_work_order", Arguments = """{"workOrderId":"WO-1001"}""" },
                        },
                    ],
                },
                new AGUIToolMessage { Id = earlier.Id("result"), ToolCallId = call, Content = new AGUIContent("""{"id":"WO-1001","status":"Scheduled"}""") },
                new AGUIAssistantMessage { Id = earlier.Id("msg", 2), Content = "WO-1001 is scheduled for 9:00 today with Priya Raman." },
            ],
        };
        yield return earlier.Finished();

        yield return run.Started();

        foreach (var evt in Say(run.Id("msg"), "Picking up where we left off: WO-1001 is still scheduled for 9:00 with Priya."))
        {
            yield return evt;
        }

        yield return run.Finished();
    }

    // ACTIVITY_SNAPSHOT creates a progress widget; ACTIVITY_DELTA patches it in place.
    public static IEnumerable<object> Activity(TourRun run)
    {
        var search = run.Id("activity");

        yield return run.Started();
        yield return new ActivitySnapshotEvent
        {
            MessageId = search,
            ActivityType = "parts-search",
            Content = Json("""{"query":"run capacitor","model":"CA-36X","found":0,"status":"searching"}"""),
        };
        yield return new ActivityDeltaEvent
        {
            MessageId = search,
            ActivityType = "parts-search",
            Patch = Json("""[{"op":"replace","path":"/found","value":1},{"op":"replace","path":"/status","value":"done"}]"""),
        };

        foreach (var evt in Say(run.Id("msg"),
            "CAP-45-5, a 45/5 uF dual run capacitor, fits the CA-36X. It costs $38.50 and 24 are in stock in Portsmouth."))
        {
            yield return evt;
        }

        yield return run.Finished();
    }

    // A reasoning span holding one reasoning message, its encrypted artifact, then the answer.
    public static IEnumerable<object> Reasoning(TourRun run)
    {
        var span = run.Id("think");
        var thought = run.Id("reasoning");

        yield return run.Started();
        yield return new ReasoningStartEvent { MessageId = span };
        yield return new ReasoningMessageStartEvent { MessageId = thought, Role = AGUIRoles.Reasoning };

        foreach (var delta in Chunk(
            "Short-cycling with warm air usually means the compressor trips on overload. The fault history shows E5 overload trips. " +
            "A weak run capacitor is the cheapest cause to rule out."))
        {
            yield return new ReasoningMessageContentEvent { MessageId = thought, Delta = delta };
        }

        yield return new ReasoningMessageEndEvent { MessageId = thought };
        yield return new ReasoningEncryptedValueEvent
        {
            Subtype = "message",
            EntityId = thought,
            EncryptedValue = "gAAAAABpQ2hhcHRlci0zLW9wYXF1ZS1yZWFzb25pbmctYXJ0aWZhY3Q=",
        };
        yield return new ReasoningEndEvent { MessageId = span };

        foreach (var evt in Say(run.Id("msg"),
            "The most likely cause is a failing run capacitor. Measure CAP-45-5 before you condemn the compressor."))
        {
            yield return evt;
        }

        yield return run.Finished(new RunFinishedSuccessOutcome(),
        [
            new TokenUsage
            {
                Provider = "beacon",
                Model = "beacon-scripted",
                InputTokens = 980,
                OutputTokens = 341,
                TotalTokens = 1321,
                ReasoningTokens = 288,
            },
        ]);
    }

    // Two subagent invocations streaming in parallel; one finishes, one fails, the parent carries on.
    public static IEnumerable<object> Subagents(TourRun run)
    {
        var parts = run.Id("sub-parts");
        var warranty = run.Id("sub-warranty");
        var partsAnswer = run.Id("msg", 2);
        var warrantyAnswer = run.Id("msg", 3);

        yield return run.Started();

        foreach (var evt in Say(run.Id("msg", 1), "I'll ask the parts and warranty specialists about EQ-501."))
        {
            yield return evt;
        }

        yield return new SubagentStartedEvent { SubagentRunId = parts, Name = "parts-specialist", Description = "Finds compatible parts and stock" };
        yield return new SubagentStartedEvent { SubagentRunId = warranty, Name = "warranty-specialist", Description = "Checks coverage" };
        yield return new TextMessageStartEvent { MessageId = partsAnswer, Role = AGUIRoles.Assistant, SubagentRunId = parts };
        yield return new TextMessageStartEvent { MessageId = warrantyAnswer, Role = AGUIRoles.Assistant, SubagentRunId = warranty };
        yield return new TextMessageContentEvent { MessageId = partsAnswer, Delta = "CAP-45-5 fits the CA-36X; " };
        yield return new TextMessageContentEvent { MessageId = warrantyAnswer, Delta = "Parts are covered until 2029-05-14.", SubagentRunId = warranty };
        yield return new TextMessageContentEvent { MessageId = partsAnswer, Delta = "24 are in stock in Portsmouth." };
        yield return new TextMessageEndEvent { MessageId = partsAnswer };
        yield return new SubagentFinishedEvent
        {
            SubagentRunId = parts,
            Outcome = new SubagentFinishedSuccessOutcome(),
            Result = Json("""{"sku":"CAP-45-5","inStock":24}"""),
        };
        yield return new TextMessageEndEvent { MessageId = warrantyAnswer, SubagentRunId = warranty };
        yield return new SubagentErrorEvent { SubagentRunId = warranty, Message = "The labor-warranty lookup timed out.", Code = "warranty_timeout" };

        foreach (var evt in Say(run.Id("msg", 4),
            "CAP-45-5 is in stock and the part is under warranty. I couldn't confirm labor coverage, so check it before quoting labor."))
        {
            yield return evt;
        }

        yield return run.Finished();
    }

    // RAW carries a provider-native event through; CUSTOM carries the application's own.
    public static IEnumerable<object> Passthrough(TourRun run)
    {
        yield return run.Started();
        yield return new RawEvent
        {
            Source = "contoso-inventory",
            Event = Json("""{"kind":"stock.changed","sku":"CAP-45-5","onHand":24,"depot":"Portsmouth"}"""),
        };

        foreach (var evt in Say(run.Id("msg"), "I've let Priya know that CAP-45-5 is in stock in Portsmouth."))
        {
            yield return evt;
        }

        yield return new CustomEvent
        {
            Name = "contoso.beacon.notify",
            Value = Json("""{"technicianId":"TECH-1","text":"CAP-45-5 is in stock in Portsmouth (24 on hand)."}"""),
        };
        yield return run.Finished();
    }

    // The compact form: reasoning, text and a frontend tool call as chunks, with no end events.
    public static IEnumerable<object> Chunks(TourRun run)
    {
        var thought = run.Id("reasoning");
        var message = run.Id("msg");
        var call = run.Id("call");

        yield return run.Started();
        yield return new ReasoningMessageChunkEvent { MessageId = thought, Delta = "The dispatcher wants a summary, " };
        yield return new ReasoningMessageChunkEvent { Delta = "then the work order on screen." };
        yield return new TextMessageChunkEvent { MessageId = message, Role = AGUIRoles.Assistant, Delta = "WO-1001: Harbor View Dental, " };
        yield return new TextMessageChunkEvent { Delta = "EQ-501 short-cycling, " };
        yield return new TextMessageChunkEvent { Delta = "Priya at 9:00." };
        yield return new ToolCallChunkEvent { ToolCallId = call, ToolCallName = "show_work_order", ParentMessageId = message, Delta = "{\"workOrderId\":" };
        yield return new ToolCallChunkEvent { Delta = "\"WO-1001\"}" };

        // show_work_order is a frontend tool: the run ends with the call unanswered, and says so.
        yield return run.Finished(new RunFinishedSuccessOutcome { PendingToolCallIds = [call] });
    }

    // The run stops to ask: RUN_FINISHED with an interrupt outcome naming the call to approve.
    public static IEnumerable<object> Interrupt(TourRun run)
    {
        var message = run.Id("msg");
        var call = run.Id("call");

        yield return run.Started();

        foreach (var evt in Say(message,
            "CMP-CA36 costs $1,795.00, which is over the $500 approval limit, so a dispatcher has to approve this order."))
        {
            yield return evt;
        }

        yield return new ToolCallStartEvent { ToolCallId = call, ToolCallName = "place_part_order", ParentMessageId = message };
        yield return new ToolCallArgsEvent { ToolCallId = call, Delta = """{"workOrderId":"WO-1001","sku":"CMP-CA36","quantity":1}""" };
        yield return new ToolCallEndEvent { ToolCallId = call };
        yield return run.Finished(new RunFinishedInterruptOutcome
        {
            Interrupts =
            [
                new AGUIInterrupt
                {
                    Id = run.Id("approval"),
                    Reason = InterruptReasons.ToolCall,
                    Message = "Approve ordering 1 x CMP-CA36 ($1,795.00) for WO-1001?",
                    ToolCallId = call,
                    ResponseSchema = Json("""{"type":"object","properties":{"approved":{"type":"boolean"}},"required":["approved"]}"""),
                    ExpiresAt = "2026-11-16T17:00:00-05:00",
                },
            ],
        });
    }

    // The dispatcher pressed Stop: everything opened is closed, then RUN_FINISHED says "cancelled".
    public static IEnumerable<object> Cancelled(TourRun run)
    {
        yield return run.Started();
        yield return new StepStartedEvent { StepName = "draft-report" };

        foreach (var evt in Say(run.Id("msg"), "Drafting the WO-1001 service report: arrived 9:05, unit short-cycling,"))
        {
            yield return evt;
        }

        yield return new StepFinishedEvent { StepName = "draft-report" };
        yield return run.Finished(new RunFinishedCancelledOutcome(),
        [
            new TokenUsage { Provider = "beacon", Model = "beacon-scripted", InputTokens = 1102, OutputTokens = 14, TotalTokens = 1116 },
        ]);
    }

    // The run fails after it started: RUN_ERROR ends it, open message and all.
    public static IEnumerable<object> Error(TourRun run)
    {
        var message = run.Id("msg");

        yield return run.Started();
        yield return new TextMessageStartEvent { MessageId = message, Role = AGUIRoles.Assistant };
        yield return new TextMessageContentEvent { MessageId = message, Delta = "Checking the parts inventory for " };
        yield return new RunErrorEvent { Message = "The parts inventory service did not respond.", Code = "inventory_unavailable" };
    }

    // Deliberate violations, for the validator to find. Two frames are hand-written JSON because the
    // tour's serializer can't produce them. The banned null is parentRunId, which no compatibility
    // shim repairs (DEPRECATIONS.md), so every 1.0 consumer must reject it.
    public static IEnumerable<object> Broken(TourRun run)
    {
        var message = run.Id("msg");
        var call = run.Id("call");

        // No protocolVersion, and a null standing in for an absent parentRunId.
        yield return $$"""{"type":"RUN_STARTED","threadId":{{Quote(run.ThreadId)}},"runId":{{Quote(run.RunId)}},"parentRunId":null}""";
        yield return new TextMessageContentEvent { MessageId = message, Delta = "Looking up " }; // Not open yet.
        yield return new TextMessageStartEvent { MessageId = message, Role = AGUIRoles.Assistant };
        yield return $$"""{"type":"TEXT_MESSAGE_CONTENT","messageId":{{Quote(message)}},"delta":42}""";
        yield return new ToolCallStartEvent { ToolCallId = call, ToolCallName = "get_work_order", ParentMessageId = message };
        yield return new ToolCallArgsEvent { ToolCallId = call, Delta = """{"workOrderId":"WO-1001"}""" };
        yield return new RunFinishedEvent { ThreadId = run.ThreadId, RunId = run.RunId }; // Text and tool call still open.
        yield return new TextMessageEndEvent { MessageId = message }; // After the run closed.
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value, AGUIJsonSerializerContext.Default.String);
}

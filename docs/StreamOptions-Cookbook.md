# `AGUIStreamOptions` Cookbook

**An online appendix to *AG-UI in .NET*.** The book's chapters refer to it as the online *AGUIStreamOptions Cookbook*; its code is in [`Online-StreamOptions-Cookbook/`](../Online-StreamOptions-Cookbook/), and every path below is relative to the root of the companion repository.

*`AGUI.Server` converts `ChatResponseUpdate`s into AG-UI events on its own for text, reasoning, tool calls, approvals, interrupts and usage. `AGUIStreamOptions` is where you change that conversion: map content your pipeline invents, raise interrupts from it, act on a complete tool call or its result, stream arguments as they're written and label token usage. Here is one recipe per hook, plus the `RawRepresentation` pass-through that needs no hook at all, each with the problem it solves, a compiled listing, the frames it puts on the wire and the pitfalls the book's tests have pinned down.*

> **Companion code:** [`Online-StreamOptions-Cookbook/`](../Online-StreamOptions-Cookbook/) contains [`StreamOptionsCookbook`](../Online-StreamOptions-Cookbook/StreamOptionsCookbook/), one static recipe class per hook, and [`StreamOptionsCookbook.Tests`](../Online-StreamOptions-Cookbook/StreamOptionsCookbook.Tests/), 33 tests that host each recipe behind the book's `MapAGUIEndpoint` with a recorded model and assert the frames on the wire. From the repository root (the book's `sample-code` folder), run them with
> `dotnet test --project Online-StreamOptions-Cookbook/StreamOptionsCookbook.Tests`; set `STREAM_OPTIONS_CAPTURE_DIR` to a folder to get each recipe's frames as an `.sse` file.

Every recipe is a static `Apply(AGUIStreamOptions options)` that registers its mappers and returns the instance. The `Map*` methods are fluent and change the instance they're called on, so one `Apply` serves both the fresh instance the book's glue builds per request and the single instance Microsoft Agent Framework's `MapAGUIServer` reads from dependency injection (section 10). Recipes compose by nesting: `MapCallRecipe.Apply(StreamingArgumentsRecipe.Apply(options))`.

The hooks don't compose alike, so check Table 1 before you mix them.

**Table 1** — When each hook runs, and what registering it twice does

| Hook | Runs for | Its events go out | Registered twice |
|---|---|---|---|
| `MapInterrupt` | content the converter has no mapping for | in the run's one `RUN_FINISHED`, when the stream ends | tried in order; first non-null wins |
| `MapContent` | the same content, if no interrupt mapper claimed it | where the content was | tried in order; first non-null wins |
| `MapCall(tool, …)` | each complete `FunctionCallContent` for the tool | right after `TOOL_CALL_END` | one per tool; the last replaces |
| `MapResult(tool, …)` | each `FunctionResultContent` for the tool | right after `TOOL_CALL_RESULT` | one per tool; the last replaces |
| `MapResultAsStateSnapshot`, `MapResultAsStateDelta` | as `MapResult`, which they call | right after `TOOL_CALL_RESULT` | share `MapResult`'s slot |
| `MapStreamingToolCallArguments` | every update that isn't a raw event | as `TOOL_CALL_START` and `TOOL_CALL_ARGS` | one extractor; the last replaces |
| `WithUsageProvider` | the usage summed from `UsageContent` | on `RUN_FINISHED` or `RUN_ERROR` | the last label wins |

Two rules hold throughout. Mapped events go out as you built them, with no `rawEvent`, validation or ID checks, so Chapter 3's ordering rules are yours to keep. And `MapContent` and `MapInterrupt` never see text, reasoning, function calls or results, approval requests, `InterruptRequestContent` or `UsageContent`; the converter handles those itself.

Each wire listing was captured by the test named in its caption, over ASP.NET Core's in-memory test server, with bulky `rawEvent` payloads collapsed to `"rawEvent":{…}` unless the payload is the point.

## 1 `MapContent`: Your Own Content as Events

Section 13.4 named the alternative to writing AG-UI events in a pipeline layer: the layer yields an `AIContent` type of its own and the endpoint decides how it looks on the wire, so the layer stays free of AG-UI types. Here `PhaseMarkingChatClient` brackets the run with `PhaseContent` markers, and the mapper turns them into steps.

**Listing 1** — Content of your own, and the mapper that turns it into steps ([`Online-StreamOptions-Cookbook/StreamOptionsCookbook/MapContentRecipe.cs`](../Online-StreamOptions-Cookbook/StreamOptionsCookbook/MapContentRecipe.cs))

```csharp
public sealed class PhaseContent : AIContent
{
    public required string Name { get; init; }

    public required bool Finished { get; init; }
}
// ...
    public static AGUIStreamOptions Apply(AGUIStreamOptions options) => options
        .MapContent(content => content switch
        {
            PhaseContent { Finished: false } phase => [new StepStartedEvent { StepName = phase.Name }],
            PhaseContent phase => [new StepFinishedEvent { StepName = phase.Name }],
            _ => null, // not ours: let the next mapper try, or drop it
        });
```

**Wire 1** — Phase markers as steps (`Phase_markers_become_step_events`)

```text
data: {"type":"RUN_STARTED","threadId":"thread-1","runId":"run-1"}

data: {"type":"STEP_STARTED","stepName":"diagnose"}

data: {"type":"TEXT_MESSAGE_START","messageId":"msg-1","role":"assistant","rawEvent":{…}}

data: {"type":"TEXT_MESSAGE_CONTENT","messageId":"msg-1","delta":"The run capacitor ","rawEvent":{…}}

data: {"type":"TEXT_MESSAGE_CONTENT","messageId":"msg-1","delta":"is the likely cause.","rawEvent":{…}}

data: {"type":"STEP_FINISHED","stepName":"diagnose"}

data: {"type":"TEXT_MESSAGE_END","messageId":"msg-1"}

data: {"type":"RUN_FINISHED","threadId":"thread-1","runId":"run-1","outcome":{"type":"success"}}

```

**Pitfalls**

- The endpoint must be able to serialize the content. `AGUI.Server` serializes every update for `rawEvent` before it reads the contents, so an unregistered type ends the run in `RUN_ERROR` (`StreamingError`) right after `RUN_STARTED`, and no mapper is asked. Register it in the endpoint's `JsonOptions` (Listing 2), as Chapter 15 does.
- Mapped events don't close an open text message: `STEP_FINISHED` lands before `TEXT_MESSAGE_END`. The specification allows it, because steps and messages are independent items.
- `null` passes the content to the next mapper, and drops it if none claims it. `[]` means "handled": nothing is written and the search ends.

**Listing 2** — Registering the recipes' content types with an endpoint's JSON options ([`Online-StreamOptions-Cookbook/StreamOptionsCookbook/CookbookJson.cs`](../Online-StreamOptions-Cookbook/StreamOptionsCookbook/CookbookJson.cs))

```csharp
    public static void RegisterContentTypes(JsonSerializerOptions options)
    {
        options.TypeInfoResolverChain.Insert(1, CookbookJsonContext.Default);
        options.AddAIContentType<PhaseContent>(MapContentRecipe.PhaseDiscriminator);
        options.AddAIContentType<ConfirmationRequestContent>(MapInterruptRecipe.ConfirmationDiscriminator);
    }
```

The context goes in behind the AG-UI resolver that `AddAGUIHosting` put first, and the test host calls this from `builder.Services.Configure<JsonOptions>(...)`.

## 2 `MapInterrupt`: Your Own Request as an Interrupt

A pipeline layer has decided that Dana must confirm a reassignment before Beacon goes on. As in Chapter 15, the layer deals in records and the endpoint turns a record into an interrupt, so the interrupt can't say anything the server didn't store.

**Listing 3** — The interrupt is built from the server's record ([`Online-StreamOptions-Cookbook/StreamOptionsCookbook/MapInterruptRecipe.cs`](../Online-StreamOptions-Cookbook/StreamOptionsCookbook/MapInterruptRecipe.cs))

```csharp
public sealed class ConfirmationRequestContent : AIContent
{
    public required string ConfirmationId { get; init; }

    [JsonIgnore]
    public PendingConfirmation? Request { get; init; }
}
// ...
    public static AGUIStreamOptions Apply(AGUIStreamOptions options) => options
        .MapInterrupt(content => content is ConfirmationRequestContent { Request: { } request }
            ? new AGUIInterrupt
            {
                Id = request.Id,
                Reason = InterruptReasons.Confirmation,
                Message = request.Question,
                ResponseSchema = ConfirmSchema,
                ExpiresAt = request.ExpiresAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            }
            : null);
```

The record is `[JsonIgnore]`, and Wire 2 shows why: the update that carried the request closed the text message, so the content went out as that frame's `rawEvent`, ID only.

**Wire 2** — A confirmation request as an interrupt outcome; the request's own update closed the text message (`A_confirmation_request_becomes_an_interrupt_outcome`)

```text
data: {"type":"RUN_STARTED","threadId":"thread-1","runId":"run-1"}

data: {"type":"TEXT_MESSAGE_START","messageId":"msg-1","role":"assistant","rawEvent":{…}}

...

data: {"type":"TEXT_MESSAGE_END","messageId":"msg-1","rawEvent":{"authorName":null,"role":"assistant","contents":[{"$type":"beaconConfirmation","confirmationId":"cnf_8f2c41d07a9e4b6c","annotations":null,"additionalProperties":null}],"additionalProperties":null,"responseId":null,"messageId":"msg-1","conversationId":null,"createdAt":null,"finishReason":null,"modelId":"beacon-scripted","continuationToken":null}}

data: {"type":"RUN_FINISHED","threadId":"thread-1","runId":"run-1","outcome":{"type":"interrupt","interrupts":[{"id":"cnf_8f2c41d07a9e4b6c","reason":"confirmation","message":"Move WO-1001 from Priya Raman to Marcus Bell at 13:00?","responseSchema":{"type":"object","properties":{"confirmed":{"type":"boolean"}},"required":["confirmed"],"additionalProperties":false},"expiresAt":"2026-11-16T09:30:00Z"}]}}

```

**Pitfalls**

- The run doesn't stop at the interrupt. The converter collects interrupts and writes them in one `RUN_FINISHED` when the update stream ends, and text the pipeline sends after the request still goes out first. The SDK's documentation says the run terminates; in fact stopping is the raising layer's job, as in Chapter 15.
- Content an interrupt mapper claims never reaches `MapContent`, and section 1's registration applies here too.
- Leave `toolCallId` off unless the interrupt is a tool approval; `AGUIChatClient` reads reason `tool_call` plus a `toolCallId` as one (Chapter 15).
- The mapper only asks. Checking the answer against `responseSchema` and `expiresAt`, once, is the resume gate's job (section 15.6).

## 3 `MapCall`: What a Tool Is About to Do

Predictive state shows a call's effect before the tool runs (Chapter 13). Here the part order appears on Dana's sheet as "placing" the moment the model's call is complete.

**Listing 4** — A complete call becomes a predictive state delta ([`Online-StreamOptions-Cookbook/StreamOptionsCookbook/MapCallRecipe.cs`](../Online-StreamOptions-Cookbook/StreamOptionsCookbook/MapCallRecipe.cs))

```csharp
    public static AGUIStreamOptions Apply(AGUIStreamOptions options) => options
        .MapCall(PlacePartOrderName, call =>
        [
            // Predictive state: the order as the model wrote it, sent after TOOL_CALL_END and before the
            // tool runs. It is display only; the tool validates the same arguments when it runs.
            new StateDeltaEvent
            {
                Delta = CookbookJson.ToElement(new JsonArray(new JsonObject
                {
                    ["op"] = "add",
                    ["path"] = "/pendingOrder",
                    ["value"] = new JsonObject
                    {
                        ["sku"] = CookbookJson.Argument(call, "sku"),
                        ["quantity"] = CookbookJson.Argument(call, "quantity"),
                        ["status"] = "placing",
                    },
                })),
            },
        ]);
```

`CookbookJson.Argument` serializes each argument rather than casting it: providers parse arguments into `JsonElement` values, hand-built calls may carry plain strings and numbers. The `\u0022` escapes in Wires 3 to 7 are System.Text.Json's defaults on the book's glue; `MapAGUIServer` writes `\"` instead (Wire 11).

**Wire 3** — The predictive delta sits between the call's end and its result (`A_complete_call_becomes_a_predictive_state_delta_between_its_end_and_its_result`)

```text
data: {"type":"TOOL_CALL_START","toolCallId":"call-1","toolCallName":"place_part_order","parentMessageId":"msg-1","rawEvent":{…}}

data: {"type":"TOOL_CALL_ARGS","toolCallId":"call-1","delta":"{\u0022workOrderId\u0022:\u0022WO-1001\u0022,\u0022sku\u0022:\u0022CAP-45-5\u0022,\u0022quantity\u0022:1}","rawEvent":{…}}

data: {"type":"TOOL_CALL_END","toolCallId":"call-1","rawEvent":{…}}

data: {"type":"STATE_DELTA","delta":[{"op":"add","path":"/pendingOrder","value":{"sku":"CAP-45-5","quantity":1,"status":"placing"}}]}

data: {"type":"TOOL_CALL_RESULT","messageId":"call-1","toolCallId":"call-1","content":"{\u0022orderId\u0022:\u0022PO-7001\u0022,\u0022workOrderId\u0022:\u0022WO-1001\u0022,\u0022sku\u0022:\u0022CAP-45-5\u0022,\u0022quantity\u0022:1,\u0022status\u0022:\u0022placed\u0022}","rawEvent":{…}}

...

```

**Pitfalls**

- `MapCall` sees complete calls only. With section 7's extractor registered, the arguments arrive in four pieces and the mapper still runs once, after `TOOL_CALL_END`. For previews while the model writes, combine section 7 with a delegating chat client, as Chapter 13's `ReportPreviewChatClient` does; the SDK's extensibility page suggests otherwise.
- Behind `FunctionInvokingChatClient`, a complete call arrives when the model's turn ends, and the mapper's events are written before the tool is invoked. Chapter 13 measured 12 milliseconds after `TOOL_CALL_END` and 1.2 seconds before the result.
- A call waiting for approval isn't mapped: with the tool wrapped in `ApprovalRequiredAIFunction`, the run ends with the call's three events and an interrupt.
- The arguments are the model's. Show them, never act on them; the tool validates them when it runs.

## 4 `MapResult`: A Result That Drives More Than Its Card

When `check_warranty` returns, the Desk's warranty badge should change too, without parsing tool results. The recipe adds a `CUSTOM` event the badge listens for.

**Listing 5** — A successful warranty result also becomes a `CUSTOM` event ([`Online-StreamOptions-Cookbook/StreamOptionsCookbook/MapResultRecipe.cs`](../Online-StreamOptions-Cookbook/StreamOptionsCookbook/MapResultRecipe.cs))

```csharp
    public static AGUIStreamOptions Apply(AGUIStreamOptions options) => options
        .MapResult(CheckWarrantyName, result =>
            result is { Exception: null, Result: JsonElement { ValueKind: JsonValueKind.Object } warranty }
                ? [new CustomEvent { Name = WarrantyCheckedEvent, Value = warranty }]
                : []); // a failed call: TOOL_CALL_RESULT already says so, and the badge keeps its last value
```

**Wire 4** — The result, then the event the badge listens for (`A_warranty_result_also_becomes_a_custom_event`)

```text
...

data: {"type":"TOOL_CALL_RESULT","messageId":"call-1","toolCallId":"call-1","content":"{\u0022equipmentId\u0022:\u0022EQ-501\u0022,\u0022covered\u0022:true,\u0022expires\u0022:\u00222029-05-14\u0022,\u0022terms\u0022:\u0022Parts: 10 years; labor: 2 years (labor expired)\u0022}","rawEvent":{…}}

data: {"type":"CUSTOM","name":"beacon.warranty_checked","value":{"equipmentId":"EQ-501","covered":true,"expires":"2029-05-14","terms":"Parts: 10 years; labor: 2 years (labor expired)"}}

...

```

**Pitfalls**

- Failures reach the mapper too. By default `FunctionInvokingChatClient` turns an exception into the string "Error: Function failed." with `Exception` set; the recipe checks both and maps a failure to nothing.
- Return `[]`, never `null`. Unlike `MapContent`, a `null` here ends the run in `RUN_ERROR` right after `TOOL_CALL_RESULT`.
- One mapper per tool: a second `MapResult`, or a state helper, for the same tool replaces the first.
- The converter pairs a result with its tool through calls it converted in the same response; a result whose call it never saw isn't mapped.

## 5 `MapResultAsStateSnapshot`: The Result Is the New State

Chapter 12's `open_work_order_sheet` returns the whole sheet, and the sheet is the shared state, so its result becomes `STATE_SNAPSHOT`.

**Listing 6** — The tool's result is the sheet, and the sheet is the state ([`Online-StreamOptions-Cookbook/StreamOptionsCookbook/StateSnapshotRecipe.cs`](../Online-StreamOptions-Cookbook/StreamOptionsCookbook/StateSnapshotRecipe.cs))

```csharp
    public static AIFunction OpenWorkOrderSheet { get; } = AIFunctionFactory.Create(
        ([Description("The work order to open, for example WO-1001.")] string workOrderId) => Open(workOrderId),
        OpenWorkOrderSheetName,
        "Opens a work order on the shared sheet and returns the whole sheet.",
        CookbookJson.ToolOptions);

    public static AGUIStreamOptions Apply(AGUIStreamOptions options) => options
        .MapResultAsStateSnapshot(OpenWorkOrderSheetName);

    // The result IS the new state, so there is no room for an error message in it. A work order the
    // tool can't open leaves the sheet as the client sent it.
    private static JsonElement Open(string workOrderId) => workOrderId switch
    {
        "WO-1001" => JsonSerializer.SerializeToElement(Wo1001, CookbookJsonContext.Default.WorkSheet),
        _ => CookbookJson.CurrentRunState(),
    };
```

**Wire 5** — The sheet as result and as snapshot (`A_tool_result_becomes_the_state_snapshot`)

```text
...

data: {"type":"TOOL_CALL_RESULT","messageId":"call-1","toolCallId":"call-1","content":"{\u0022workOrderId\u0022:\u0022WO-1001\u0022,\u0022problem\u0022:\u0022Air conditioner short-cycling and blowing warm air\u0022,\u0022checklist\u0022:[{\u0022id\u0022:\u0022C1\u0022,\u0022text\u0022:\u0022Check thermostat settings and wiring\u0022,\u0022done\u0022:false},{\u0022id\u0022:\u0022C2\u0022,\u0022text\u0022:\u0022Measure refrigerant pressures\u0022,\u0022done\u0022:false},{\u0022id\u0022:\u0022C3\u0022,\u0022text\u0022:\u0022Inspect run capacitor\u0022,\u0022done\u0022:false},{\u0022id\u0022:\u0022C4\u0022,\u0022text\u0022:\u0022Test compressor amperage\u0022,\u0022done\u0022:false}]}","rawEvent":{…}}

data: {"type":"STATE_SNAPSHOT","snapshot":{"workOrderId":"WO-1001","problem":"Air conditioner short-cycling and blowing warm air","checklist":[{"id":"C1","text":"Check thermostat settings and wiring","done":false},{"id":"C2","text":"Measure refrigerant pressures","done":false},{"id":"C3","text":"Inspect run capacitor","done":false},{"id":"C4","text":"Test compressor amperage","done":false}]}}

...

```

**Pitfalls**

- The result must be a `JsonElement`. `AIFunctionFactory` serializes every return value to one, records included, so a successful call qualifies. A failed call doesn't: its result is the string "Error: Function failed.", the helper throws inside the converter and the run ends in `RUN_ERROR` before the model hears about it (Wire 6). Keep mapped tools from throwing, or use `MapResult` with your own check.
- The result is the state, so it has no room for an error. The recipe answers a work order it can't open with the client's state, unchanged.
- The bytes go out twice, as the result the model reads and as the snapshot. With `AIJsonUtilities.DefaultOptions`, the result's copy is indented as well; `CookbookJson.ToolOptions` is compact.

**Wire 6** — A mapped tool that throws ends the run (`A_mapped_tool_that_throws_ends_the_run_in_RUN_ERROR`)

```text
...

data: {"type":"TOOL_CALL_END","toolCallId":"call-1","rawEvent":{…}}

data: {"type":"TOOL_CALL_RESULT","messageId":"call-1","toolCallId":"call-1","content":"Error: Function failed.","rawEvent":{…}}

data: {"type":"RUN_ERROR","message":"An error occurred while streaming the agent response.","code":"StreamingError"}

```

## 6 `MapResultAsStateDelta`: The Result Is a Patch

Ticking one checklist item shouldn't resend the sheet. The tool returns an RFC 6902 patch against the state the client sent with the run, and the helper sends it as `STATE_DELTA`.

**Listing 7** — A patch against the client's copy of the sheet ([`Online-StreamOptions-Cookbook/StreamOptionsCookbook/StateDeltaRecipe.cs`](../Online-StreamOptions-Cookbook/StreamOptionsCookbook/StateDeltaRecipe.cs))

```csharp
    private static JsonElement Patch(string itemId, bool done)
    {
        // The patch must apply to the document the client holds, which arrived as this run's state.
        var state = CookbookJson.CurrentRunState();
        if (state.TryGetProperty("checklist", out var checklist) && checklist.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in checklist.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id) && id.ValueEquals(itemId))
                {
                    if (item.TryGetProperty("done", out var current) && current.ValueKind == (done ? JsonValueKind.True : JsonValueKind.False))
                    {
                        break; // already so
                    }

                    // Pin the item's identity first: if the client's list has moved, the test fails and the
                    // whole patch is rejected instead of ticking the wrong line.
                    return CookbookJson.ToElement(new JsonArray(
                        new JsonObject { ["op"] = "test", ["path"] = $"/checklist/{index}/id", ["value"] = itemId },
                        new JsonObject { ["op"] = "replace", ["path"] = $"/checklist/{index}/done", ["value"] = done }));
                }

                index++;
            }
        }

        // Nothing to change, or nothing it could change: an empty patch, never an error object, because
        // whatever this returns goes out as STATE_DELTA.
        return CookbookJson.ToElement(new JsonArray());
    }
```

**Wire 7** — Ticking C3: the patch as result and as delta (`A_patch_result_becomes_a_state_delta_against_the_clients_state`)

```text
...

data: {"type":"TOOL_CALL_RESULT","messageId":"call-1","toolCallId":"call-1","content":"[{\u0022op\u0022:\u0022test\u0022,\u0022path\u0022:\u0022/checklist/2/id\u0022,\u0022value\u0022:\u0022C3\u0022},{\u0022op\u0022:\u0022replace\u0022,\u0022path\u0022:\u0022/checklist/2/done\u0022,\u0022value\u0022:true}]","rawEvent":{…}}

data: {"type":"STATE_DELTA","delta":[{"op":"test","path":"/checklist/2/id","value":"C3"},{"op":"replace","path":"/checklist/2/done","value":true}]}

...

```

**Pitfalls**

- The converter doesn't check that a delta is a patch. A tool that returns `{"error": …}` puts an object where the patch belongs, which the specification makes fatal for the client. Answer "nothing to change" with `[]`.
- The patch must apply to the client's document: compute it from the run's state and pin identity with a `test` operation (sections 12.4 and 12.5).
- Section 5's `JsonElement` trap applies unchanged.

## 7 `MapStreamingToolCallArguments`: Arguments as the Model Writes Them

Microsoft.Extensions.AI hands the pipeline a `FunctionCallContent` only when a call is complete. The pieces survive only in the provider's raw updates, so the endpoint supplies a provider-specific extractor; this one reads the OpenAI SDK's chat-completions chunks, which the OpenAI and Azure OpenAI providers produce (a Responses-API client surfaces a different raw type and needs its own case).

**Listing 8** — An extractor for the OpenAI SDK's streamed tool-call chunks ([`Online-StreamOptions-Cookbook/StreamOptionsCookbook/StreamingArgumentsRecipe.cs`](../Online-StreamOptions-Cookbook/StreamOptionsCookbook/StreamingArgumentsRecipe.cs))

```csharp
    public static AGUIStreamOptions Apply(AGUIStreamOptions options) => options
        .MapStreamingToolCallArguments(FromOpenAI);
// ...
    public static IEnumerable<AGUIToolCallArgumentFragment>? FromOpenAI(ChatResponseUpdate update) =>
        update.RawRepresentation is StreamingChatCompletionUpdate { ToolCallUpdates.Count: > 0 } chunk
            ? chunk.ToolCallUpdates.Select(piece => new AGUIToolCallArgumentFragment
            {
                Index = piece.Index,
                ToolCallId = piece.ToolCallId,
                FunctionName = piece.FunctionName,
                ArgumentsDelta = piece.FunctionArgumentsUpdate?.ToString() ?? string.Empty,
            })
            : null;
```

**Wire 8** — Three pieces of `check_warranty`'s arguments, as the model wrote them (`Each_OpenAI_argument_piece_becomes_its_own_TOOL_CALL_ARGS`)

```text
data: {"type":"TOOL_CALL_START","toolCallId":"call-1","toolCallName":"check_warranty","parentMessageId":"msg-1","rawEvent":{…}}

data: {"type":"TOOL_CALL_ARGS","toolCallId":"call-1","delta":"{\u0022equip","rawEvent":{…}}

data: {"type":"TOOL_CALL_ARGS","toolCallId":"call-1","delta":"mentId\u0022:\u0022EQ","rawEvent":{…}}

data: {"type":"TOOL_CALL_ARGS","toolCallId":"call-1","delta":"-501\u0022}","rawEvent":{…}}

data: {"type":"TOOL_CALL_END","toolCallId":"call-1","rawEvent":{…}}

...

```

**Pitfalls**

- One extractor counts, the last registered, so handle every provider you stream from in one function, as Chapter 9's does.
- The converter opens a call on its first piece, the only one carrying the ID and function name, and adds only `TOOL_CALL_END` when the coalesced call arrives.
- Ollama sends calls whole: one `TOOL_CALL_ARGS` per call. And because `FunctionInvokingChatClient` holds back the rest of a turn once it has a complete call, pieces stream live only from providers that report calls at the end of the turn, as OpenAI's client does (Chapter 9).
- `AGUIChatClient` doesn't surface `TOOL_CALL_ARGS`; read them through `WireEventTap` (Chapter 7). A preview is display only.

## 8 `WithUsageProvider`: Naming the Provider on Token Usage

The converter sums every `UsageContent` in the run onto the terminal event. The model label comes from each update's `ModelId`, but nothing names the provider, so the endpoint declares it, reading it, as Chapter 25 does, from the chat client's metadata.

**Listing 9** — The provider label, read from the chat client's metadata ([`Online-StreamOptions-Cookbook/StreamOptionsCookbook/UsageProviderRecipe.cs`](../Online-StreamOptions-Cookbook/StreamOptionsCookbook/UsageProviderRecipe.cs))

```csharp
    public static AGUIStreamOptions Apply(AGUIStreamOptions options, IChatClient chatClient) => options
        .WithUsageProvider(chatClient.GetService<ChatClientMetadata>()?.ProviderName);
```

**Wire 9** — Usage from two model calls, labeled (`Usage_from_every_model_call_is_summed_and_labeled`)

```text
...

data: {"type":"RUN_FINISHED","threadId":"thread-1","runId":"run-1","outcome":{"type":"success"},"usage":[{"provider":"beacon","model":"beacon-scripted","inputTokens":898,"outputTokens":49,"totalTokens":947}]}

```

**Pitfalls**

- Usage is summed per provider and model: the two calls behind Wire 9 reported 412 and 486 input tokens. Two models produce two entries, in order of first appearance, and without a label `provider` is omitted.
- A failed run still reports what it used, on `RUN_ERROR`.
- A count no call reported stays absent rather than becoming zero.

## 9 `RawRepresentation`: Events No Hook Produces

No `AIContent` maps to activity messages. A delegating chat client builds `ACTIVITY_SNAPSHOT` and `ACTIVITY_DELTA` itself and hands each to the converter in `ChatResponseUpdate.RawRepresentation`, which the converter writes verbatim.

**Listing 10** — Activity events built by a delegating chat client ([`Online-StreamOptions-Cookbook/StreamOptionsCookbook/RawRepresentationRecipe.cs`](../Online-StreamOptions-Cookbook/StreamOptionsCookbook/RawRepresentationRecipe.cs))

```csharp
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // One activity message per run, named after the run.
        var runId = options?.TryGetRunAgentInput(out var input) == true ? input.RunId : Guid.NewGuid().ToString("N");
        var messageId = $"activity-{runId}";

        yield return new ChatResponseUpdate
        {
            RawRepresentation = new ActivitySnapshotEvent
            {
                MessageId = messageId,
                ActivityType = ActivityType,
                Content = CookbookJson.ToElement(new JsonObject { ["label"] = label, ["status"] = "running" }),
            },
        };

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            yield return update;
        }

        yield return new ChatResponseUpdate
        {
            RawRepresentation = new ActivityDeltaEvent
            {
                MessageId = messageId,
                ActivityType = ActivityType,
                Patch = CookbookJson.ToElement(new JsonArray(
                    new JsonObject { ["op"] = "replace", ["path"] = "/status", ["value"] = "done" })),
            },
        };
    }
```

`builder.UseProgressActivity(label)` adds it above function invocation, so it brackets the whole run.

**Wire 10** — A progress activity around the answer (`Activity_events_from_a_delegating_client_are_written_verbatim`)

```text
data: {"type":"RUN_STARTED","threadId":"thread-1","runId":"run-1"}

data: {"type":"ACTIVITY_SNAPSHOT","messageId":"activity-run-1","activityType":"beacon.progress","content":{"label":"Checking parts for WO-1001","status":"running"}}

data: {"type":"TEXT_MESSAGE_START","messageId":"msg-1","role":"assistant","rawEvent":{…}}

data: {"type":"TEXT_MESSAGE_CONTENT","messageId":"msg-1","delta":"CAP-45-5 is in stock ","rawEvent":{…}}

data: {"type":"TEXT_MESSAGE_CONTENT","messageId":"msg-1","delta":"in Portsmouth.","rawEvent":{…}}

data: {"type":"ACTIVITY_DELTA","messageId":"activity-run-1","activityType":"beacon.progress","patch":[{"op":"replace","path":"/status","value":"done"}]}

data: {"type":"TEXT_MESSAGE_END","messageId":"msg-1"}

data: {"type":"RUN_FINISHED","threadId":"thread-1","runId":"run-1","outcome":{"type":"success"}}

```

**Pitfalls**

- An update carrying a raw event carries nothing else: its `Contents` are ignored, so text placed beside a `CustomEvent` never reaches the client.
- Verbatim means unchecked. IDs, patches and ordering are yours; the recipe names the activity after the run.
- Raw events don't close an open text message, hence `ACTIVITY_DELTA` before `TEXT_MESSAGE_END`.
- The converter still writes `RUN_STARTED` first, unless the raw event is a `RUN_STARTED`, which takes its place; Chapter 24 uses that to start the run before the model answers.
- The pass-through also works through a `ChatClientAgent` behind `MapAGUIServer` (`Raw_events_pass_through_an_Agent_Framework_agent_too`).

## 10 Supplying the Options on Both Hosting Paths

The book's `MapAGUIEndpoint` takes a factory and calls it for every request.

**Listing 11** — The book's glue asks for options on every request ([`Online-StreamOptions-Cookbook/StreamOptionsCookbook.Tests/Infrastructure/CookbookHost.cs`](../Online-StreamOptions-Cookbook/StreamOptionsCookbook.Tests/Infrastructure/CookbookHost.cs))

```csharp
        // The book's glue builds the stream options per request: a fresh AGUIStreamOptions for every run.
        app.MapAGUIEndpoint("/agent", endpoint =>
            endpoint.StreamOptions = _ => recipe?.Invoke(new AGUIStreamOptions()) ?? new AGUIStreamOptions());
```

A fresh instance per request lets a mapper keep per-run state, which is how Chapter 13 shares one predictive-state writer between its mappers and its pipeline. Agent Framework's `MapAGUIServer` (preview) has no options parameter; it reads `AGUIStreamOptions` from the endpoint's metadata or, failing that, from `IOptions<AGUIStreamOptions>`, so both registrations in Listing 12 work.

**Listing 12** — Two ways to give `MapAGUIServer` its options, and the JSON fix it needs ([`Online-StreamOptions-Cookbook/StreamOptionsCookbook.Tests/HostingPathTests.cs`](../Online-StreamOptions-Cookbook/StreamOptionsCookbook.Tests/HostingPathTests.cs))

```csharp
        await using var app = await StartAgentFrameworkAsync(
            services => services.Configure<AGUIStreamOptions>(options => MapResultRecipe.Apply(options)),
            endpoints => endpoints.MapAGUIServer("/agent", model.AsAIAgent(name: "beacon", tools: [MapResultRecipe.CheckWarranty])));
// ...
            endpoints => endpoints
                .MapAGUIServer("/agent", model.AsAIAgent(name: "beacon", tools: [MapResultRecipe.CheckWarranty]))
                .WithMetadata(MapResultRecipe.Apply(new AGUIStreamOptions())));
// ...
        builder.Services.AddAGUIServer();

        // AddAGUIServer appends AG-UI's resolver after ASP.NET Core's, which writes "parentRunId": null and
        // friends; put AG-UI's first (Chapter 5).
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AGUIJsonUtilities.DefaultTypeInfoResolver));
```

**Wire 11** — `MapResultRecipe` behind `MapAGUIServer` (`MapAGUIServer_reads_the_options_from_dependency_injection`)

```text
...

data: {"type":"TOOL_CALL_RESULT","messageId":"call-1","toolCallId":"call-1","content":"{\"equipmentId\":\"EQ-501\",\"covered\":true,\"expires\":\"2029-05-14\",\"terms\":\"Parts: 10 years; labor: 2 years (labor expired)\"}","rawEvent":{…}}

data: {"type":"CUSTOM","name":"beacon.warranty_checked","value":{"equipmentId":"EQ-501","covered":true,"expires":"2029-05-14","terms":"Parts: 10 years; labor: 2 years (labor expired)"}}

data: {"type":"TEXT_MESSAGE_START","messageId":"msg-2","role":"assistant","name":"beacon","rawEvent":{…}}

...

```

The relaxed escaping and the `name` on `TEXT_MESSAGE_START` are `MapAGUIServer`'s own (Chapter 5).

**Pitfalls**

- On `MapAGUIServer`, one instance serves every request. A mapper that counts the results it has seen reports 2 on the second run there, and 1 behind `MapAGUIEndpoint`. Keep mappers stateless on that path; the SDK documentation's `MapCall` example, which remembers the last document in a closure, is fine per request and wrong application-wide.
- Endpoint metadata replaces the options from dependency injection rather than merging with them.
- Mappers are the only code of yours on the output side of `MapAGUIServer`. On the input side, an endpoint filter on the builder it returns runs after `RunAgentInput` is bound and can refuse the request or rewrite the input, but it runs outside the agent, so checks that need the server's own records or per-run state still need the book's `MapAGUIEndpoint` (Chapters 5 and 23).
- The test host has no authentication. A real `MapAGUIServer` endpoint needs `RequireAuthorization()` and, with a session store, an isolation key provider (Appendix B, section B.6; Chapter 23).
- `AddAGUIServer()` alone lets `MapAGUIServer` write `"parentRunId":null` and friends; insert `AGUIJsonUtilities.DefaultTypeInfoResolver` first, as Chapter 5 does.
- Both paths serialize with ASP.NET Core's `JsonOptions`, so section 1's registration goes in the same place on either.

## Further Reading

- [Extensibility](https://docs.ag-ui.com/sdk/dotnet/hosting/extensibility) — the SDK's page on `AGUIStreamOptions` and the pass-through; read its `MapCall` and `MapInterrupt` sections with sections 2 and 3 in mind.
- [`ChatResponseUpdateAGUIExtensions.cs`](https://github.com/ag-ui-protocol/ag-ui/blob/main/sdks/dotnet/src/AGUI.Server/ChatResponseUpdateAGUIExtensions.cs) — the converter that calls every hook; the clearest statement of when each one runs.
- [Streaming messages (spec 1.0)](https://docs.ag-ui.com/spec/1.0/basic/patterns/streaming) — why steps and activity events may arrive inside an open text message.
- [`AGUIDojoServer/ChatClientAgentFactory.cs`](https://github.com/ag-ui-protocol/ag-ui/blob/main/sdks/dotnet/samples/AGUIClientServer/AGUIDojoServer/ChatClientAgentFactory.cs) — the SDK's `AGUIDojoServer` sample using the state helpers, `MapCall` and an OpenAI extractor; its `MapCall` closure is safe only because its endpoint builds options per request.

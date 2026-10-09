# Migrating Pre-1.0 Code to AG-UI 1.0

**An online appendix to *AG-UI in .NET*.** The book's chapters refer to it as the online *Migration Guide*; section 3 is the book's reference for legacy nulls.

*AG-UI 1.0, released on 2026-09-17, retired a handful of wire shapes, and the .NET packages around it renamed or replaced several APIs on the way there. This appendix lists each change a .NET developer has to make: what it was, what replaces it, how to find it in your code, the fix and the deadline. It ends with a checklist and with what to expect, until the compatibility shims expire on 2027-09-17, from peers that haven't moved yet.*

Pre-1.0 .NET code comes from three places: Microsoft Agent Framework's AG-UI previews from November 2025 to July 2026, namely the client package `Microsoft.Agents.AI.AGUI` and the hosting package's `AddAGUI()` and `MapAGUI()`; the official SDK's `AGUI.*` 0.0.1 to 0.0.6, published in July and August 2026; and AG-UI written by hand against the 0.x shapes. A fourth source isn't yours to change: agents and clients in other languages that haven't upgraded.

The date that matters is 2027-09-17. `DEPRECATIONS.md` in the AG-UI repository records a shim for every retired shape, all expiring twelve months after 1.0, after which the next release may remove them. The shims live mostly in the TypeScript client, which translates retired shapes it receives and warns as it does; the Python SDK keeps two type aliases (`BinaryInputContent` and `SubAgentInfo`), and both SDKs alias the old content-part type names. The .NET SDK carries no shims. `AGUI.Client` 1.0.0 skips `THINKING_*` events, rejects `binary` parts and reads the listed nulls as absent without the warning a shim would give. So for a .NET consumer the reasoning and attachment deadlines have already passed, and the null leniency is undocumented behavior that a later release may tighten. For a .NET producer, 2027-09-17 is the day other clients stop forgiving it. Table 1 summarizes the changes.

**Table 1** — The changes at a glance

| Change | Before 1.0 | In 1.0 | Deadline or status | Section |
|---|---|---|---|---|
| Reasoning events | `THINKING_*` | `REASONING_*`, written from `TextReasoningContent` | Shims expire 2027-09-17; .NET skips the old events now | 1 |
| Media input | `{ "type": "binary" }`, `AGUIBinaryInputContent` | `image`, `audio`, `video` and `document` parts with a `source` | Shims expire 2027-09-17; .NET rejects the old part now | 2 |
| Optional fields | Sent as `null` | Omitted | Shims expire 2027-09-17; .NET reads them leniently, without warning | 3 |
| Client package | `Microsoft.Agents.AI.AGUI` | `AGUI.Client` | Last release 2026-07-03 | 4 |
| Hosting names | `AddAGUI()`, `MapAGUI()` | `AddAGUIServer()`, `MapAGUIServer()` | Gone since 2026-07-21 | 5 |
| Version declaration | None | `protocolVersion` on the input and on `RUN_STARTED` | Required by 1.0 | 6 |
| Type renames | `AgentThread`, `SubAgentInfo` and others | `AgentSession`, `SubagentInfo` and others | Compile errors now | 7 |

The C# examples were compiled in a scratch project, not in the companion code: the "before" code against the November 2025 previews it was written for, the "after" code against this book's pins. That's why they're labeled examples rather than listings.

## 1 `THINKING_*` Becomes `REASONING_*`

Before 1.0, reasoning traveled as five `THINKING_*` events. The reasoning family replaces them (Table 2) and adds what the old shapes lacked: message IDs that tie content to its message, a fixed `reasoning` role and `REASONING_ENCRYPTED_VALUE` for reasoning a provider keeps sealed (Chapter 16).

**Table 2** — Retired reasoning events and their replacements

| Before 1.0 | In 1.0 | What a translation has to do |
|---|---|---|
| `THINKING_START` | `REASONING_START` | Mint a `messageId` for the span; drop `title`, which has no counterpart |
| `THINKING_TEXT_MESSAGE_START` | `REASONING_MESSAGE_START` | Mint a `messageId`; add `role: "reasoning"` |
| `THINKING_TEXT_MESSAGE_CONTENT` | `REASONING_MESSAGE_CONTENT` | Use the open message's `messageId` |
| `THINKING_TEXT_MESSAGE_END` | `REASONING_MESSAGE_END` | Use the open message's `messageId` |
| `THINKING_END` | `REASONING_END` | Use the span's `messageId` |

No .NET package ever wrote the old names. `AGUI.*` 0.0.x has no classes for them, and Agent Framework's packages went from no reasoning support in the November 2025 previews straight to `REASONING_*` by 1.13. So `THINKING_*` in .NET code means events written by hand. The fix is to stop writing reasoning events yourself: yield `new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent(text)])`, with sealed reasoning in `ProtectedData`, and let `AGUI.Server` write the events (Example 1).

**Example 1** — The same reasoning before 1.0 (the hand-written fixture in Chapter 19's `LegacyPeerTests`) and as `AGUI.Server` 1.0.0 writes it from `TextReasoningContent`

```text
data: {"type":"THINKING_START","title":"Checking the unit"}

data: {"type":"THINKING_TEXT_MESSAGE_START"}

data: {"type":"THINKING_TEXT_MESSAGE_CONTENT","delta":"Short-cycling usually means the compressor trips."}

data: {"type":"THINKING_TEXT_MESSAGE_END"}

data: {"type":"THINKING_END"}

data: {"type":"REASONING_START","messageId":"msg_O0lwF8EPV1zEWZgq5GkcGOA6"}

data: {"type":"REASONING_MESSAGE_START","messageId":"msg_O0lwF8EPV1zEWZgq5GkcGOA6","role":"reasoning"}

data: {"type":"REASONING_MESSAGE_CONTENT","messageId":"msg_O0lwF8EPV1zEWZgq5GkcGOA6","delta":"Short-cycling usually means the compressor trips."}

data: {"type":"REASONING_MESSAGE_END","messageId":"msg_O0lwF8EPV1zEWZgq5GkcGOA6"}

data: {"type":"REASONING_END","messageId":"msg_O0lwF8EPV1zEWZgq5GkcGOA6"}

```

**Find it:** search for `THINKING_` in code and in any event names you build. **Deadline:** the TypeScript client translates incoming `THINKING_*` until 2027-09-17; `AGUI.Client` never did, so a .NET client already loses that reasoning (section 9).

## 2 `binary` Parts Become Media Parts

Before 1.0, every attachment was one part type, `binary`, with a `mimeType` and `data`, `url` or `id`, plus an optional `filename`; `AGUI.Abstractions` 0.0.x modeled it as `AGUIBinaryInputContent`, with the constant `AGUIInputContentTypes.Binary`. In 1.0 a part is named by its media (`image`, `audio` or `video` from the MIME type's top level, `document` for everything else), carries a `source` of type `data`, `url` or the new `file` (a provider's file handle) and keeps the file name in `metadata` (Example 2). A part that carried only an `id` has no 1.0 equivalent.

**Example 2** — The same nameplate photo as a pre-1.0 part and as `AGUIChatClient` 1.0.0 sends it

```json
{"type":"binary","mimeType":"image/png","data":"iVBORw0KGgo=","filename":"nameplate.png"}
{"type":"image","source":{"type":"data","value":"iVBORw0KGgo=","mimeType":"image/png"},"metadata":{"filename":"nameplate.png"}}
```

Don't build parts by hand in .NET. Put a `DataContent` or `UriContent` on the `ChatMessage`, with `Name` for the file name, and `AGUIChatClient` writes the second form; on the server, `ToChatRequestContext` turns it back into `DataContent` (Chapter 17). The old shape fails hard in 1.0 .NET. Any `binary` part makes deserialization throw `JsonException` ("Unknown ContentPart type: 'binary'"), which Chapter 4's glue answers with a 400, and a `MESSAGES_SNAPSHOT` carrying one ends the client's stream with the same exception, losing the rest of the run.

**Find it:** `AGUIBinaryInputContent`, `AGUIInputContentTypes.Binary` and `"binary"` in JSON you build. **Deadline:** translated by the TypeScript client until 2027-09-17; never in .NET.

## 3 Optional Fields Are Omitted, Never `null`

0.x producers often wrote `null` for an optional field with no value; 1.0 says absent means absent. `DEPRECATIONS.md` lists the nulls the TypeScript client forgives until 2027-09-17: `parentMessageId` on `TOOL_CALL_START` and `TOOL_CALL_CHUNK`, `outcome` and `result` on `RUN_FINISHED`, `result` on `SUBAGENT_FINISHED`, `rawEvent` on any event, a resume entry's `payload`, `metadata` on media parts, a tool's `parameters` and `forwardedProps`. Two were never forgiven, `metadata: null` on an event or message and `parentRunId: null`. A `null` that is data stays valid: `CUSTOM.value`, values inside state and metadata, and a patch's `value` (the online *JSON Patch Recipes*).

In .NET the producer side is where this bites. The source-generated resolver in `AGUI.Abstractions`, `AGUIJsonUtilities.DefaultTypeInfoResolver`, omits unset members, but only if it comes before any reflection-based resolver in the chain. `AddAGUIServer()` in the 1.23.0-preview.260928.1 hosting package appends it after ASP.NET Core's, so `MapAGUIServer` writes `"parentRunId":null` and its siblings unless you insert the resolver first, as Example 4 does (Chapter 5 found this). The consumer side forgives silently: `AGUIChatClient` 1.0.0 reads `"outcome":null`, `"result":null` and `"rawEvent":null` as absent, without a warning, so a .NET client won't tell you that its server sends them.

**Find it:** assert on the raw `data:` payloads your endpoint writes, never on re-serialized events (Chapter 4's `RawFrames` and `AGUIWire.NullProperties`). **Deadline:** after 2027-09-17, a TypeScript client may reject your nulls.

## 4 `Microsoft.Agents.AI.AGUI` Becomes `AGUI.Client`

Agent Framework shipped the first .NET AG-UI client, `Microsoft.Agents.AI.AGUI`, from November 2025 until its last release, 1.13.0-preview.260703.1 on 2026-07-03, a day after `AGUI.Client` 0.0.1 appeared. The constructor change is mechanical (Example 3); the behavior changes are not.

**Example 3** — A turn with the superseded client (`Microsoft.Agents.AI.AGUI` 1.0.0-preview.251110.1), then with `AGUI.Client` 1.0.0

```csharp
// Before
IChatClient beacon = new AGUIChatClient(http, "http://localhost:5120/agent");
var options = new ChatOptions { ConversationId = threadId }; // the thread to continue

history.Add(new ChatMessage(ChatRole.User,
[
    new TextContent("Tick C3 on WO-1001."),
    new DataContent(JsonSerializer.SerializeToUtf8Bytes(sheet), "application/json"), // became RunAgentInput.state
]));

await foreach (var update in beacon.GetStreamingResponseAsync(history, options))
{
    threadId = update.ConversationId; // the thread the server used
}
```

```csharp
// After
IChatClient beacon = new AGUIChatClient(new AGUIChatClientOptions(http, "http://localhost:5120/agent"));
var options = new ChatOptions // keep one instance per conversation
{
    RawRepresentationFactory = _ => new RunAgentInput { ThreadId = threadId ?? "", State = sheet }, // "" lets AGUIChatClient mint one on the first turn
};

history.Add(new ChatMessage(ChatRole.User, "Tick C3 on WO-1001.") { MessageId = Guid.NewGuid().ToString("N") });

await foreach (var update in beacon.GetStreamingResponseAsync(history, options))
{
    if (update.RawRepresentation is RunStartedEvent started)
    {
        threadId = started.ThreadId; // ConversationId is always null now
    }
}
```

First, the thread. The old client stamped the thread ID onto every update's `ConversationId`, and code read it back from there. `AGUI.Client` always leaves `ConversationId` null, because a non-null value tells Agent Framework's wrappers that the service keeps the history, and they then send only new messages to a server that keeps nothing. Ported code that reads `update.ConversationId` gets null and starts a new thread every turn. Take the thread from `RunStartedEvent` (or `AdditionalProperties["agui_thread_id"]`), reuse one `ChatOptions` or set it with `RawRepresentationFactory`; a `ConversationId` you set on `ChatOptions` is still accepted as the outgoing thread ID.

Second, state. The old client lifted a `DataContent` of type `application/json` out of the last message into `RunAgentInput.state`. `AGUI.Client` sends the same content as a `document` part, so the state reaches the model as an attachment and the agent sees no state at all. Pass state through `RawRepresentationFactory`, or `ConversationOptions.SendState` in `Beacon.Client.Core`. Third, set `MessageId` on every message you send, because a missing one goes out as `"id":""`.

**Find it:** package references and `using` directives for `Microsoft.Agents.AI.AGUI`, reads of `.ConversationId` and JSON `DataContent` used as state. **Deadline:** none was announced, but the package gets no updates and predates interrupts, resume and `protocolVersion`.

## 5 `AddAGUI()` and `MapAGUI()` Become `AddAGUIServer()` and `MapAGUIServer()`

`Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` exposed `services.AddAGUI()` and `app.MapAGUI(pattern, agent)` from 1.0.0-preview.251107.1 through 1.13.0-preview.260703.1. In 1.14.0-preview.260721.1 (2026-07-21) the package moved onto `AGUI.Server` and the names became `AddAGUIServer()` and `MapAGUIServer()`, with the three overloads the late previews had: an agent instance, a named agent from dependency injection or an `IHostedAgentBuilder`. The package is still a preview. Don't confuse either pair with the `AddAGUI()` and `MapAGUI()` in the AG-UI .NET docs and GettingStarted samples, which are helpers from `AGUI.Samples.Shared` and not package APIs, and don't copy from `integrations/microsoft-agent-framework/dotnet` in the AG-UI repository, which still pins 1.0.0-preview.251110.1.

**Example 4** — Hosting an agent with the November 2025 preview, then with 1.23.0-preview.260928.1

```csharp
// Before
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAGUI();
var app = builder.Build();

IChatClient model = new StubChatClient();
AIAgent beacon = model.CreateAIAgent(instructions: "You are Beacon.", name: "beacon");
app.MapAGUI("/agent", beacon);
```

```csharp
// After
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAGUIServer();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AGUIJsonUtilities.DefaultTypeInfoResolver)); // no nulls on the wire
var app = builder.Build();

IChatClient model = new StubChatClient();
AIAgent beacon = model.AsAIAgent(instructions: "You are Beacon.", name: "beacon");
app.MapAGUIServer("/agent", beacon);
```

Two things change underneath the names. The resolver insertion is the fix from section 3. And run data moved: the old endpoint copied state, context, forwarded properties and the thread and run IDs into `ChatOptions.AdditionalProperties` under `ag_ui_state`, `ag_ui_context`, `ag_ui_forwarded_properties`, `ag_ui_thread_id` and `ag_ui_run_id`. The new one sets none of them, so middleware that reads `ag_ui_state` silently gets nothing. Read the whole input with `options.TryGetRunAgentInput(out var input)` or typed state with `TryGetRunAgentState` (Chapter 12). Stream options now come from endpoint metadata or `IOptions<AGUIStreamOptions>` (Chapter 18), and persisted sessions bring the isolation rules of Chapters 5 and 23.

**Find it:** `AddAGUI(`, `MapAGUI(` and `"ag_ui_`. **Deadline:** no release since 2026-07-21 has the old names.

## 6 Declaring `protocolVersion`

0.x carried no version, and `AGUI.*` 0.0.x has no property for one. In 1.0 each side declares the version it speaks, the client on `RunAgentInput.protocolVersion` and the producer on `RUN_STARTED.protocolVersion`, and absence identifies a pre-1.0 peer. `AGUIChatClient` 1.0.0 sends `"1.0"` by itself. `AGUI.Server` 1.0.0 and `MapAGUIServer` don't stamp `RUN_STARTED`, so the SDK path sets the glue's `AGUIEndpointOptions.ProtocolVersion` (Chapter 19), an Agent Framework agent can yield its own `RunStartedEvent` from its pipeline (Chapter 19's `RunStartedDeclarationChatClient`), and a hand-built producer sets `ProtocolVersion = AGUIProtocol.Version`.

**Find it:** Chapter 3's validator reports `VER001` for a stream that doesn't declare. **Deadline:** none, because this isn't a deprecation: 1.0 makes the declaration a requirement.

## 7 Renamed Types in Agent Framework, Microsoft.Extensions.AI and `AGUI.*`

The remaining changes are renames, and the compiler finds them (Table 3): Agent Framework renamed its core types before its 1.0 release, `Microsoft.Extensions.AI` renamed its approval content in 10.4 and `AGUI.Abstractions` 1.0.0 follows the 1.0 schema.

**Table 3** — Renames the compiler will find for you

| Before | Now | Where |
|---|---|---|
| `AgentThread`, `agent.GetNewThread()` | `AgentSession`, `await agent.CreateSessionAsync()` | Agent Framework 2025 previews |
| `agent.DeserializeThread(...)` | `await agent.DeserializeSessionAsync(...)`, with `SerializeSessionAsync` | Agent Framework 2025 previews |
| `AgentRunResponse`, `AgentRunResponseUpdate` | `AgentResponse`, `AgentResponseUpdate` | Agent Framework 2025 previews |
| `ChatMessageStore`, `InMemoryChatMessageStore` | `ChatHistoryProvider`, `InMemoryChatHistoryProvider` | Agent Framework 2025 previews |
| `chatClient.CreateAIAgent(...)` | `chatClient.AsAIAgent(...)` | Agent Framework 2025 previews |
| `FunctionApprovalRequestContent`, `FunctionApprovalResponseContent` | `ToolApprovalRequestContent`, `ToolApprovalResponseContent` | `Microsoft.Extensions.AI` before 10.4 |
| `SubAgentInfo`, `MultiAgentCapabilities.SubAgents` | `SubagentInfo`, `Subagents` (wire key `subagents`) | `AGUI.Abstractions` 0.0.x |
| `AGUIUserContent`; `string` content on tool messages and `ToolCallResultEvent` | `AGUIContent` (a string still converts implicitly) | `AGUI.Abstractions` 0.0.x |
| `AgentCapabilities.Custom`, `IdentityCapabilities.Metadata` as dictionaries | `JsonElement?` | `AGUI.Abstractions` 0.0.x |
| `ExecutionCapabilities.MaxIterations`, `MaxExecutionTime` as `int?` | `long?` | `AGUI.Abstractions` 0.0.x |
| `AGUITool.Parameters`, `RunAgentInput.ForwardedProperties` as `JsonElement` | `JsonElement?` | `AGUI.Abstractions` 0.0.x |

One rename the compiler can't see: a capabilities document that still says `subAgents` has that key skipped as unknown. And one change is a new feature rather than a rename. The 0.x protocol had no interrupts, and neither Agent Framework package supported them, so approvals were improvised with `CUSTOM` events or confirmation tools. In 1.0 a run that needs a human ends with an interrupt outcome on `RUN_FINISHED` and the next run resumes it (Chapters 14 and 15).

## 8 A Migration Checklist

- [ ] Replace `Microsoft.Agents.AI.AGUI` with `AGUI.Client` 1.0.0 and build `AGUIChatClient` from `AGUIChatClientOptions`.
- [ ] Stop reading `ConversationId`; take the thread from `RunStartedEvent` and keep one `ChatOptions` per conversation.
- [ ] Send state through `RawRepresentationFactory` or `SendState`, not as a JSON `DataContent`, and set `MessageId` on every message.
- [ ] Move to hosting 1.14.0-preview.260721.1 or later, rename to `AddAGUIServer()` and `MapAGUIServer()` and put `AGUIJsonUtilities.DefaultTypeInfoResolver` first in the HTTP JSON options.
- [ ] Replace reads of `ag_ui_*` keys with `TryGetRunAgentInput` or `TryGetRunAgentState`.
- [ ] Emit reasoning as `TextReasoningContent` and delete hand-written `THINKING_*` events.
- [ ] Build attachments from `DataContent` and `UriContent` and delete `AGUIBinaryInputContent` and `"binary"`.
- [ ] Prove with a raw-frame test that no optional field goes out as `null`.
- [ ] Declare `protocolVersion` on `RUN_STARTED`.
- [ ] Fix the compile errors from Table 3 and rename `subAgents` in capability documents.
- [ ] Replace improvised approvals with interrupts and resume.
- [ ] List every peer that hasn't upgraded, with the date it will.

## 9 Talking to Pre-1.0 Peers Until 2027-09-17

A 1.0 .NET client meeting a pre-1.0 agent gets most of the run. Text, tool calls and state arrive, nulls are read as absent, and a `RUN_STARTED` without `protocolVersion` tells you the agent predates 1.0 (Chapter 19's `ProducerVersionCheck` reports it as undeclared). Two things are lost. `THINKING_*` events are skipped as unknown types with only a `Trace` warning, so the reasoning disappears (Chapter 19's `LegacyPeerTests`), and a `binary` part in a `MESSAGES_SNAPSHOT` ends the stream. The TypeScript client translates both until its shims expire; `AGUI.Client` translates neither.

Upgrading the agent is the real fix. If you can't, translate before deserialization, at the seam Chapter 19 names: an `IAGUIEventStreamFormatter` registered ahead of `SseEventStreamFormatter` in an `AGUIEventStreamHandler`, renaming events as in Table 2 and converting `binary` parts as in Example 2. A translation that loses content must warn, so log what doesn't survive, such as a span's `title`. Beacon has no pre-1.0 agents, so the book doesn't build that handler.

The other direction is gentler, with one trap. A pre-1.0 client ignores the event types 1.0 added, sends no `protocolVersion` (which a 1.0 agent must still serve; Chapter 19's gate logs it) and has its optional nulls read as absent. Its `binary` parts fail model binding before any endpoint filter runs, so the request gets a 400; converting them would take middleware that rewrites the raw body, and asking the client's team to upgrade is cheaper. The trap is interrupts. An old client doesn't know `outcome`, so a run that ends waiting for an approval looks to it like a run that succeeded, and nothing ever resumes it. For a client that doesn't declare a version, refuse consequential actions on the server instead of pausing for an approval it can't give.

Put 2027-09-17 in the plan. After it, the TypeScript client may stop translating too, and a pre-1.0 agent breaks for every client, not only the .NET ones.

## Further Reading

- [AG-UI deprecations](https://github.com/ag-ui-protocol/ag-ui/blob/main/DEPRECATIONS.md) — Every retired shape, its replacement, where its shim lives and the 2027-09-17 expiry.
- [Migrating to 1.0](https://docs.ag-ui.com/migrating-to-1-0) — The per-SDK changes; its .NET section covers the generated models, the retired `AGUIBinaryInputContent` and the type changes in Table 3.
- [Key changes (spec 1.0)](https://docs.ag-ui.com/spec/1.0/changelog) — What 1.0 changes against 0.x, from outcomes and subagents to "absent means absent".
- [Versioning and compatibility (spec 1.0)](https://docs.ag-ui.com/spec/1.0/basic/versioning) — Retired shapes, lossless and lossy downgrades and what each side owes an older peer.
- [AG-UI integration with Agent Framework](https://learn.microsoft.com/agent-framework/integrations/ag-ui/) — Microsoft's documentation for `AddAGUIServer` and `MapAGUIServer`.
- [Microsoft.Agents.AI.AGUI on NuGet](https://www.nuget.org/packages/Microsoft.Agents.AI.AGUI) — The superseded client's version history, ending at 1.13.0-preview.260703.1.
- [AGUI.Client on NuGet](https://www.nuget.org/packages/AGUI.Client) — The client that replaces it, from 0.0.1 to 1.0.0.

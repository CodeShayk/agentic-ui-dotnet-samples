using System.Text.Json;

namespace Ch03.StreamValidator;

/// <summary>
/// Verification: the rules that span events. Where a run is in its lifecycle, which messages, tool
/// calls, reasoning spans, steps and subagent invocations are open, who owns each one, and what must
/// be closed before the run may finish.
/// </summary>
internal sealed class RunVerifier(StreamValidatorOptions options)
{
    private enum Phase
    {
        BeforeFirstRun,
        Active,
        Finished,
        Errored,
    }

    private enum MessageKind
    {
        Text,
        Reasoning,
        ToolResult,
        Activity,
    }

    private sealed record Item(string Id, string? Owner, int OpenedAt)
    {
        public string? Role { get; init; }

        public string? Name { get; init; }

        public string? ToolCallName { get; init; }

        public string? ParentMessageId { get; init; }
    }

    // The stream as a whole.
    private Phase _phase = Phase.BeforeFirstRun;
    private string? _firstThreadId;
    private string? _lastRunId;
    private readonly HashSet<string> _runIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MessageKind> _messageKinds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activityIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _entityIds = new(options.KnownEntityIds, StringComparer.Ordinal);
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);
    private readonly List<(int Index, string Subtype, string EntityId)> _encryptedValues = [];

    // The current run. Run-scoped tracking does not cross a run boundary.
    private RunScope _run = new();

    public int RunCount { get; private set; }

    /// <summary>Who owns the open item with this id, for chunk expansion's lane resolution.</summary>
    public string? OwnerOfOpenItem(ItemKind kind, string id) => _run.Open(kind).TryGetValue(id, out var item) ? item.Owner : null;

    public void Apply(WireEvent evt, DiagnosticSink sink)
    {
        switch (evt.Type)
        {
            case "RUN_STARTED":
                StartRun(evt, sink);
                return;
            case "RUN_FINISHED":
                FinishRun(evt, sink);
                return;
            case "RUN_ERROR":
                FailRun(sink);
                return;
        }

        switch (_phase)
        {
            case Phase.BeforeFirstRun:
                sink.Report(Rules.FirstEvent, $"The stream opens with {evt.Type}; it must open with RUN_STARTED or RUN_ERROR.");
                _phase = Phase.Active; // Keep checking the rest as though a run had started.
                break;
            case Phase.Finished:
                sink.Report(Rules.AfterClose, $"The run already finished with RUN_FINISHED; only RUN_STARTED or RUN_ERROR may follow, not {evt.Type}.");
                return;
            case Phase.Errored:
                sink.Report(Rules.AfterClose, $"The run already failed with RUN_ERROR; only RUN_STARTED may follow, not {evt.Type}.");
                return;
        }

        CheckAttribution(evt, sink);

        switch (evt.Type)
        {
            case "TEXT_MESSAGE_START":
                StartItem(ItemKind.Text, evt, sink);
                break;
            case "TEXT_MESSAGE_CONTENT" or "TEXT_MESSAGE_END":
                ContinueItem(ItemKind.Text, evt, closes: evt.Type == "TEXT_MESSAGE_END", sink);
                break;
            case "TOOL_CALL_START":
                StartItem(ItemKind.ToolCall, evt, sink);
                break;
            case "TOOL_CALL_ARGS" or "TOOL_CALL_END":
                ContinueItem(ItemKind.ToolCall, evt, closes: evt.Type == "TOOL_CALL_END", sink);
                break;
            case "TOOL_CALL_RESULT":
                ToolResult(evt, sink);
                break;
            case "REASONING_MESSAGE_START":
                StartItem(ItemKind.Reasoning, evt, sink);
                break;
            case "REASONING_MESSAGE_CONTENT" or "REASONING_MESSAGE_END":
                ContinueItem(ItemKind.Reasoning, evt, closes: evt.Type == "REASONING_MESSAGE_END", sink);
                break;
            case "REASONING_START":
                if (!_run.Spans.TryAdd(evt.MessageId!, evt.Index))
                {
                    sink.Report(Rules.SpanAlreadyOpen, $"Reasoning span '{evt.MessageId}' is already open (since #{_run.Spans[evt.MessageId!]}).");
                }

                break;
            case "REASONING_END":
                if (!_run.Spans.Remove(evt.MessageId!))
                {
                    sink.Report(Rules.SpanNotOpen, $"No reasoning span '{evt.MessageId}' is open.");
                }

                break;
            case "REASONING_ENCRYPTED_VALUE":
                _encryptedValues.Add((evt.Index, Json.String(evt.Json, "subtype")!, Json.String(evt.Json, "entityId")!));
                break;
            case "STEP_STARTED":
                if (!_run.Steps.TryAdd((evt.Owner, evt.StepName!), evt.Index))
                {
                    sink.Report(Rules.StepAlreadyOpen, $"Step '{evt.StepName}' is already open for {OwnerName(evt.Owner)}.");
                }

                break;
            case "STEP_FINISHED":
                if (!_run.Steps.Remove((evt.Owner, evt.StepName!)))
                {
                    sink.Report(Rules.StepNotOpen, $"Step '{evt.StepName}' was not started by {OwnerName(evt.Owner)}, or is already finished.");
                }

                break;
            case "MESSAGES_SNAPSHOT":
                MessagesSnapshot(evt, sink);
                break;
            case "ACTIVITY_SNAPSHOT":
                ActivitySnapshot(evt, sink);
                break;
            case "ACTIVITY_DELTA":
                ActivityDelta(evt, sink);
                break;
            case "SUBAGENT_STARTED":
                SubagentStarted(evt, sink);
                break;
            case "SUBAGENT_FINISHED" or "SUBAGENT_ERROR":
                if (!_run.ActiveSubagents.Remove(evt.SubagentRunId!))
                {
                    sink.Report(Rules.SubagentNotActive,
                        $"Invocation '{evt.SubagentRunId}' is not active: it was never announced with SUBAGENT_STARTED, or it already ended.");
                }

                _run.FinishedSubagents.Add(evt.SubagentRunId!);
                break;
            case "CUSTOM":
                var name = Json.String(evt.Json, "name");
                if (name is not null && name.IndexOfAny(['.', ':', '/']) < 0)
                {
                    sink.Report(Rules.CustomNameUnprefixed,
                        $"'{name}' has no vendor or application prefix; unprefixed names are reserved for the protocol (try 'contoso.{name}').",
                        "/name");
                }

                break;
        }
    }

    // ---- Runs ---------------------------------------------------------------------------------

    private void StartRun(WireEvent evt, DiagnosticSink sink)
    {
        if (_phase == Phase.Active)
        {
            sink.Report(Rules.NestedRun, $"Run '{_run.RunId}' is still active; close it with RUN_FINISHED or RUN_ERROR before starting another.");
            return;
        }

        var threadId = Json.String(evt.Json, "threadId");
        var runId = Json.String(evt.Json, "runId");

        _phase = Phase.Active;
        _run = new RunScope { RunId = runId, ThreadId = threadId };
        RunCount++;
        _lastRunId = runId;

        if (runId is not null && !_runIds.Add(runId))
        {
            sink.Report(Rules.RunIdReused, $"runId '{runId}' was already used by an earlier run on this thread.", "/runId");
        }

        _firstThreadId ??= threadId;
        if (threadId is not null && threadId != _firstThreadId)
        {
            sink.Report(Rules.ThreadChanged, $"This run is on thread '{threadId}', but the stream began on '{_firstThreadId}'.", "/threadId");
        }

        CheckRequestedThread(threadId, sink);
        CheckVersion(evt.Json, sink);
    }

    private void CheckVersion(JsonElement runStarted, DiagnosticSink sink)
    {
        var version = Json.String(runStarted, "protocolVersion");
        if (version is null)
        {
            sink.Report(Rules.NoVersion,
                "A 1.0 producer declares the version it speaks on RUN_STARTED; absence identifies a pre-1.0 peer.", "/protocolVersion");
            return;
        }

        if (version == "1.0")
        {
            return;
        }

        var parts = version.Split('.');
        var message = parts.Length == 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor)
            ? major == 1 && minor > 0
                ? $"The producer speaks {version}, a newer minor of 1.x; a 1.0 consumer proceeds and drops what it does not recognize."
                : $"The producer speaks {version}, which is not the 1.0 line this validator checks."
            : $"'{version}' is not a MAJOR.MINOR version; a consumer treats it like a newer version and proceeds.";
        sink.Report(Rules.OtherVersion, message, "/protocolVersion");
    }

    private void FinishRun(WireEvent evt, DiagnosticSink sink)
    {
        switch (_phase)
        {
            case Phase.BeforeFirstRun:
                sink.Report(Rules.FirstEvent, "The stream opens with RUN_FINISHED; there is no run to finish.");
                _phase = Phase.Finished;
                return;
            case Phase.Finished:
                sink.Report(Rules.AfterClose, "The run already finished; a second RUN_FINISHED is not allowed.");
                return;
            case Phase.Errored:
                sink.Report(Rules.AfterClose, "The run already failed with RUN_ERROR; only RUN_STARTED may follow.");
                return;
        }

        var threadId = Json.String(evt.Json, "threadId");
        var runId = Json.String(evt.Json, "runId");
        if (_run.RunId is not null && runId is not null && runId != _run.RunId)
        {
            sink.Report(Rules.BoundaryMismatch, $"RUN_FINISHED names run '{runId}', but RUN_STARTED opened '{_run.RunId}'.", "/runId");
        }

        if (_run.ThreadId is not null && threadId is not null && threadId != _run.ThreadId)
        {
            sink.Report(Rules.BoundaryMismatch, $"RUN_FINISHED names thread '{threadId}', but RUN_STARTED named '{_run.ThreadId}'.", "/threadId");
        }

        CheckRequestedThread(threadId, sink);
        ReportOpenItems(sink, "at RUN_FINISHED");
        CheckOutcome(evt.Json, sink);

        _phase = Phase.Finished;
    }

    private void FailRun(DiagnosticSink sink)
    {
        switch (_phase)
        {
            case Phase.Errored:
                sink.Report(Rules.AfterClose, "The run already failed with RUN_ERROR; only RUN_STARTED may follow.");
                return;

            // RUN_ERROR may open a stream (the run failed before it began), end an active run, or
            // report a failure that surfaced after RUN_FINISHED. In every case it ends whatever was
            // still open, and nothing is expected to close it.
            default:
                _phase = Phase.Errored;
                return;
        }
    }

    private void CheckRequestedThread(string? threadId, DiagnosticSink sink)
    {
        if (options.ThreadId is not null && threadId is not null && threadId != options.ThreadId)
        {
            sink.Report(Rules.WrongThread, $"The request was for thread '{options.ThreadId}', but this event names '{threadId}'.", "/threadId");
        }
    }

    private void CheckOutcome(JsonElement runFinished, DiagnosticSink sink)
    {
        if (!runFinished.TryGetProperty("outcome", out var outcome) || outcome.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        switch (Json.String(outcome, "type"))
        {
            case "interrupt" when outcome.TryGetProperty("interrupts", out var interrupts) && interrupts.ValueKind == JsonValueKind.Array:
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var interrupt in interrupts.EnumerateArray())
                {
                    if (Json.String(interrupt, "id") is { } id && !seen.Add(id))
                    {
                        sink.Report(Rules.DuplicateInterruptId, $"Interrupt id '{id}' appears twice; a resume entry answers an interrupt by its id.", "/outcome/interrupts");
                    }
                }

                break;

            case "success" when outcome.TryGetProperty("pendingToolCallIds", out var pending) && pending.ValueKind == JsonValueKind.Array:
                var declared = pending.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String ? p.GetString()! : "?").ToList();
                var unanswered = _run.ToolCallsStarted.Where(id => !_run.ToolCallsAnswered.Contains(id)).ToList();
                if (declared.Count > 0 && !declared.SequenceEqual(unanswered))
                {
                    sink.Report(Rules.PendingMismatch,
                        $"pendingToolCallIds is [{string.Join(", ", declared)}], but the run left [{string.Join(", ", unanswered)}] unanswered.",
                        "/outcome/pendingToolCallIds");
                }

                break;

            case "cancelled" when runFinished.TryGetProperty("result", out _):
                sink.Report(Rules.CancelledWithResult, "A cancelled run has no return value; result should be absent.", "/result");
                break;
        }
    }

    private void ReportOpenItems(DiagnosticSink sink, string when)
    {
        foreach (var item in _run.OpenText.Values)
        {
            sink.Report(Rules.TextLeftOpen, $"Text message '{item.Id}' (opened at #{item.OpenedAt}) is still open {when}.");
        }

        foreach (var item in _run.OpenToolCalls.Values)
        {
            sink.Report(Rules.ToolLeftOpen, $"Tool call '{item.Id}' (opened at #{item.OpenedAt}) is still open {when}.");
        }

        foreach (var item in _run.OpenReasoning.Values)
        {
            sink.Report(Rules.ReasoningLeftOpen, $"Reasoning message '{item.Id}' (opened at #{item.OpenedAt}) is still open {when}.");
        }

        foreach (var (id, openedAt) in _run.Spans)
        {
            sink.Report(Rules.SpanLeftOpen, $"Reasoning span '{id}' (opened at #{openedAt}) is still open {when}.");
        }

        foreach (var ((owner, name), openedAt) in _run.Steps)
        {
            sink.Report(Rules.StepLeftOpen, $"Step '{name}' of {OwnerName(owner)} (started at #{openedAt}) is still open {when}.");
        }

        foreach (var id in _run.ActiveSubagents)
        {
            sink.Report(Rules.SubagentLeftActive, $"Invocation '{id}' is still active {when}; close it with SUBAGENT_FINISHED or SUBAGENT_ERROR.");
        }
    }

    /// <summary>End-of-stream checks: truncation, the requested run, and encrypted values that named nothing.</summary>
    public void Complete(DiagnosticSink sink)
    {
        if (_phase == Phase.BeforeFirstRun)
        {
            sink.Report(Rules.Truncated, "The stream ended without a single event; a run that never starts has no outcome.");
        }
        else if (_phase == Phase.Active)
        {
            sink.Report(Rules.Truncated,
                $"The stream ended while run '{_run.RunId ?? "(unnamed)"}' was active, with no RUN_FINISHED or RUN_ERROR. The run is truncated, not finished.");
            ReportOpenItems(sink, "when the stream ended");
        }

        if (options.RunId is not null && _lastRunId is not null && _lastRunId != options.RunId)
        {
            sink.Report(Rules.RequestedRunNotLast,
                $"The request asked for run '{options.RunId}', but the last run on the stream is '{_lastRunId}'.");
        }

        foreach (var (index, subtype, entityId) in _encryptedValues)
        {
            if (!_entityIds.Contains(entityId))
            {
                sink.Report(Rules.UnknownEntity,
                    $"REASONING_ENCRYPTED_VALUE at #{index} names {subtype} '{entityId}', which neither the stream nor the request created; a consumer has nowhere to store it.");
            }
        }
    }

    // ---- Streamed items: text messages, tool calls, reasoning messages --------------------------

    private void StartItem(ItemKind kind, WireEvent evt, DiagnosticSink sink)
    {
        var id = Id(kind, evt);
        var open = _run.Open(kind);

        if (open.TryGetValue(id, out var already))
        {
            var rule = kind switch
            {
                ItemKind.Text => Rules.TextAlreadyOpen,
                ItemKind.ToolCall => Rules.ToolAlreadyOpen,
                _ => Rules.ReasoningAlreadyOpen,
            };
            sink.Report(rule, $"'{id}' is already open (since #{already.OpenedAt}); close it before starting it again.");
            return;
        }

        var item = new Item(id, evt.Owner, evt.Index)
        {
            Role = kind == ItemKind.Text ? evt.Role ?? "assistant" : evt.Role,
            Name = evt.Name,
            ToolCallName = evt.ToolCallName,
            ParentMessageId = evt.ParentMessageId,
        };

        // A closed item is closed, not sealed: it may be reopened, but the reopening start must agree.
        if (_run.Closed(kind).TryGetValue(id, out var closed))
        {
            CheckReopen(kind, closed, item, sink);
        }

        if (kind == ItemKind.ToolCall)
        {
            CheckToolOwner(item, sink);
            if (!_run.ToolCallsStarted.Contains(id))
            {
                _run.ToolCallsStarted.Add(id);
            }

            _toolNames[id] = item.ToolCallName!;
        }
        else
        {
            Register(id, kind == ItemKind.Text ? MessageKind.Text : MessageKind.Reasoning, sink);
        }

        _entityIds.Add(id);
        open[id] = item;
    }

    private void ContinueItem(ItemKind kind, WireEvent evt, bool closes, DiagnosticSink sink)
    {
        var id = Id(kind, evt);
        var open = _run.Open(kind);

        if (!open.TryGetValue(id, out var item))
        {
            var rule = kind switch
            {
                ItemKind.Text => Rules.TextNotOpen,
                ItemKind.ToolCall => Rules.ToolNotOpen,
                _ => Rules.ReasoningNotOpen,
            };
            var what = kind switch
            {
                ItemKind.Text => "text message",
                ItemKind.ToolCall => "tool call",
                _ => "reasoning message",
            };
            sink.Report(rule, $"No {what} '{id}' is open; {evt.Type} has nothing to continue.");
            return;
        }

        // An untagged continuation continues whatever its opener owns; a tagged one must agree.
        if (evt.Owner is not null && evt.Owner != item.Owner)
        {
            sink.Report(Rules.OwnerMismatch,
                $"{evt.Type} is attributed to '{evt.Owner}', which does not match the {OwnerName(item.Owner)} that opened '{id}'.",
                "/subagentRunId");
        }

        if (closes)
        {
            open.Remove(id);
            _run.Closed(kind)[id] = item;
        }
    }

    private static void CheckReopen(ItemKind kind, Item closed, Item reopening, DiagnosticSink sink)
    {
        if (kind == ItemKind.Text && (closed.Role != reopening.Role || (reopening.Name is not null && closed.Name != reopening.Name)))
        {
            sink.Report(Rules.TextReopenDisagrees,
                $"Reopening '{closed.Id}' as role '{reopening.Role}', name '{reopening.Name}' disagrees with role '{closed.Role}', name '{closed.Name}'.");
        }

        if (kind == ItemKind.ToolCall && (closed.ToolCallName != reopening.ToolCallName || closed.ParentMessageId != reopening.ParentMessageId))
        {
            sink.Report(Rules.ToolReopenDisagrees,
                $"Reopening '{closed.Id}' as '{reopening.ToolCallName}' disagrees with the call it reopens ('{closed.ToolCallName}').");
        }
    }

    private void CheckToolOwner(Item call, DiagnosticSink sink)
    {
        // A tool call inherits the attribution of the message that holds it.
        if (call.Owner is null || call.ParentMessageId is null)
        {
            return;
        }

        var parent = _run.OpenText.GetValueOrDefault(call.ParentMessageId) ?? _run.ClosedText.GetValueOrDefault(call.ParentMessageId);
        if (parent is not null && parent.Owner != call.Owner)
        {
            sink.Report(Rules.ToolOwnerMismatch,
                $"Tool call '{call.Id}' is attributed to '{call.Owner}', but its parent message '{parent.Id}' belongs to {OwnerName(parent.Owner)}.");
        }
    }

    private void ToolResult(WireEvent evt, DiagnosticSink sink)
    {
        var callId = evt.ToolCallId!;
        if (_run.OpenToolCalls.ContainsKey(callId))
        {
            sink.Report(Rules.ResultBeforeEnd,
                $"Tool call '{callId}' is still open; its arguments are not complete, so nothing should have executed it yet.");
        }

        if (_toolNames.TryGetValue(callId, out var toolName) && options.FrontendTools.Contains(toolName))
        {
            sink.Report(Rules.FrontendToolAnswered,
                $"'{toolName}' is a frontend tool the client advertised; the producer must leave the call unanswered for the application.");
        }

        Register(evt.MessageId!, MessageKind.ToolResult, sink);
        _entityIds.Add(evt.MessageId!);
        _run.ToolCallsAnswered.Add(callId);
    }

    /// <summary>Message ids are unique within a thread, whatever kind of message they name.</summary>
    private void Register(string messageId, MessageKind kind, DiagnosticSink sink)
    {
        if (_messageKinds.TryGetValue(messageId, out var existing) && (existing != kind || kind == MessageKind.ToolResult))
        {
            sink.Report(Rules.MessageIdCollision,
                $"'{messageId}' already identifies a {Describe(existing)} message in this stream; message ids are unique within a thread.");
            return;
        }

        _messageKinds[messageId] = kind;
    }

    private static string Describe(MessageKind kind) => kind switch
    {
        MessageKind.Text => "text",
        MessageKind.Reasoning => "reasoning",
        MessageKind.ToolResult => "tool",
        _ => "activity",
    };

    private static string Id(ItemKind kind, WireEvent evt) => (kind == ItemKind.ToolCall ? evt.ToolCallId : evt.MessageId)!;

    // ---- Snapshots and activity ----------------------------------------------------------------

    private void MessagesSnapshot(WireEvent evt, DiagnosticSink sink)
    {
        if (!evt.Json.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var message in messages.EnumerateArray())
        {
            if (Json.String(message, "id") is { } id)
            {
                if (!ids.Add(id))
                {
                    sink.Report(Rules.SnapshotDuplicateId, $"Message '{id}' appears twice in the snapshot.", $"/messages/{index}/id");
                }

                if (Json.String(message, "role") == "activity")
                {
                    _activityIds.Add(id);
                }
            }

            Json.CollectEntityIds(message, _entityIds);
            index++;
        }
    }

    private void ActivitySnapshot(WireEvent evt, DiagnosticSink sink)
    {
        var id = evt.MessageId!;
        var replace = Json.Boolean(evt.Json, "replace") ?? true;

        if (_activityIds.Contains(id) && !replace)
        {
            // replace:false leaves the activity as it stands, owner included.
            if (evt.Owner is not null && _run.ActivityOwners.TryGetValue(id, out var owner) && owner != evt.Owner)
            {
                sink.Report(Rules.ActivityOwnerDisagrees,
                    $"This replace:false snapshot is attributed to '{evt.Owner}', but activity '{id}' belongs to {OwnerName(owner)}.");
            }

            return;
        }

        Register(id, MessageKind.Activity, sink);
        _activityIds.Add(id);
        _run.ActivityOwners[id] = evt.Owner; // A replacing snapshot re-mints ownership.
    }

    private void ActivityDelta(WireEvent evt, DiagnosticSink sink)
    {
        var id = evt.MessageId!;
        if (!_activityIds.Contains(id))
        {
            var reason = _messageKinds.TryGetValue(id, out var kind)
                ? $"'{id}' is a {Describe(kind)} message, not an activity message"
                : $"no ACTIVITY_SNAPSHOT created '{id}' earlier in the stream";
            sink.Report(Rules.ActivityWithoutBaseline, $"{reason}; a consumer skips this delta.");
            return;
        }

        if (evt.Owner is not null && _run.ActivityOwners.TryGetValue(id, out var owner) && owner != evt.Owner)
        {
            sink.Report(Rules.OwnerMismatch,
                $"ACTIVITY_DELTA is attributed to '{evt.Owner}', but activity '{id}' belongs to {OwnerName(owner)}.", "/subagentRunId");
        }
    }

    // ---- Subagents ------------------------------------------------------------------------------

    private void SubagentStarted(WireEvent evt, DiagnosticSink sink)
    {
        var id = evt.SubagentRunId!;
        if (_run.ActiveSubagents.Contains(id))
        {
            sink.Report(Rules.SubagentAlreadyActive, $"Invocation '{id}' is already active.");
            return;
        }

        if (_run.FinishedSubagents.Contains(id))
        {
            sink.Report(Rules.SubagentIdReused, $"Invocation '{id}' already finished in this run; a second invocation needs its own id.");
        }

        if (_run.AttributedBeforeAnnouncement.Contains(id))
        {
            sink.Report(Rules.AnnouncedLate, $"Events were attributed to '{id}' before it was announced; announce an invocation before its first event.");
        }

        if (Json.String(evt.Json, "parentSubagentRunId") is { } parent
            && !_run.ActiveSubagents.Contains(parent)
            && !_run.FinishedSubagents.Contains(parent))
        {
            sink.Report(Rules.UnknownParentInvocation, $"Parent invocation '{parent}' was not announced in this run.", "/parentSubagentRunId");
        }

        _run.ActiveSubagents.Add(id);
    }

    private void CheckAttribution(WireEvent evt, DiagnosticSink sink)
    {
        if (evt.Owner is not { } owner || evt.Synthesized)
        {
            return;
        }

        if (_run.FinishedSubagents.Contains(owner) && !_run.ActiveSubagents.Contains(owner))
        {
            sink.Report(Rules.AttributedAfterFinish, $"'{owner}' already finished in this run; nothing more should be attributed to it.", "/subagentRunId");
        }
        else if (!_run.ActiveSubagents.Contains(owner))
        {
            // Attribution without announcement is legal; remember it so a late announcement is caught.
            _run.AttributedBeforeAnnouncement.Add(owner);
        }
    }

    private static string OwnerName(string? owner) => owner is null ? "the parent agent" : $"'{owner}'";

    /// <summary>Everything the protocol tracks per run: it starts empty and does not cross the run boundary.</summary>
    private sealed class RunScope
    {
        public string? RunId { get; init; }

        public string? ThreadId { get; init; }

        public Dictionary<string, Item> OpenText { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Item> ClosedText { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Item> OpenToolCalls { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Item> ClosedToolCalls { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Item> OpenReasoning { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Item> ClosedReasoning { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> Spans { get; } = new(StringComparer.Ordinal);

        public Dictionary<(string? Owner, string Name), int> Steps { get; } = new();

        public HashSet<string> ActiveSubagents { get; } = new(StringComparer.Ordinal);

        public HashSet<string> FinishedSubagents { get; } = new(StringComparer.Ordinal);

        public HashSet<string> AttributedBeforeAnnouncement { get; } = new(StringComparer.Ordinal);

        public List<string> ToolCallsStarted { get; } = [];

        public HashSet<string> ToolCallsAnswered { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string?> ActivityOwners { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Item> Open(ItemKind kind) => kind switch
        {
            ItemKind.Text => OpenText,
            ItemKind.ToolCall => OpenToolCalls,
            _ => OpenReasoning,
        };

        public Dictionary<string, Item> Closed(ItemKind kind) => kind switch
        {
            ItemKind.Text => ClosedText,
            ItemKind.ToolCall => ClosedToolCalls,
            _ => ClosedReasoning,
        };
    }
}

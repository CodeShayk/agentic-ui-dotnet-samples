using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using AGUI.Server;
using Beacon.AI;
using Microsoft.Extensions.AI;

namespace Ch13.Beacon.Agent;

/// <summary>
/// Everything one run has told the client about the diagnostic plan and the service report, and the
/// smallest <c>STATE_DELTA</c> for each new fact. One instance per run: the endpoint creates it, the
/// stream options' <c>MapCall</c> and <c>MapResult</c> handlers use it, and so does
/// <see cref="ReportPreviewChatClient"/> through <see cref="ChatOptions.AdditionalProperties"/>. Because
/// every path writes through the same memory, nothing the client already has is sent twice.
/// </summary>
/// <remarks>
/// The baseline is the client's own state from <c>RunAgentInput.state</c>, which is the value every
/// delta in the run applies to. Only the fields Beacon owns are read from it, and only in the shape
/// Beacon writes; anything else is treated as absent and replaced.
/// </remarks>
public sealed class PredictiveStateWriter
{
    /// <summary>The key under which the writer travels in <see cref="ChatOptions.AdditionalProperties"/>.</summary>
    public const string OptionsKey = "beacon.predictiveState";

    public const string Drafting = "drafting";
    public const string Saving = "saving";
    public const string Saved = "saved";
    public const string Rejected = "rejected";

    private static readonly string[] ReportFields = ["summary", "findings", "recommendation"];

    private readonly Lock _gate = new();
    private readonly Dictionary<int, StreamedCall> _streams = [];
    private JsonObject? _plan;
    private JsonObject? _report;

    /// <summary>Creates a writer whose baseline is the client's state, if it sent one.</summary>
    public PredictiveStateWriter(JsonElement? clientState = null)
    {
        if (clientState is { ValueKind: JsonValueKind.Object } state)
        {
            _plan = state.TryGetProperty("plan", out var plan) ? ReadPlan(plan) : null;
            _report = state.TryGetProperty("report", out var report) ? ReadReport(report) : null;
        }
    }

    /// <summary>Makes this writer reachable from the chat pipeline for the run that <paramref name="options"/> belongs to.</summary>
    public void AttachTo(ChatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        (options.AdditionalProperties ??= [])[OptionsKey] = this;
    }

    /// <summary>The writer for the run <paramref name="options"/> belongs to, if one is attached.</summary>
    public static PredictiveStateWriter? From(ChatOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(OptionsKey, out PredictiveStateWriter? writer) == true ? writer : null;

    // ---- The plan --------------------------------------------------------------------------

    /// <summary><c>MapCall</c> for <c>set_diagnostic_plan</c>: the plan, every step pending.</summary>
    public IEnumerable<BaseEvent> PlanSet(FunctionCallContent call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var steps = Strings(Argument(call, "steps"));
        if (Text(Argument(call, "workOrderId")) is not { } workOrderId || DiagnosticPlan.Problem(steps) is not null)
        {
            return []; // the tool tells the model what was wrong; the client sees nothing
        }

        var plan = new JsonObject
        {
            ["workOrderId"] = workOrderId,
            ["steps"] = new JsonArray([.. steps.Select(title => (JsonNode)new JsonObject { ["title"] = title, ["status"] = DiagnosticPlan.Pending })]),
        };

        lock (_gate)
        {
            if (JsonNode.DeepEquals(_plan, plan))
            {
                return [];
            }

            _plan = plan;
            return [Delta(Operation("add", "/plan", plan.DeepClone()))];
        }
    }

    /// <summary><c>MapCall</c> for <c>update_plan_step</c>: one step's status and note, when they changed.</summary>
    public IEnumerable<BaseEvent> PlanStepUpdated(FunctionCallContent call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var index = Number(Argument(call, "index"));
        var status = Text(Argument(call, "status"));
        var note = Text(Argument(call, "note"));
        if (!DiagnosticPlan.IsStatus(status) || note?.Length > DiagnosticPlan.MaxNoteLength)
        {
            return [];
        }

        lock (_gate)
        {
            if (_plan?["steps"] is not JsonArray steps || index is not { } i || i < 0 || i >= steps.Count || steps[i] is not JsonObject step)
            {
                return []; // a step this run can't vouch for never reaches the client
            }

            JsonArray operations = [];
            if (step["status"]?.GetValue<string>() != status)
            {
                operations.Add(Operation("replace", $"/plan/steps/{i}/status", status));
                step["status"] = status;
            }

            if (note is not null && step["note"]?.GetValue<string>() != note)
            {
                operations.Add(Operation("add", $"/plan/steps/{i}/note", note));
                step["note"] = note;
            }

            return operations.Count == 0 ? [] : [new StateDeltaEvent { Delta = JsonNodes.ToElement(operations) }];
        }
    }

    /// <summary>True when this run's plan has a step at <paramref name="index"/>.</summary>
    public bool HasPlanStep(int index)
    {
        lock (_gate)
        {
            return _plan?["steps"] is JsonArray steps && index >= 0 && index < steps.Count;
        }
    }

    // ---- The service report ----------------------------------------------------------------

    /// <summary>
    /// A piece of a call's arguments while the model is still writing them. For
    /// <c>write_service_report</c>, the report so far becomes a delta, unless the piece changed nothing
    /// a reader would see (a property name, a quote, a comma).
    /// </summary>
    public StateDeltaEvent? ReportFragment(AGUIToolCallArgumentFragment fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        lock (_gate)
        {
            if (fragment.FunctionName is { Length: > 0 } name)
            {
                _streams[fragment.Index] = new StreamedCall(fragment.ToolCallId, name); // providers reuse indexes per turn
            }

            if (!_streams.TryGetValue(fragment.Index, out var stream) || stream.Name != DiagnosticTools.WriteReportName)
            {
                return null;
            }

            stream.Arguments.Append(fragment.ArgumentsDelta);
            if (PartialJson.Parse(stream.Arguments.ToString(), out var stillWriting) is not JsonObject arguments)
            {
                return null;
            }

            return ReportDelta(Draft(arguments, stillWriting, Drafting));
        }
    }

    /// <summary>
    /// <c>MapCall</c> for <c>write_service_report</c>: the complete arguments, and the report is now
    /// being saved. Usually only the status changes, because the pieces already delivered the text.
    /// </summary>
    public IEnumerable<BaseEvent> ReportCompleted(FunctionCallContent call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var arguments = new JsonObject();
        foreach (var name in (string[])["workOrderId", .. ReportFields])
        {
            if (Text(Argument(call, name)) is { } value)
            {
                arguments[name] = value;
            }
        }

        lock (_gate)
        {
            foreach (var index in _streams.Where(s => s.Value.CallId == call.CallId).Select(s => s.Key).ToList())
            {
                _streams.Remove(index);
            }

            return ReportDelta(Draft(arguments, stillWriting: null, Saving)) is { } delta ? [delta] : [];
        }
    }

    /// <summary><c>MapResult</c> for <c>write_service_report</c>: the prediction confirmed, or corrected.</summary>
    public IEnumerable<BaseEvent> ReportSaved(FunctionResultContent result)
    {
        ArgumentNullException.ThrowIfNull(result);
        string? error = null;
        try
        {
            using var document = JsonDocument.Parse(ScriptedChatClient.ResultJson(result));
            if (document.RootElement is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("error", out var message))
            {
                error = message.ValueKind == JsonValueKind.String ? message.GetString() : "The report wasn't saved.";
            }
        }
        catch (JsonException)
        {
            error = "The report wasn't saved.";
        }

        lock (_gate)
        {
            if (_report?.DeepClone() is not JsonObject next)
            {
                return [];
            }

            next["status"] = error is null ? Saved : Rejected;
            if (error is not null)
            {
                next["error"] = error;
            }
            else
            {
                next.Remove("error");
            }

            return ReportDelta(next) is { } delta ? [delta] : [];
        }
    }

    // Builds the report the client should show from (possibly partial) arguments. Only Beacon's own
    // fields go into state, capped at the length the tool accepts, and never half an identifier.
    private static JsonObject Draft(JsonObject arguments, string? stillWriting, string status)
    {
        var draft = new JsonObject();
        if (stillWriting != "workOrderId" && Text(arguments["workOrderId"]) is { } workOrderId)
        {
            draft["workOrderId"] = workOrderId;
        }

        draft["status"] = status;
        foreach (var field in ReportFields)
        {
            if (Text(arguments[field]) is { Length: > 0 } text) // a field that has only just opened shows nothing yet
            {
                draft[field] = text.Length > DiagnosticTools.MaxReportFieldLength ? text[..DiagnosticTools.MaxReportFieldLength] : text;
            }
        }

        return draft;
    }

    // The dedup: compare with what the client already has and send only the difference.
    private StateDeltaEvent? ReportDelta(JsonObject next)
    {
        JsonArray operations = [];
        if (_report is null)
        {
            operations.Add(Operation("add", "/report", next.DeepClone()));
        }
        else
        {
            foreach (var (name, value) in next)
            {
                if (!_report.TryGetPropertyValue(name, out var current) || !JsonNode.DeepEquals(current, value))
                {
                    operations.Add(Operation("add", $"/report/{name}", value?.DeepClone()));
                }
            }

            foreach (var name in _report.Select(p => p.Key).Where(name => !next.ContainsKey(name)))
            {
                operations.Add(Operation("remove", $"/report/{name}"));
            }
        }

        if (operations.Count == 0)
        {
            return null;
        }

        _report = (JsonObject)next.DeepClone();
        return new StateDeltaEvent { Delta = JsonNodes.ToElement(operations) };
    }

    // ---- Reading the client's baseline -----------------------------------------------------

    private static JsonObject? ReadPlan(JsonElement plan)
    {
        if (plan.ValueKind != JsonValueKind.Object || !plan.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array
            || steps.GetArrayLength() is 0 or > DiagnosticPlan.MaxSteps)
        {
            return null;
        }

        var copy = new JsonArray();
        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object || Text(step, "title") is not { } title || Text(step, "status") is not { } status || !DiagnosticPlan.IsStatus(status))
            {
                return null;
            }

            var item = new JsonObject { ["title"] = title, ["status"] = status };
            if (Text(step, "note") is { } note)
            {
                item["note"] = note;
            }

            copy.Add(item);
        }

        return new JsonObject { ["workOrderId"] = Text(plan, "workOrderId"), ["steps"] = copy };
    }

    private static JsonObject? ReadReport(JsonElement report)
    {
        if (report.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var copy = new JsonObject();
        foreach (var name in (string[])["workOrderId", "status", .. ReportFields, "error"])
        {
            if (Text(report, name) is { } value)
            {
                copy[name] = value;
            }
        }

        return copy;
    }

    // ---- Small helpers ---------------------------------------------------------------------

    private static StateDeltaEvent Delta(JsonObject operation) => new() { Delta = JsonNodes.ToElement(new JsonArray(operation)) };

    private static JsonObject Operation(string op, string path, JsonNode? value = null)
    {
        var operation = new JsonObject { ["op"] = op, ["path"] = path };
        if (op != "remove")
        {
            operation["value"] = value;
        }

        return operation;
    }

    private static object? Argument(FunctionCallContent call, string name) =>
        call.Arguments?.TryGetValue(name, out var value) == true ? value : null;

    // Arguments arrive as CLR values from the scripted model and as JsonElement from a real provider.
    private static string? Text(object? value) => value switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonValue node when node.TryGetValue<string>(out var text) => text,
        _ => null,
    };

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Number(object? value) => value switch
    {
        int number => number,
        long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
        JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var number) => number,
        _ => null,
    };

    private static List<string> Strings(object? value) => value switch
    {
        IEnumerable<string> strings => [.. strings],
        JsonElement { ValueKind: JsonValueKind.Array } array => [.. array.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "")],
        _ => [],
    };


    private sealed class StreamedCall(string? callId, string name)
    {
        public string? CallId { get; } = callId;

        public string Name { get; } = name;

        public StringBuilder Arguments { get; } = new();
    }
}

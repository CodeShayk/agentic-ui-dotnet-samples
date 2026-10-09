using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Beacon.AI;
using Microsoft.Extensions.AI;

namespace Ch13.Beacon.Agent;

/// <summary>
/// The run's "What Beacon checked" activity: one line per lookup, created with an
/// <c>ACTIVITY_SNAPSHOT</c> at the first lookup and amended with <c>ACTIVITY_DELTA</c> patches as more
/// lookups start and their results arrive. The server words each finding, so any client that can
/// render a list of labels and statuses can show it without knowing Beacon's tools.
/// </summary>
/// <param name="messageId">The activity message's ID, unique in the thread.</param>
internal sealed class CheckList(string messageId)
{
    public const string ActivityType = "beacon.checks";

    private readonly List<(string CallId, FunctionCallContent Call)> _checks = [];

    public IEnumerable<BaseEvent> Observe(ChatResponseUpdate update)
    {
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case FunctionCallContent call when IsLookup(call.Name) && !_checks.Exists(c => c.CallId == call.CallId):
                    var line = new JsonObject { ["callId"] = call.CallId, ["label"] = Label(call), ["status"] = "running" };
                    _checks.Add((call.CallId, call));

                    // The first check creates the activity; the protocol forbids a delta without that baseline.
                    yield return _checks.Count == 1
                        ? new ActivitySnapshotEvent
                        {
                            MessageId = messageId,
                            ActivityType = ActivityType,
                            Content = JsonNodes.ToElement(new JsonObject { ["title"] = "What Beacon checked", ["checks"] = new JsonArray(line) }),
                        }
                        : Delta(Operation("add", "/checks/-", line));
                    break;

                case FunctionResultContent result when _checks.FindIndex(c => c.CallId == result.CallId) is var index and >= 0:
                    var (status, finding) = Finding(_checks[index].Call, ScriptedChatClient.ResultJson(result));
                    yield return Delta(
                        Operation("replace", $"/checks/{index}/status", status),
                        Operation("add", $"/checks/{index}/finding", finding));
                    break;
            }
        }
    }

    private ActivityDeltaEvent Delta(params JsonObject[] operations) => new()
    {
        MessageId = messageId,
        ActivityType = ActivityType, // required on every delta; repeating it keeps the type
        Patch = JsonNodes.ToElement(new JsonArray([.. operations])),
    };

    private static bool IsLookup(string tool) =>
        tool is not (DiagnosticTools.SetPlanName or DiagnosticTools.UpdatePlanStepName or DiagnosticTools.WriteReportName);

    private static string Label(FunctionCallContent call) => call.Name switch
    {
        BeaconTools.GetWorkOrderName => $"Work order {Arg(call, "workOrderId")}",
        BeaconTools.GetFaultHistoryName => $"Fault history of {Arg(call, "equipmentId")}",
        BeaconTools.CheckWarrantyName => $"Warranty on {Arg(call, "equipmentId")}",
        BeaconTools.SearchPartsName => Arg(call, "model") is { Length: > 0 } model ? $"Stock of {Arg(call, "query")} for {model}" : $"Stock of {Arg(call, "query")}",
        BeaconTools.GetScheduleName => "The schedule for today",
        BeaconTools.ListWorkOrdersName => "Open work orders",
        _ => call.Name,
    };

    // One line a dispatcher can read at a glance. Tool results are the server's own data, but they are
    // parsed defensively anyway: a finding that can't be worded falls back to "Done".
    private static (string Status, string Finding) Finding(FunctionCallContent call, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
            {
                return ("failed", error.GetString() ?? "The lookup failed.");
            }

            return ("done", call.Name switch
            {
                BeaconTools.GetWorkOrderName when root.TryGetProperty("workOrder", out var wo) =>
                    $"{wo.GetProperty("status").GetString()}, {wo.GetProperty("priority").GetString()} priority, " +
                    $"{wo.GetProperty("assignedTechnicianId").GetString()} at " +
                    $"{DateTimeOffset.Parse(wo.GetProperty("scheduledFor").GetString()!, CultureInfo.InvariantCulture):HH:mm}",

                BeaconTools.GetFaultHistoryName when root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 =>
                    $"{root.GetArrayLength()} faults; latest {root[root.GetArrayLength() - 1].GetProperty("code").GetString()} " +
                    $"on {root[root.GetArrayLength() - 1].GetProperty("date").GetString()}",

                BeaconTools.CheckWarrantyName when root.TryGetProperty("covered", out var covered) =>
                    covered.GetBoolean() ? $"Covered until {root.GetProperty("expiresOn").GetString()}" : "Not covered",

                BeaconTools.SearchPartsName when root.ValueKind == JsonValueKind.Array =>
                    root.GetArrayLength() == 0 ? "Nothing in stock" :
                    $"{root[0].GetProperty("sku").GetString()}, ${root[0].GetProperty("unitPrice").GetDecimal().ToString("0.00", CultureInfo.InvariantCulture)}, " +
                    $"{root[0].GetProperty("inStock").GetInt32()} in stock",

                _ when root.ValueKind == JsonValueKind.Array => $"{root.GetArrayLength()} found",
                _ => "Done",
            });
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return ("done", "Done");
        }
    }

    private static string? Arg(FunctionCallContent call, string name) =>
        call.Arguments?.TryGetValue(name, out var value) == true
            ? value switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null,
            }
            : null;

    private static JsonObject Operation(string op, string path, JsonNode? value) =>
        new() { ["op"] = op, ["path"] = path, ["value"] = value };

}

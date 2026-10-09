using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Beacon.AI;

namespace Ch26.Beacon.Agent.State;

/// <summary>
/// Switches that put known regressions back, so the test suite can be shown catching them
/// (section 26.8). All of them are off by default, and none belongs in production configuration.
/// </summary>
public sealed class RegressionSwitches
{
    public const string SectionName = "Beacon:Regressions";

    /// <summary>Writes the settled work-order snapshot after <c>RUN_FINISHED</c> instead of before it.</summary>
    public bool SettleAfterRunFinished { get; set; }
}

/// <summary>
/// The work order a run opened, shared with the Desk as state. The stage watches the run's events, and
/// when <c>get_work_order</c> returns, it prepares a <c>STATE_SNAPSHOT</c> that settles the run (Chapter
/// 12's settle snapshot, reduced to one field). The snapshot belongs to the run, so it goes out just
/// before the run's terminal event.
/// </summary>
public static class WorkOrderState
{
    public static async IAsyncEnumerable<BaseEvent> SettleWorkOrderState(
        this IAsyncEnumerable<BaseEvent> events,
        RegressionSwitches switches,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> toolNames = new(StringComparer.Ordinal);
        StateSnapshotEvent? settled = null;

        await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            switch (evt)
            {
                case ToolCallStartEvent start:
                    toolNames[start.ToolCallId] = start.ToolCallName;
                    break;

                case ToolCallResultEvent result
                    when toolNames.GetValueOrDefault(result.ToolCallId) == BeaconTools.GetWorkOrderName
                    && FromToolResult(result.Content.Value as string) is { } snapshot:
                    settled = new StateSnapshotEvent { Snapshot = snapshot };
                    break;

                case RunFinishedEvent when settled is not null && !switches.SettleAfterRunFinished:
                    yield return settled; // still inside the run: RUN_FINISHED is the last event it may send
                    break;
            }

            yield return evt;
        }

        if (settled is not null && switches.SettleAfterRunFinished)
        {
            // The regression of section 26.8: "once the run's events are written" is after RUN_FINISHED.
            yield return settled;
        }
    }

    /// <summary>The state document for a <c>get_work_order</c> result, or <see langword="null"/> for an error result.</summary>
    public static JsonElement? FromToolResult(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root?["workOrder"] is not JsonObject workOrder || root["customer"] is not JsonObject customer || root["equipment"] is not JsonObject equipment)
        {
            return null; // a ToolError, such as an unknown work order: nothing was opened
        }

        var state = new JsonObject
        {
            ["activeWorkOrder"] = new JsonObject
            {
                ["id"] = workOrder["id"]?.DeepClone(),
                ["status"] = workOrder["status"]?.DeepClone(),
                ["priority"] = workOrder["priority"]?.DeepClone(),
                ["summary"] = workOrder["summary"]?.DeepClone(),
                ["customer"] = customer["name"]?.DeepClone(),
                ["equipmentId"] = equipment["id"]?.DeepClone(),
                ["equipment"] = $"{equipment["manufacturer"]} {equipment["model"]}",
                ["technicianId"] = workOrder["assignedTechnicianId"]?.DeepClone(),
            },
        };

        using var document = JsonDocument.Parse(state.ToJsonString());
        return document.RootElement.Clone();
    }
}

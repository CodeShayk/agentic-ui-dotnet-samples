using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using Ch20.DeclarativeUi;

namespace Ch20.Beacon.Agent.Surfaces;

/// <summary>
/// Puts accepted surfaces on the wire. In the AG-UI SDKs for other languages, a middleware scans tool
/// results and emits the surface as an activity; in .NET the seam is <see cref="AGUIStreamOptions.MapResult"/>,
/// whose events follow the tool's own <c>TOOL_CALL_RESULT</c>.
/// </summary>
public static class SurfaceEvents
{
    public static AGUIStreamOptions StreamOptions(SurfaceLedger ledger) =>
        new AGUIStreamOptions().MapResult(A2ui.RenderToolName, result => Paint(ledger, result.CallId));

    /// <summary>
    /// One <c>ACTIVITY_SNAPSHOT</c> per accepted surface: <c>activityType</c> <c>a2ui-surface</c>, the operations
    /// under <c>a2ui_operations</c>, and an ID derived from the tool call, so the surface keeps its place in the
    /// conversation. A rejected call has nothing to paint, and produces no event.
    /// </summary>
    public static IEnumerable<BaseEvent> Paint(SurfaceLedger ledger, string callId)
    {
        if (ledger.TakePaint(callId) is not { } operations)
        {
            return [];
        }

        using var content = JsonDocument.Parse(SurfaceOperations.ActivityContent(operations).ToJsonString());
        return
        [
            new ActivitySnapshotEvent
            {
                MessageId = A2ui.ActivityMessageId(callId),
                ActivityType = A2ui.ActivityType,
                Content = content.RootElement.Clone(),
                Replace = true,
            },
        ];
    }
}

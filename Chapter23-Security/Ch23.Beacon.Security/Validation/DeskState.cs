using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ch23.Beacon.Security.Validation;

/// <summary>
/// The shared state Beacon's clients may send in <c>RunAgentInput.state</c>: which work order is on
/// screen and which view shows it. State is a claim from the client, so it's parsed against a closed
/// schema here and authorized against the caller elsewhere; it never grants anything.
/// </summary>
public sealed partial record DeskState(string? ActiveWorkOrderId, string? View)
{
    public static readonly IReadOnlySet<string> Views = new HashSet<string>(StringComparer.Ordinal) { "board", "detail", "map" };

    /// <summary>
    /// Parses state against the schema: an object with at most <c>activeWorkOrderId</c> (a work order ID)
    /// and <c>view</c> (one of <see cref="Views"/>). Unknown properties are errors, not extras: the server
    /// owns this schema, and a property it doesn't know is one it can't vouch for.
    /// </summary>
    public static DeskState? Parse(JsonElement state, Action<string, string> error)
    {
        if (state.ValueKind != JsonValueKind.Object)
        {
            error("state", "State must be a JSON object.");
            return null;
        }

        string? workOrderId = null;
        string? view = null;
        var valid = true;

        foreach (var property in state.EnumerateObject())
        {
            switch (property.Name)
            {
                case "activeWorkOrderId" when property.Value.ValueKind == JsonValueKind.Null:
                    break;
                case "activeWorkOrderId" when property.Value.ValueKind == JsonValueKind.String
                                              && WorkOrderId().IsMatch(property.Value.GetString()!):
                    workOrderId = property.Value.GetString();
                    break;
                case "activeWorkOrderId":
                    error("state.activeWorkOrderId", "Must be a work order ID such as WO-1001.");
                    valid = false;
                    break;
                case "view" when property.Value.ValueKind == JsonValueKind.String && Views.Contains(property.Value.GetString()!):
                    view = property.Value.GetString();
                    break;
                case "view":
                    error("state.view", "Must be one of: board, detail, map.");
                    valid = false;
                    break;
                default:
                    error($"state.{property.Name}", "Beacon's state has no such property.");
                    valid = false;
                    break;
            }
        }

        return valid ? new DeskState(workOrderId, view) : null;
    }

    [GeneratedRegex(@"^WO-\d{4}$")]
    private static partial Regex WorkOrderId();
}

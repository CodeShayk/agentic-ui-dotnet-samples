// Copied from Chapter 14 (Ch14.Beacon.Agent/Approvals/ApprovalInterrupts.cs); unchanged apart from the namespace.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Beacon.AI;
using Beacon.Domain;

namespace Ch27.Beacon.Agent.Security.Approvals;

/// <summary>
/// The interrupt the server sends for an approval. The SDK's own interrupt for a
/// <see cref="Microsoft.Extensions.AI.ToolApprovalRequestContent"/> carries a generic message and a
/// yes-or-no schema; Beacon replaces it with one that says what the call costs, when the question
/// expires and exactly which arguments a dispatcher may change.
/// </summary>
public static class ApprovalInterrupts
{
    public static AGUIInterrupt For(PendingApproval approval, ApprovalOptions options) => new()
    {
        Id = approval.Id,
        Reason = InterruptReasons.ToolCall,
        ToolCallId = approval.ToolCallId,
        Message = $"{approval.Summary} Spending over {SpendingApprovalPolicy.Money(SpendingPolicy.ApprovalThreshold)} needs a dispatcher to approve it.",
        ExpiresAt = approval.ExpiresAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        ResponseSchema = ToElement(ResponseSchema(approval, options)),
        Metadata = ToElement(new JsonObject
        {
            ["amount"] = approval.Amount,
            ["threshold"] = SpendingPolicy.ApprovalThreshold,
            ["currency"] = "USD",
        }),
    };

    // AG-UI's approve-with-edits pattern: editedArgs is a full replacement of the call's arguments. Each
    // argument a dispatcher may not change is pinned with "const", so the schema itself says what's editable.
    private static JsonObject ResponseSchema(PendingApproval approval, ApprovalOptions options)
    {
        var editable = SpendingApprovalPolicy.EditableArguments[approval.ToolName];
        var arguments = new JsonObject();
        JsonArray required = [];
        foreach (var argument in approval.Arguments.EnumerateObject())
        {
            arguments[argument.Name] = editable.Contains(argument.Name)
                ? EditableArgument(approval.ToolName, argument.Name)
                : new JsonObject { ["const"] = JsonNode.Parse(argument.Value.GetRawText()) };
            required.Add(argument.Name);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["approved"] = new JsonObject { ["type"] = "boolean" },
                ["reason"] = new JsonObject { ["type"] = "string", ["maxLength"] = options.MaxReasonLength },
                ["editedArgs"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = arguments,
                    ["required"] = required,
                    ["additionalProperties"] = false,
                },
            },
            ["required"] = new JsonArray("approved"),
        };
    }

    private static JsonObject EditableArgument(string toolName, string argument) => (toolName, argument) switch
    {
        (BeaconTools.PlacePartOrderName, "quantity") => new() { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 10 },
        (BeaconTools.ChargeCustomerName, "amount") => new() { ["type"] = "number", ["exclusiveMinimum"] = 0, ["maximum"] = 25000 },
        (BeaconTools.ChargeCustomerName, "description") => new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 200 },
        _ => throw new InvalidOperationException($"{toolName}.{argument} isn't editable."),
    };

    private static JsonElement ToElement(JsonNode node) => JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
}

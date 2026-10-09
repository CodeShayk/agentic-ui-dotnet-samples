using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Server;
using Ch20.Beacon.Agent.Inspections;
using Ch20.DeclarativeUi;
using Microsoft.Extensions.AI;

namespace Ch20.Beacon.Agent.Surfaces;

/// <summary>
/// <c>render_a2ui</c>, with the name and arguments the AG-UI A2UI middleware gives it (<c>surfaceId</c>,
/// <c>components</c>, <c>data</c>), as a server tool. Invoking it draws nothing: it builds the A2UI operations,
/// validates them against Beacon's catalog and the inspection template, and either records them for the
/// stream or tells the model, in A2UI's <c>VALIDATION_FAILED</c> format, exactly what to fix.
/// </summary>
public sealed partial class RenderSurfaceTool(
    SurfaceValidator validator,
    InspectionRules rules,
    SurfaceLedger ledger,
    ILogger<RenderSurfaceTool> logger) : AIFunction
{
    private static readonly JsonElement Schema = InspectionTemplates.ToElement(JsonNode.Parse(
        """
        {
          "type": "object",
          "properties": {
            "surfaceId": { "type": "string", "description": "A unique ID for the surface, such as inspection-WO-1003." },
            "components": { "type": "array", "items": { "type": "object" }, "description": "A2UI v0.9 components, flat; the root has the ID root." },
            "data": { "type": "object", "description": "The surface's initial data model." }
          },
          "required": ["surfaceId", "components"]
        }
        """)!);

    public override string Name => A2ui.RenderToolName;

    public override string Description =>
        "Shows a form on the dispatcher's screen, described in A2UI v0.9 with Beacon's field-forms catalog. " +
        "Beacon chooses the catalog; don't send one. The result says whether the form was accepted.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var call = FunctionInvokingChatClient.CurrentContext;
        var callId = call?.CallContent.CallId ?? $"call-{Guid.NewGuid():N}";
        var run = call?.Options is { } options && options.TryGetRunAgentInput(out var input) ? input : null;
        var surfaceId = Text(Argument(arguments, "surfaceId")) ?? string.Empty;

        if (run is not null && ledger.CountAttempt(run.ThreadId, run.RunId) > ledger.MaxAttemptsPerRun)
        {
            return Result(Rejected(surfaceId, [new("attempts_exhausted", string.Empty, "No more forms this run. Answer in text.", IsError: true)]));
        }

        // The model writes components and data; the server writes the envelope, and chooses the catalog.
        var operations = new JsonArray(
            SurfaceOperations.CreateSurface(surfaceId, BeaconCatalog.Id),
            SurfaceOperations.UpdateComponents(surfaceId, Argument(arguments, "components") as JsonArray ?? new JsonArray()),
            SurfaceOperations.UpdateDataModel(surfaceId, Argument(arguments, "data") as JsonObject ?? new JsonObject()));

        // First the catalog's rules, then the inspection's.
        var verdict = validator.Validate(operations);
        List<SurfaceProblem> problems = [.. verdict.Errors];
        InspectionForm? form = null;
        if (verdict.Surface is { } surface)
        {
            problems.AddRange(rules.CheckForm(surface, out form));
        }

        if (problems.Count > 0 || form is null || run is null)
        {
            LogRejected(logger, surfaceId, problems.Count, problems.FirstOrDefault()?.Code ?? "no_run");
            return Result(Rejected(surfaceId, problems));
        }

        ledger.Issue(run.ThreadId, surfaceId, form, callId, operations);
        return Result(new JsonObject
        {
            ["status"] = "accepted",
            ["surfaceId"] = surfaceId,
            ["components"] = verdict.Surface!.Components.Count,
            ["next"] = "The form is on the dispatcher's screen. Say so in one sentence; its submit_inspection action comes back later.",
        });
    }

    // A2UI's standard validation error: code, surfaceId, path (a JSON Pointer) and a one-sentence message.
    private static JsonObject Rejected(string surfaceId, IEnumerable<SurfaceProblem> problems) => new()
    {
        ["status"] = "rejected",
        ["errors"] = new JsonArray([.. problems.Take(10).Select(p => new JsonObject
        {
            ["code"] = "VALIDATION_FAILED",
            ["surfaceId"] = surfaceId,
            ["path"] = InArguments(p.Path),
            ["message"] = $"{p.Code}: {p.Message}",
        })]),
        ["next"] = "Fix these and call render_a2ui again, or answer in text.",
    };

    // The validator's paths point into the operations the server built; the model wants them in its own arguments.
    private static string InArguments(string path) =>
        path.StartsWith("/1/updateComponents", StringComparison.Ordinal) ? path["/1/updateComponents".Length..]
        : path.StartsWith("/2/updateDataModel/value", StringComparison.Ordinal) ? "/data" + path["/2/updateDataModel/value".Length..]
        : path.StartsWith("/0/createSurface", StringComparison.Ordinal) ? path["/0/createSurface".Length..]
        : path;

    // Models send the arguments as JSON; some send an array or object as a JSON string, which is accepted too.
    private static JsonNode? Argument(AIFunctionArguments arguments, string name) =>
        arguments.TryGetValue(name, out var value) ? value switch
        {
            JsonNode node => node.DeepClone(),
            JsonElement { ValueKind: JsonValueKind.String } text => ParseText(text.GetString()),
            JsonElement element => JsonNode.Parse(element.GetRawText()),
            string text => ParseText(text),
            _ => null,
        } : null;

    private static JsonNode? ParseText(string? text)
    {
        if (text is null || text.TrimStart() is not ['[' or '{', ..])
        {
            return text is null ? null : JsonValue.Create(text);
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static ValueTask<object?> Result(JsonObject result) => ValueTask.FromResult<object?>(InspectionTemplates.ToElement(result));

    // The surface ID, how many problems and the first one's code; never the components or the data.
    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Rejected surface {SurfaceId}: {Count} problem(s), first {Code}.")]
    private static partial void LogRejected(ILogger logger, string surfaceId, int count, string code);
}

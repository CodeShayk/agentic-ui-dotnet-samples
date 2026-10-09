using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Beacon.AI;
using Ch20.Beacon.Agent.Inspections;
using Ch20.DeclarativeUi;
using Microsoft.Extensions.AI;

namespace Ch20.Beacon.Agent;

/// <summary>
/// Chapter 20's rules for the scripted model, tried before Beacon's defaults. Asked for an inspection, it
/// reads the template and composes the form in A2UI, the way an instructed model would, then reacts to the
/// server's verdict and, later, to the submitted form. On the CA-36X it slips on its first try, leaving a
/// button's label undefined, so the validate-and-retry loop runs every time.
/// </summary>
/// <remarks>The scripted model matches keywords and composes forms in code; a real model writes its own tree.</remarks>
public static class InspectionScript
{
    public const string SubmitLabel = "Submit inspection";

    public static IReadOnlyList<ScriptRule> Rules { get; } =
    [
        new("inspection-template", Reply: ctx =>
            ctx.Mentions("inspection", "checklist") && ctx.Find(BeaconScripts.WorkOrderPattern) is { } wo
                ? ScriptReply.Call(InspectionTemplates.ToolName, Args(new JsonObject { ["workOrderId"] = wo }))
                : null),

        new("inspection-form", Summarize: ctx => ctx.ToolResults.Count == 1 ? Next(ctx, ctx.ToolResults[0]) : null),
    ];

    private static ScriptReply? Next(ScriptContext ctx, FunctionResultContent result)
    {
        var json = ParseObject(ScriptedChatClient.ResultJson(result));
        switch (ctx.ToolNameFor(result))
        {
            case InspectionTemplates.ToolName when (json?["error"]) is { } error:
                return ScriptReply.Say(error.GetValue<string>());

            case InspectionTemplates.ToolName when json is not null:
                return ScriptReply.Call(A2ui.RenderToolName, Compose(json, slip: Text(json["model"]) == "CA-36X"));

            case A2ui.RenderToolName when Text(json?["status"]) == "accepted" && TemplateIn(ctx) is { } template:
                return ScriptReply.Say(
                    $"Here is the {Text(template["title"])} for {Text(template["workOrderId"])}, from {Text(template["source"])}. " +
                    $"Fill it in with the technician and press **{SubmitLabel}**; I'll record it on the work order.");

            case A2ui.RenderToolName when TemplateIn(ctx) is { } template && Attempts(ctx) < 2:
                return ScriptReply.Call(A2ui.RenderToolName, Compose(template, slip: false)); // fix what the server listed

            case A2ui.RenderToolName:
                var reason = (json?["errors"] as JsonArray)?.FirstOrDefault()?["message"]?.GetValue<string>() ?? "the form wasn't accepted";
                return ScriptReply.Say($"I couldn't show that form ({reason}). Ask me for the inspection again, or I can list its checks as text.");

            case A2ui.LogEventToolName:
                const string marker = "work order: ";
                var text = ResultText(result) ?? string.Empty;
                var at = text.IndexOf(marker, StringComparison.Ordinal);
                var summary = at >= 0 ? text[(at + marker.Length)..] : "The inspection is on the work order.";
                return ScriptReply.Say($"Recorded. {summary}" + FollowUp(summary));

            default:
                return null;
        }
    }

    /// <summary>
    /// The form for a template: a card around a column with a title, a template over the checks, an input per
    /// reading, the overall condition, notes and a submit button whose context sends the lot.
    /// </summary>
    public static Dictionary<string, object?> Compose(JsonObject template, bool slip)
    {
        var wo = Text(template["workOrderId"])!;
        var readings = (template["readings"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var checks = (template["checks"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];

        string[] order = ["title", "subtitle", "checks-heading", "checks", "readings-heading", .. readings.Select(r => $"reading-{Text(r["id"])}"), "condition", "notes", "submit"];
        var components = new JsonArray
        {
            new JsonObject { ["id"] = "root", ["component"] = "Card", ["child"] = "form" },
            new JsonObject { ["id"] = "form", ["component"] = "Column", ["children"] = Strings(order) },
            TextNode("title", $"{Text(template["title"])} for {wo}", "h2"),
            TextNode("subtitle", $"{Text(template["customer"])}, {Text(template["equipmentId"])}. From {Text(template["source"])}.", "caption"),
            TextNode("checks-heading", "Checks", "h3"),
            new JsonObject { ["id"] = "checks", ["component"] = "Column", ["children"] = new JsonObject { ["componentId"] = "check", ["path"] = "/checks" } },
            new JsonObject { ["id"] = "check", ["component"] = "CheckBox", ["label"] = Bind("label"), ["value"] = Bind("passed") },
            TextNode("readings-heading", "Readings", "h3"),
        };

        var data = new JsonObject
        {
            ["workOrderId"] = wo,
            ["checks"] = new JsonArray([.. checks.Select(c => new JsonObject { ["id"] = c["id"]!.DeepClone(), ["label"] = c["label"]!.DeepClone(), ["passed"] = false })]),
            ["readings"] = new JsonObject(),
            ["condition"] = new JsonArray(),
            ["notes"] = string.Empty,
        };

        List<JsonNode> required = [];
        foreach (var reading in readings)
        {
            var id = Text(reading["id"])!;
            var label = $"{Text(reading["label"])} ({Text(reading["unit"])})";
            var path = $"/readings/{id}";
            var (min, max) = (reading["min"]!.GetValue<double>(), reading["max"]!.GetValue<double>());
            if (Text(reading["kind"]) == "scale")
            {
                components.Add(new JsonObject { ["id"] = $"reading-{id}", ["component"] = "Slider", ["label"] = label, ["min"] = min, ["max"] = max, ["value"] = Bind(path) });
                data["readings"]![id] = min;
                continue;
            }

            components.Add(new JsonObject
            {
                ["id"] = $"reading-{id}",
                ["component"] = "TextField",
                ["label"] = label,
                ["variant"] = "number",
                ["value"] = Bind(path),
                ["checks"] = new JsonArray
                {
                    Check(Call("required", new JsonObject { ["value"] = Bind(path) }), $"Enter the {Text(reading["label"])!.ToLowerInvariant()}."),
                    Check(Call("numeric", new JsonObject { ["value"] = Bind(path), ["min"] = min, ["max"] = max }), $"Enter a number from {Num(min)} to {Num(max)} {Text(reading["unit"])}."),
                },
            });
            data["readings"]![id] = string.Empty;
            required.Add(Call("required", new JsonObject { ["value"] = Bind(path) }));
        }

        required.Add(Call("required", new JsonObject { ["value"] = Bind("/condition") }));
        components.Add(new JsonObject
        {
            ["id"] = "condition",
            ["component"] = "ChoicePicker",
            ["label"] = InspectionTemplate.ConditionLabel,
            ["variant"] = "mutuallyExclusive",
            ["options"] = InspectionRules.ConditionOptions,
            ["value"] = Bind("/condition"),
        });
        components.Add(new JsonObject
        {
            ["id"] = "notes",
            ["component"] = "TextField",
            ["label"] = "Notes for the work order",
            ["variant"] = "longText",
            ["value"] = Bind("/notes"),
            ["checks"] = new JsonArray { Check(Call("length", new JsonObject { ["value"] = Bind("/notes"), ["max"] = InspectionRules.MaxNotesLength }), "Keep notes under 1,000 characters.") },
        });

        // The slip: the button names a label component that the model never writes.
        if (!slip)
        {
            components.Add(TextNode("submit-label", SubmitLabel, "body"));
        }

        components.Add(new JsonObject
        {
            ["id"] = "submit",
            ["component"] = "Button",
            ["child"] = "submit-label",
            ["variant"] = "primary",
            ["action"] = new JsonObject
            {
                ["event"] = new JsonObject
                {
                    ["name"] = InspectionRules.SubmitAction,
                    ["context"] = new JsonObject
                    {
                        ["workOrderId"] = wo,
                        ["checks"] = Bind("/checks"),
                        ["readings"] = Bind("/readings"),
                        ["condition"] = Bind("/condition"),
                        ["notes"] = Bind("/notes"),
                    },
                },
            },
            ["checks"] = new JsonArray { Check(Call("and", new JsonObject { ["values"] = new JsonArray([.. required]) }), "Enter every reading and choose the overall condition.") },
        });

        return Args(new JsonObject { ["surfaceId"] = $"inspection-{wo}", ["components"] = components, ["data"] = data });
    }

    private static JsonObject? TemplateIn(ScriptContext ctx) =>
        ctx.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .LastOrDefault(r => ctx.ToolNameFor(r) == InspectionTemplates.ToolName) is { } result
            ? ParseObject(ScriptedChatClient.ResultJson(result))
            : null;

    // Tool results are JSON, except log_a2ui_event's, which is a sentence.
    private static JsonObject? ParseObject(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int Attempts(ScriptContext ctx) =>
        ctx.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Count(c => c.Name == A2ui.RenderToolName);

    private static string FollowUp(string summary) =>
        summary.Contains("failed: outdoor fan turns freely", StringComparison.Ordinal)
            ? " The bearing noise points at the outdoor fan: BRG-KIT-1, the fan bearing kit ($46.75), fits the HP-24S and is in stock at Dover if you want one ordered."
            : summary.Contains("Overall: pass.", StringComparison.Ordinal) ? string.Empty : " Tell me if you want parts looked up for what failed.";

    private static string? ResultText(FunctionResultContent result) => result.Result switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        _ => null,
    };

    private static JsonObject TextNode(string id, string text, string variant) =>
        new() { ["id"] = id, ["component"] = "Text", ["text"] = text, ["variant"] = variant };

    private static JsonObject Bind(string path) => new() { ["path"] = path };

    private static JsonObject Call(string name, JsonObject args) => new() { ["call"] = name, ["args"] = args };

    private static JsonObject Check(JsonNode condition, string message) => new() { ["condition"] = condition, ["message"] = message };

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]);

    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static Dictionary<string, object?> Args(JsonObject args) =>
        args.ToDictionary(p => p.Key, p => (object?)InspectionTemplates.ToElement(p.Value!), StringComparer.Ordinal);
}

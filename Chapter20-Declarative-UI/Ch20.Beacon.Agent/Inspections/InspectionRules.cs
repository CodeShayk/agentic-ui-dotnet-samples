using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Beacon.Domain;
using Ch20.DeclarativeUi;

namespace Ch20.Beacon.Agent.Inspections;

/// <summary>An inspection form the server accepted: which work order, which template and which button submits it.</summary>
public sealed record InspectionForm(string WorkOrderId, string EquipmentId, InspectionTemplate Template, string SubmitButtonId);

/// <summary>A submitted inspection, every value checked against the template.</summary>
public sealed record InspectionSubmission(
    string WorkOrderId,
    string EquipmentId,
    InspectionTemplate Template,
    IReadOnlyList<(InspectionCheck Check, bool Passed)> Checks,
    IReadOnlyList<(InspectionReading Reading, double Value)> Readings,
    InspectionOption Condition,
    string Notes)
{
    /// <summary>One paragraph for the work order's notes, and for the model.</summary>
    public string Summary()
    {
        var failed = Checks.Where(c => !c.Passed).Select(c => Lower(c.Check.Label)).ToList();
        var readings = string.Join(", ", Readings.Select(r => r.Reading.Kind == ReadingKind.Scale
            ? $"{Lower(r.Reading.Label)} {Number(r.Value)} of {Number(r.Reading.Max)}"
            : $"{Lower(r.Reading.Label)} {Number(r.Value)} {r.Reading.Unit}"));
        return $"Inspection of {EquipmentId} (Contoso Air {Template.Model}) per {Template.Source}: "
            + $"{Checks.Count(c => c.Passed)} of {Checks.Count} checks passed"
            + (failed.Count > 0 ? $"; failed: {string.Join("; ", failed)}" : string.Empty)
            + $". Readings: {readings}. Overall: {Condition.Label.ToLowerInvariant()}."
            + (Notes.Length > 0 ? $" Notes: {Notes}" : string.Empty);
    }

    private static string Lower(string label) => char.ToLowerInvariant(label[0]) + label[1..];

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>
/// The inspection's own rules, on top of the catalog's. A valid A2UI surface can still be the wrong form:
/// it could drop a check, invent one, forget to bind a reading, or keep the template's values while showing
/// Dana different words (a "Pass" option that sends "fail", a checkbox labeled with the opposite of its
/// check). The server pins every word Dana answers to the template, as Chapter 11 held a component's props
/// to Beacon's data, and holds each submission to the template too. Headings, the title and hint text stay
/// the model's.
/// </summary>
public sealed class InspectionRules(InspectionTemplates templates)
{
    public const string SubmitAction = "submit_inspection";
    public const int MaxNotesLength = 1000;

    private static readonly string[] ContextKeys = ["workOrderId", "checks", "readings", "condition", "notes"];

    /// <summary>The overall-condition options a form must offer: the template's, label and value, in order.</summary>
    public static JsonArray ConditionOptions =>
        new([.. InspectionTemplate.Conditions.Select(c => new JsonObject { ["label"] = c.Label, ["value"] = c.Value })]);

    /// <summary>The data model a form must start from: the template's checks and labels, nothing answered yet.</summary>
    public static JsonObject BlankForm(string workOrderId, InspectionTemplate template) => new()
    {
        ["workOrderId"] = workOrderId,
        ["checks"] = new JsonArray([.. template.Checks.Select(c => new JsonObject { ["id"] = c.Id, ["label"] = c.Label, ["passed"] = false })]),
        ["readings"] = new JsonObject([.. template.Readings.Select(r => KeyValuePair.Create(r.Id,
            r.Kind == ReadingKind.Scale ? JsonValue.Create(r.Min) : (JsonNode?)JsonValue.Create(string.Empty)))]),
        ["condition"] = new JsonArray(),
        ["notes"] = string.Empty,
    };

    /// <summary>Checks that a surface is this work order's inspection, and finds the button that submits it.</summary>
    public IReadOnlyList<SurfaceProblem> CheckForm(SurfaceDocument surface, out InspectionForm? form)
    {
        form = null;
        List<SurfaceProblem> problems = [];
        if (Text(surface.Data["workOrderId"]) is not { } workOrderId || templates.For(workOrderId) is not { } found)
        {
            problems.Add(Problem("/data/workOrderId", "The data model names the work order (workOrderId) whose inspection template the form follows."));
            return problems;
        }

        var (workOrder, equipment, _, template) = found;

        // The template's blank form: its checks with its labels, nothing answered, so no answer is Dana's by default.
        var blank = BlankForm(workOrder.Id, template);
        if (!JsonNode.DeepEquals(surface.Data, blank))
        {
            problems.Add(Problem("/data", $"Start from the template's blank form, exactly: {blank.ToJsonString()}"));
        }

        // Every input answers a question the template asks, in the template's words.
        var repeated = TemplateChildren(surface, "/checks");
        HashSet<string> answered = new(StringComparer.Ordinal);
        foreach (var input in surface.Components.Values.Where(c => c.IsKnown && c.Type is "CheckBox" or "TextField" or "Slider" or "ChoicePicker"))
        {
            var label = input.Properties["label"];
            switch (SurfaceDataModel.BindingOf(input.Properties["value"])?.ToString())
            {
                // The checks: one CheckBox repeated over /checks, showing each check's own label from the data model.
                case "passed" when input.Type == "CheckBox" && repeated.Contains(input.Id):
                    if (SurfaceDataModel.BindingOf(label)?.ToString() != "label")
                    {
                        problems.Add(Problem("/components", $"'{input.Id}' shows each check's own label: \"label\": {{\"path\": \"label\"}}."));
                    }

                    answered.Add("/checks");
                    break;

                case { } path when template.Readings.FirstOrDefault(r => path == $"/readings/{r.Id}") is { } reading:
                    if (input.Type is not ("TextField" or "Slider") || Text(label) != reading.InputLabel)
                    {
                        problems.Add(Problem("/components", $"'{input.Id}' is a TextField or Slider labeled \"{reading.InputLabel}\", the template's words for {path}."));
                    }

                    answered.Add(path);
                    break;

                // The verdict: the template's options, so "Pass" always sends "pass".
                case "/condition":
                    if (input.Type != "ChoicePicker" || Text(label) != InspectionTemplate.ConditionLabel
                        || !JsonNode.DeepEquals(input.Properties["options"], ConditionOptions))
                    {
                        problems.Add(Problem("/components", $"'{input.Id}' is a ChoicePicker labeled \"{InspectionTemplate.ConditionLabel}\" with exactly these options: {ConditionOptions.ToJsonString()}."));
                    }

                    answered.Add("/condition");
                    break;

                case "/notes" when input.Type == "TextField":
                    break;

                default:
                    problems.Add(Problem("/components", $"'{input.Id}' binds a value the inspection doesn't record. Inputs bind /readings/<id>, /condition or /notes; the checks' CheckBox binds passed."));
                    break;
            }
        }

        foreach (var missing in template.Readings.Select(r => $"/readings/{r.Id}").Append("/condition").Append("/checks").Where(p => !answered.Contains(p)))
        {
            problems.Add(Problem("/components", missing == "/checks"
                ? "Repeat one CheckBox over /checks with \"label\": {\"path\": \"label\"} and \"value\": {\"path\": \"passed\"}."
                : $"No input answers {missing}."));
        }

        // One button submits, and its context sends the whole form for this work order and nothing else.
        var submit = surface.Components.Values
            .Where(c => c.IsKnown && c.Type == "Button" && EventOf(c) is { } e && Text(e["name"]) == SubmitAction)
            .ToList();
        if (submit.Count != 1)
        {
            problems.Add(Problem("/components", $"Exactly one Button's action is {SubmitAction}."));
        }
        else if (EventOf(submit[0])!["context"] is not JsonObject context
                 || !context.Select(p => p.Key).Order().SequenceEqual(ContextKeys.Order())
                 || Text(context["workOrderId"]) != workOrder.Id
                 || ContextKeys.Skip(1).Any(key => SurfaceDataModel.BindingOf(context[key])?.ToString() != "/" + key))
        {
            problems.Add(Problem("/components", $"The {SubmitAction} context is workOrderId (literal) and bindings to /checks, /readings, /condition and /notes."));
        }

        if (problems.Count == 0)
        {
            form = new InspectionForm(workOrder.Id, equipment.Id, template, submit[0].Id);
        }

        return problems;
    }

    /// <summary>Reads a submitted context, value by value, against the template. Errors are keyed like a validation problem.</summary>
    public InspectionSubmission? ReadSubmission(string workOrderId, JsonObject context, out Dictionary<string, string[]> errors)
    {
        errors = new();
        if (templates.For(workOrderId) is not { } found)
        {
            errors["workOrderId"] = ["The work order has no inspection template."];
            return null;
        }

        var (_, equipment, _, template) = found;
        foreach (var key in context.Select(p => p.Key).Where(k => !ContextKeys.Contains(k)))
        {
            errors[key] = ["The inspection has no such field."];
        }

        if (Text(context["workOrderId"]) != workOrderId)
        {
            errors["workOrderId"] = [$"This form is for {workOrderId}."];
        }

        List<(InspectionCheck, bool)> checks = [];
        var submitted = (context["checks"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        foreach (var check in template.Checks)
        {
            if (submitted.FirstOrDefault(c => Text(c["id"]) == check.Id)?["passed"] is JsonValue passed
                && passed.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
            {
                checks.Add((check, passed.GetValueKind() == JsonValueKind.True));
            }
            else
            {
                errors[$"checks.{check.Id}"] = ["Every check is answered with true or false."];
            }
        }

        if (submitted.Count != template.Checks.Count)
        {
            errors["checks"] = [$"The inspection has {template.Checks.Count} checks."];
        }

        List<(InspectionReading, double)> readings = [];
        var values = context["readings"] as JsonObject ?? new JsonObject();
        foreach (var key in values.Select(p => p.Key).Where(k => template.Readings.All(r => r.Id != k)))
        {
            errors[$"readings.{key}"] = ["The inspection has no such reading."];
        }

        foreach (var reading in template.Readings)
        {
            if (SurfaceDataModel.ToNumber(values[reading.Id]) is { } value && value >= reading.Min && value <= reading.Max)
            {
                readings.Add((reading, value));
            }
            else
            {
                errors[$"readings.{reading.Id}"] = [$"{reading.Label} is a number from {reading.Min} to {reading.Max} {reading.Unit}."];
            }
        }

        var chosen = (context["condition"] as JsonArray)?.Select(Text).ToList() ?? [];
        var condition = chosen.Count == 1 ? InspectionTemplate.Conditions.FirstOrDefault(c => c.Value == chosen[0]) : null;
        if (condition is null)
        {
            errors["condition"] = ["Choose one overall condition."];
        }

        var notes = context["notes"] is null ? string.Empty : Text(context["notes"]);
        if (notes is null || notes.Length > MaxNotesLength)
        {
            errors["notes"] = [$"Notes are text of at most {MaxNotesLength} characters."];
        }

        return errors.Count > 0
            ? null
            : new InspectionSubmission(workOrderId, equipment.Id, template, checks, readings, condition!, notes!.Trim());
    }

    // The components a template repeats over this array. One that is also someone's static child is left out:
    // drawn there, its relative bindings would resolve at the data model's root, not in a check.
    private static HashSet<string> TemplateChildren(SurfaceDocument surface, string arrayPath)
    {
        HashSet<string> repeated = new(StringComparer.Ordinal);
        HashSet<string> placed = new(StringComparer.Ordinal);
        foreach (var component in surface.Components.Values)
        {
            switch (component.Properties["children"])
            {
                case JsonObject template when Text(template["path"]) == arrayPath && Text(template["componentId"]) is { } id:
                    repeated.Add(id);
                    break;
                case JsonArray list:
                    placed.UnionWith(list.Select(Text).OfType<string>());
                    break;
            }

            if (Text(component.Properties["child"]) is { } child)
            {
                placed.Add(child);
            }
        }

        repeated.ExceptWith(placed);
        return repeated;
    }

    private static JsonObject? EventOf(ComponentNode button) =>
        button.Properties["action"] is JsonObject action ? action["event"] as JsonObject : null;

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static SurfaceProblem Problem(string path, string message) => new("inspection_mismatch", path, message, IsError: true);
}

/// <summary>Writes a submitted inspection to its work order: one note, in the work order's history.</summary>
public sealed class InspectionRecorder(IFieldServiceStore store)
{
    public string Record(InspectionSubmission submission)
    {
        var summary = submission.Summary();
        var workOrder = store.GetWorkOrder(submission.WorkOrderId)
            ?? throw new FieldServiceException($"Work order {submission.WorkOrderId} doesn't exist.");
        store.UpdateWorkOrder(workOrder with { Notes = [.. workOrder.Notes, summary] });
        return summary;
    }
}

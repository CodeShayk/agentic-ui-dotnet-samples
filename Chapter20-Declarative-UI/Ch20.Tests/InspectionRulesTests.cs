using System.Text.Json;
using System.Text.Json.Nodes;
using Beacon.Domain;
using Ch20.Beacon.Agent.Inspections;
using Ch20.Beacon.Agent.Surfaces;
using Ch20.DeclarativeUi;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using static Ch20.Tests.Surfaces;

namespace Ch20.Tests;

/// <summary>
/// The inspection's own rules on top of the catalog's. Every form here is a valid A2UI surface, so the
/// catalog accepts it; the question is whether it still asks Dana what the template asks, in the template's
/// words, with nothing answered for her. A steered model's inversions (swapped option values, a checkbox
/// labeled with the opposite of its check, swapped reading labels) must never become a surface.
/// </summary>
public sealed class InspectionRulesTests
{
    private static readonly InspectionRules Rules = new(new InspectionTemplates(new InMemoryFieldServiceStore()));

    [Theory]
    [InlineData(Recordings.Wo1003, "WO-1003")]
    [InlineData(Recordings.Wo1001, "WO-1001")]
    [InlineData(Recordings.Wo1005, "WO-1005")]
    public void The_forms_the_agent_sent_follow_their_templates(string recording, string workOrderId)
    {
        var problems = Rules.CheckForm(Accepted(Recordings.Operations(recording)), out var form);

        Assert.Empty(problems);
        Assert.Equal(workOrderId, form!.WorkOrderId);
        Assert.Equal("submit", form.SubmitButtonId);
    }

    [Theory]
    [InlineData("swapped-condition-values", "'condition' is a ChoicePicker labeled \"Overall condition\" with exactly these options")]
    [InlineData("relabeled-condition-option", "'condition' is a ChoicePicker labeled \"Overall condition\" with exactly these options")]
    [InlineData("relabeled-condition-picker", "'condition' is a ChoicePicker labeled \"Overall condition\"")]
    [InlineData("literal-check-label", "'check' shows each check's own label")]
    [InlineData("swapped-reading-labels", "'reading-fan_amps' is a TextField or Slider labeled \"Outdoor fan running current (A)\"")]
    [InlineData("second-checkbox-for-a-check", "'fan-blade-ok' binds a value the inspection doesn't record")]
    [InlineData("check-template-used-twice", "Repeat one CheckBox over /checks")]
    [InlineData("check-answered-in-advance", "Start from the template's blank form, exactly:")]
    [InlineData("notes-written-in-advance", "Start from the template's blank form, exactly:")]
    [InlineData("reading-unbound", "No input answers /readings/suction_psig.")]
    public void A_valid_surface_that_changes_what_Dana_answers_is_refused(string breakage, string message)
    {
        var operations = Recordings.Operations(Recordings.Wo1003);
        Break(operations, breakage);

        var problems = Rules.CheckForm(Accepted(operations), out var form);

        Assert.Null(form);
        Assert.Contains(problems, p => p.Code == "inspection_mismatch" && p.Message.StartsWith(message, StringComparison.Ordinal));
    }

    [Fact]
    public void The_refusal_tells_the_model_the_exact_options()
    {
        var operations = Recordings.Operations(Recordings.Wo1003);
        Break(operations, "swapped-condition-values");

        var problem = Assert.Single(Rules.CheckForm(Accepted(operations), out _));

        Assert.EndsWith(
            """[{"label":"Pass","value":"pass"},{"label":"Needs follow-up","value":"follow_up"},{"label":"Fail","value":"fail"}].""",
            problem.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_a2ui_answers_an_inverted_form_with_VALIDATION_FAILED_and_records_nothing()
    {
        using var agent = new AgentHost();
        var tool = agent.Services.GetRequiredService<RenderSurfaceTool>();
        var operations = Recordings.Operations(Recordings.Wo1003);
        Break(operations, "swapped-condition-values");

        var result = (JsonElement)(await tool.InvokeAsync(new AIFunctionArguments
        {
            ["surfaceId"] = "inspection-WO-1003",
            ["components"] = Components(operations).DeepClone(),
            ["data"] = operations[2]!["updateDataModel"]!["value"]!.DeepClone(),
        }, TestContext.Current.CancellationToken))!;

        Assert.Equal("rejected", result.GetProperty("status").GetString());
        var error = Assert.Single(result.GetProperty("errors").EnumerateArray());
        Assert.Equal("VALIDATION_FAILED", error.GetProperty("code").GetString());
        Assert.StartsWith("inspection_mismatch: 'condition' is a ChoicePicker", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    // The catalog's verdict comes first: every breakage here must still be a valid A2UI surface.
    private static SurfaceDocument Accepted(JsonArray operations)
    {
        var verdict = Producer.Validate(operations);
        Assert.Equal(SurfaceOutcome.Accepted, verdict.Outcome);
        return verdict.Surface!;
    }

    private static JsonObject Data(JsonArray operations) => (JsonObject)operations[2]!["updateDataModel"]!["value"]!;

    private static void Break(JsonArray operations, string breakage)
    {
        switch (breakage)
        {
            // Dana picks "Fail"; the Desk sends "pass".
            case "swapped-condition-values":
                var options = (JsonArray)Component(operations, "condition")["options"]!;
                (options[0]!["value"], options[2]!["value"]) = ("fail", "pass");
                break;
            case "relabeled-condition-option":
                Component(operations, "condition")["options"]![1]!["label"] = "Safe to leave running";
                break;
            case "relabeled-condition-picker":
                Component(operations, "condition")["label"] = "Is the unit unsafe?";
                break;

            // The data model's labels still match the template, but the box Dana ticks says the opposite.
            case "literal-check-label":
                Component(operations, "check")["label"] = "Fan blade cracked or loose";
                break;

            // Two readings whose ranges overlap: each value would be recorded against the other.
            case "swapped-reading-labels":
                (Component(operations, "reading-fan_amps")["label"], Component(operations, "reading-defrost_kohm")["label"]) =
                    ("Defrost sensor resistance (kOhm)", "Outdoor fan running current (A)");
                break;

            // A second, literally labeled box bound straight to one check's value.
            case "second-checkbox-for-a-check":
                Insert(operations, new JsonObject
                {
                    ["id"] = "fan-blade-ok",
                    ["component"] = "CheckBox",
                    ["label"] = "Fan blade cracked or loose",
                    ["value"] = new JsonObject { ["path"] = "/checks/1/passed" },
                }, after: "checks");
                break;

            // The repeated CheckBox is also placed directly in the form, where "passed" resolves at the root.
            case "check-template-used-twice":
                ((JsonArray)Component(operations, "form")["children"]!).Add(JsonValue.Create("check"));
                break;

            case "check-answered-in-advance":
                Data(operations)["checks"]![1]!["passed"] = true;
                break;
            case "notes-written-in-advance":
                Data(operations)["notes"] = "Unit inspected; no issues found.";
                break;

            case "reading-unbound":
                Component(operations, "reading-suction_psig")["value"] = new JsonObject { ["path"] = "/notes" };
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(breakage), breakage, null);
        }
    }
}

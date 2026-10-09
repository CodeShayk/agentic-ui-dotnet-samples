using System.Text.Json.Nodes;
using Ch20.DeclarativeUi;
using static Ch20.Tests.Surfaces;

namespace Ch20.Tests;

/// <summary>
/// The validator on both ends, on the surfaces the agent actually sent (captured with curl -N) and on
/// copies of them broken on purpose: an unknown element degrades on the Desk and is rejected by the agent;
/// a malformed, oversized or executable tree is rejected by both.
/// </summary>
public sealed class CatalogValidationTests
{
    [Fact]
    public void The_recorded_surface_is_accepted_by_the_agent_and_by_the_Desk()
    {
        var operations = Recordings.Operations(Recordings.Wo1003);

        var server = Producer.Validate(operations);
        var desk = Desk.Validate(operations);

        Assert.Equal(SurfaceOutcome.Accepted, server.Outcome);
        Assert.Equal(SurfaceOutcome.Accepted, desk.Outcome);
        Assert.Empty(desk.Problems);
        Assert.Equal("inspection-WO-1003", desk.Surface!.SurfaceId);
        Assert.Equal(BeaconCatalog.Id, desk.Surface.CatalogId);
        Assert.Equal(15, desk.Surface.Components.Count);
    }

    [Fact]
    public void An_unknown_element_degrades_on_the_Desk_and_is_rejected_by_the_agent()
    {
        var operations = Recordings.Operations(Recordings.Wo1003);
        Insert(operations, new JsonObject
        {
            ["id"] = "fan-gauge",
            ["component"] = "Gauge",
            ["label"] = "Fan speed",
            ["value"] = new JsonObject { ["path"] = "/readings/fan_amps" },
        });

        var desk = Desk.Validate(operations);
        Assert.Equal(SurfaceOutcome.Degraded, desk.Outcome);
        var warning = Assert.Single(desk.Problems);
        Assert.Equal(("unknown_component", false), (warning.Code, warning.IsError));
        Assert.False(desk.Surface!.Components["fan-gauge"].IsKnown);
        Assert.True(desk.Surface.Components["reading-fan_amps"].IsKnown); // the rest of the form is intact

        var server = Producer.Validate(operations);
        Assert.Equal(SurfaceOutcome.Rejected, server.Outcome);
        Assert.Contains(server.Errors, p => p.Code == "unknown_component" && p.Path == "/1/updateComponents/components/15/component");
    }

    [Fact]
    public void A_catalog_component_this_Desk_cannot_draw_yet_degrades()
    {
        var operations = Recordings.Operations(Recordings.Wo1005);

        Assert.Equal(SurfaceOutcome.Accepted, Producer.Validate(operations).Outcome); // the agent's catalog has Slider
        var desk = Desk.Validate(operations);
        Assert.Equal(SurfaceOutcome.Degraded, desk.Outcome);
        Assert.Equal("unknown_component", Assert.Single(desk.Problems).Code);
        Assert.Equal("Slider", desk.Surface!.Components["reading-vibration"].Type);
    }

    [Theory]
    [InlineData("cycle", "child_cycle")]
    [InlineData("dangling-child", "unresolved_child")]
    [InlineData("no-root", "no_root")]
    [InlineData("duplicate-id", "duplicate_id")]
    [InlineData("wrong-catalog", "wrong_catalog")]
    [InlineData("second-surface", "surface_mismatch")]
    [InlineData("missing-required", "missing_required_prop")]
    [InlineData("bad-variant", "invalid_value")]
    [InlineData("old-version", "unsupported_version")]
    [InlineData("two-operations-in-one", "invalid_operation")]
    [InlineData("delete-surface", "unsupported_operation")]
    public void A_malformed_tree_is_rejected_by_both_ends(string breakage, string code)
    {
        var operations = Recordings.Operations(Recordings.Wo1003);
        Break(operations, breakage);

        foreach (var validator in new[] { Producer, Desk })
        {
            var verdict = validator.Validate(operations);
            Assert.Equal(SurfaceOutcome.Rejected, verdict.Outcome);
            Assert.Null(verdict.Surface);
            Assert.Contains(verdict.Errors, p => p.Code == code);
        }
    }

    [Fact]
    public void A_rejection_points_at_the_problem_with_a_JSON_Pointer()
    {
        var operations = Recordings.Operations(Recordings.Wo1003);
        Break(operations, "dangling-child");

        var error = Assert.Single(Desk.Validate(operations).Errors);
        Assert.Equal("/1/updateComponents/components/13/child", error.Path);
        Assert.Equal("'submit' refers to 'submit-label', which no component defines.", error.Message);
    }

    [Fact]
    public void An_oversized_payload_is_rejected_before_it_is_walked()
    {
        var operations = Recordings.Operations(Recordings.Wo1003);
        ((JsonObject)operations[2]!["updateDataModel"]!["value"]!)["notes"] = new string('x', 70_000);

        var verdict = Desk.Validate(operations);

        var error = Assert.Single(verdict.Problems);
        Assert.Equal("too_large", error.Code);
    }

    [Fact]
    public void Too_many_components_are_rejected()
    {
        var operations = Recordings.Operations(Recordings.Wo1003);
        for (var i = 0; i < 120; i++)
        {
            Components(operations).Add(new JsonObject { ["id"] = $"filler-{i}", ["component"] = "Divider" });
        }

        Assert.Equal("too_many_components", Assert.Single(Desk.Validate(operations).Problems).Code);
    }

    [Fact]
    public void A_tree_deeper_than_the_limit_is_rejected()
    {
        var operations = Recordings.Operations(Recordings.Wo1003);
        var children = (JsonArray)Component(operations, "form")["children"]!;
        children.Add(JsonValue.Create("nest-0"));
        for (var i = 0; i < 12; i++)
        {
            Components(operations).Add(new JsonObject { ["id"] = $"nest-{i}", ["component"] = "Column", ["children"] = new JsonArray($"nest-{i + 1}") });
        }

        Components(operations).Add(new JsonObject { ["id"] = "nest-12", ["component"] = "Divider" });

        Assert.Contains(Desk.Validate(operations).Errors, p => p.Code == "too_deep");
    }

    [Theory]
    [InlineData("local-function-action", "local_action_not_allowed")]
    [InlineData("open-url-check", "function_not_allowed")]
    [InlineData("format-string-text", "function_not_allowed")]
    [InlineData("function-in-unknown-component", "local_action_not_allowed")]
    public void Executable_content_is_rejected_by_both_ends(string breakage, string code)
    {
        var operations = Recordings.Operations(Recordings.Wo1003);
        Break(operations, breakage);

        Assert.Contains(Producer.Validate(operations).Errors, p => p.Code == code);
        var desk = Desk.Validate(operations);
        Assert.Equal(SurfaceOutcome.Rejected, desk.Outcome);
        Assert.Contains(desk.Errors, p => p.Code == code);
    }

    [Theory]
    [InlineData("https://manuals.contoso-climate.example/hp-24s/defrost-sensor.png", null)]
    [InlineData("http://manuals.contoso-climate.example/hp-24s/defrost-sensor.png", "url_not_allowed")]
    [InlineData("https://images.example.net/pixel.png", "url_not_allowed")]
    [InlineData("https://user@manuals.contoso-climate.example/a.png", "url_not_allowed")]
    [InlineData("javascript:alert(1)", "url_not_allowed")]
    public void Images_come_only_from_allowed_https_origins(string url, string? code)
    {
        var operations = Recordings.Operations(Recordings.Wo1003);
        Insert(operations, new JsonObject { ["id"] = "diagram", ["component"] = "Image", ["url"] = url, ["description"] = "Where the defrost sensor clips on" });

        var verdict = Desk.Validate(operations);

        if (code is null)
        {
            Assert.Equal(SurfaceOutcome.Accepted, verdict.Outcome);
        }
        else
        {
            Assert.Equal(code, Assert.Single(verdict.Errors).Code);
        }
    }

    [Fact]
    public void A_URL_bound_to_data_is_rejected_because_data_can_change_after_the_check()
    {
        var operations = Recordings.Operations(Recordings.Wo1003);
        Insert(operations, new JsonObject { ["id"] = "diagram", ["component"] = "Image", ["url"] = new JsonObject { ["path"] = "/notes" } });

        Assert.Equal("bound_url_not_allowed", Assert.Single(Desk.Validate(operations).Errors).Code);
    }

    [Fact]
    public void An_unknown_property_is_dropped_on_the_Desk_and_rejected_by_the_agent()
    {
        var operations = Recordings.Operations(Recordings.Wo1003);
        Component(operations, "title")["onClick"] = "alert(1)";

        var desk = Desk.Validate(operations);
        Assert.Equal(SurfaceOutcome.Degraded, desk.Outcome);
        Assert.False(desk.Surface!.Components["title"].Properties.ContainsKey("onClick"));

        Assert.Contains(Producer.Validate(operations).Errors, p => p.Code == "unknown_property");
    }

    [Fact]
    public void The_agent_requires_every_absolute_binding_to_exist_in_the_data_model()
    {
        var operations = Recordings.Operations(Recordings.Wo1003);
        Component(operations, "reading-fan_amps")["value"] = new JsonObject { ["path"] = "/readings/fan_rpm" };

        Assert.Contains(Producer.Validate(operations).Errors, p => p.Code == "unresolved_binding");
        Assert.Equal(SurfaceOutcome.Accepted, Desk.Validate(operations).Outcome); // a consumer renders it empty
    }

    private static void Break(JsonArray operations, string breakage)
    {
        switch (breakage)
        {
            case "cycle":
                ((JsonArray)Component(operations, "form")["children"]!).Add(JsonValue.Create("root"));
                break;
            case "dangling-child":
                Components(operations).Remove(Component(operations, "submit-label"));
                break;
            case "no-root":
                Component(operations, "root")["id"] = "start";
                break;
            case "duplicate-id":
                Components(operations).Add(new JsonObject { ["id"] = "title", ["component"] = "Divider" });
                break;
            case "wrong-catalog":
                operations[0]!["createSurface"]!["catalogId"] = "https://a2ui.org/specification/v0_9/basic_catalog.json";
                break;
            case "second-surface":
                operations[2]!["updateDataModel"]!["surfaceId"] = "inspection-WO-1001";
                break;
            case "missing-required":
                Component(operations, "check").Remove("label");
                break;
            case "bad-variant":
                Component(operations, "title")["variant"] = "marquee";
                break;
            case "old-version":
                operations[1]!["version"] = "v0.8";
                break;
            case "two-operations-in-one":
                operations[0]!["deleteSurface"] = new JsonObject { ["surfaceId"] = "inspection-WO-1003" };
                break;
            case "delete-surface":
                operations.Add(new JsonObject { ["version"] = "v0.9", ["deleteSurface"] = new JsonObject { ["surfaceId"] = "inspection-WO-1003" } });
                break;
            case "local-function-action":
                Component(operations, "submit")["action"] = new JsonObject { ["functionCall"] = new JsonObject { ["call"] = "openUrl", ["args"] = new JsonObject { ["url"] = "https://example.net" } } };
                break;
            case "open-url-check":
                ((JsonArray)Component(operations, "notes")["checks"]!).Add(new JsonObject
                {
                    ["condition"] = new JsonObject { ["call"] = "openUrl", ["args"] = new JsonObject { ["url"] = "https://example.net" } },
                    ["message"] = "Opened.",
                });
                break;
            case "format-string-text":
                Component(operations, "title")["text"] = new JsonObject { ["call"] = "formatString", ["args"] = new JsonObject { ["value"] = "${/notes}" } };
                break;
            case "function-in-unknown-component":
                Insert(operations, new JsonObject
                {
                    ["id"] = "fancy",
                    ["component"] = "Gauge",
                    ["label"] = "Fan speed",
                    ["action"] = new JsonObject { ["functionCall"] = new JsonObject { ["call"] = "openUrl" } },
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(breakage), breakage, null);
        }
    }
}

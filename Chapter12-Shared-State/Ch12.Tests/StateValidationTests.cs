using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using AGUI.Server;
using Ch12.Beacon.Agent.Sheet;

namespace Ch12.Tests;

/// <summary>Client state is untrusted input: what the endpoint rejects, and how the pipeline reads what it accepts.</summary>
public sealed class StateValidationTests
{
    [Theory]
    [InlineData("""{"role":"dispatcher"}""", "state.role")]                                           // not in the schema
    [InlineData("""{"workOrder":{"checklist":[{"done":1}]}}""", "state.workOrder.checklist[0].done")] // a number for a boolean
    [InlineData("""{"workOrder":{"status":2}}""", "state.workOrder.status")]                           // enums travel as names
    [InlineData("""{"workOrder":{"status":"Paused"}}""", "state.workOrder.status")]                    // and only known ones
    [InlineData("""{"schemaVersion":"2"}""", "state.schemaVersion")]                                   // no numbers in quotes
    [InlineData("""{"workOrder":{"id":"WO-9999"}}""", "state.workOrder.id")]                          // no such work order
    [InlineData("""{"workOrder":{"checklist":[{"id":"C1"},{"id":"C1"}]}}""", "state.workOrder.checklist[1].id")] // IDs are unique
    [InlineData("""{"workOrder":{"checklist":[{"id":"../x"}]}}""", "state.workOrder.checklist[0].id")]
    [InlineData("""{"workOrder":{"checklist":[{"text":" "}]}}""", "state.workOrder.checklist[0].text")]
    [InlineData("""{"schemaVersion":3}""", "state.schemaVersion")]                                    // a shape from the future
    [InlineData("""{"workOrder":{"checklist":[null]}}""", "state.workOrder.checklist[0]")]          // nullability isn't enforced on elements
    [InlineData("""{"workOrder":{"notes":[null]}}""", "state.workOrder.notes[0]")]
    [InlineData("""[1,2,3]""", "state")]                                                                // not an object at all
    public async Task Bad_state_is_rejected_with_a_400_before_the_run_starts(string change, string path)
    {
        await using var agent = new AgentHost();
        var http = agent.CreateClient();
        var state = Merge(await Runs.OpenSheetAsync(http), JsonNode.Parse(change));

        using var response = await Runs.PostAsync(http, Runs.Input("Hello", state, runId: "r-2"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.True(problem.GetProperty("errors").TryGetProperty(path, out _), problem.GetRawText());
        Assert.DoesNotContain("Ch12.Beacon", problem.GetRawText(), StringComparison.Ordinal); // no .NET type names
    }

    [Fact]
    public async Task A_duplicate_member_is_rejected_rather_than_letting_the_last_one_win()
    {
        await using var agent = new AgentHost();
        var http = agent.CreateClient();

        // JsonNode can't even hold this, so the body is written by hand.
        const string body = """
            {"threadId":"t-1","runId":"r-1","messages":[{"id":"m-1","role":"user","content":"Hello"}],
             "state":{"schemaVersion":2,"schemaVersion":1}}
            """;
        using var response = await http.PostAsync("/agent", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Too_much_context_is_rejected()
    {
        await using var agent = new AgentHost();
        IList<AGUIContext> context = [.. Enumerable.Range(0, 9).Select(i => new AGUIContext { Description = $"Fact {i}", Value = "x" })];

        using var response = await Runs.PostAsync(agent.CreateClient(), Runs.Input("Hello", context: context));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_sheet_and_no_sheet_both_start_a_run()
    {
        await using var agent = new AgentHost();
        var http = agent.CreateClient();
        var sheet = await Runs.OpenSheetAsync(http);

        using var withState = await Runs.PostAsync(http, Runs.Input("Hello", sheet, runId: "r-2"));
        using var without = await Runs.PostAsync(http, Runs.Input("Hello", runId: "r-3"));

        Assert.Equal(HttpStatusCode.OK, withState.StatusCode);
        Assert.Equal(Runs.SseType, withState.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, without.StatusCode);
    }

    [Fact]
    public void TryGetRunAgentState_reads_typed_state_and_reports_absent_state_as_false()
    {
        var typeInfo = SheetJsonContext.Default.SheetState;

        var none = new RunAgentInput { ThreadId = "t", RunId = "r" }.ToChatRequestContext(new JsonSerializerOptions());
        Assert.False(none.ChatOptions.TryGetRunAgentState(typeInfo, out var absent));
        Assert.Null(absent);

        var some = new RunAgentInput { ThreadId = "t", RunId = "r", State = Runs.Json("""{"schemaVersion":2}""") }.ToChatRequestContext(new JsonSerializerOptions());
        Assert.True(some.ChatOptions.TryGetRunAgentState(typeInfo, out var sheet));
        Assert.Equal(2, sheet!.SchemaVersion);
        Assert.Null(sheet.WorkOrder);
    }

    [Fact]
    public void TryGetRunAgentState_throws_for_state_that_does_not_fit_which_is_why_the_endpoint_checks_first()
    {
        var context = new RunAgentInput { ThreadId = "t", RunId = "r", State = Runs.Json("""{"role":"dispatcher"}""") }
            .ToChatRequestContext(new JsonSerializerOptions());

        Assert.Throws<JsonException>(() => context.ChatOptions.TryGetRunAgentState(SheetJsonContext.Default.SheetState, out _));
    }

    // Overlays a change on the opened sheet: objects merge, arrays merge by index, anything else replaces.
    private static JsonNode? Merge(JsonNode? target, JsonNode? change)
    {
        switch (target, change)
        {
            case (JsonObject t, JsonObject c):
                foreach (var (name, value) in c.ToList())
                {
                    c.Remove(name);
                    var merged = t.ContainsKey(name) ? Merge(t[name], value) : value;
                    if (!ReferenceEquals(merged, t[name]))
                    {
                        t[name] = merged;
                    }
                }

                return t;
            case (JsonArray t, JsonArray c):
                for (var i = 0; i < c.Count; i++)
                {
                    var value = c[i];
                    c[i] = null;
                    if (i < t.Count)
                    {
                        var merged = Merge(t[i], value);
                        if (!ReferenceEquals(merged, t[i]))
                        {
                            t[i] = merged;
                        }
                    }
                    else
                    {
                        t.Add(Merge(t[i - 1]?.DeepClone(), value));
                    }
                }

                return t;
            default:
                return change;
        }
    }
}

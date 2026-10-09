using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Client;
using Beacon.Client.Core;
using Beacon.Domain;
using Beacon.Testing;
using Ch20.DeclarativeUi;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static Ch20.Tests.Surfaces;

namespace Ch20.Tests;

/// <summary>
/// A form's action back to the agent: the Desk's client stack submits it over HTTP to the in-process
/// agent, which records the inspection and tells the model; replayed, forged, invalid, malformed and
/// expired actions are refused before anything streams.
/// </summary>
public sealed class ActionRoundTripTests : IDisposable
{
    private static readonly DateTimeOffset Monday = new(2026, 11, 16, 16, 52, 7, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(Monday.AddMinutes(-5));
    private readonly AgentHost _agent;

    public ActionRoundTripTests() => _agent = new AgentHost { TestServices = services => services.AddSingleton<TimeProvider>(_time) };

    [Fact]
    public async Task The_Desks_client_stack_submits_a_form_and_the_agent_records_it()
    {
        var requests = new RequestRecorder();
        var http = _agent.CreateDefaultClient(requests);
        var tap = WireEventTap.ForHttp(http, "http://localhost/agent");
        using var client = new AGUIChatClient(new AGUIChatClientOptions { Transport = tap });
        using var conversation = new AgentConversation(client, new ConversationOptions { Tap = tap });
        var board = new SurfaceBoard(Desk);

        await conversation.SendAsync(new ChatMessage(ChatRole.User, "Start the inspection checklist for WO-1003.") { MessageId = "user-1" }, cancellationToken: Ct);
        var activity = Assert.Single(conversation.View.Messages.OfType<ActivityMessageView>());
        var surface = board.Sync(activity.Id, activity.Content);
        Assert.Equal(SurfaceOutcome.Accepted, surface.Verdict.Outcome);

        DataBindingTests.Fill(surface);
        var action = surface.CreateAction(Node(surface, "submit"), Monday)!;
        await conversation.ContinueAsync(new RunInputs { ForwardedProperties = action.ToForwardedProperties() }, Ct);

        Assert.Null(conversation.View.Failure);
        var reply = conversation.View.Messages.OfType<AssistantMessageView>().Last().Text;
        Assert.StartsWith("Recorded. Inspection of EQ-601 (Contoso Air HP-24S)", reply, StringComparison.Ordinal);
        Assert.Contains("4 of 5 checks passed; failed: outdoor fan turns freely, with no bearing noise", reply, StringComparison.Ordinal);

        var note = _agent.Services.GetRequiredService<IFieldServiceStore>().GetWorkOrder("WO-1003")!.Notes[^1];
        Assert.Contains("outdoor fan running current 2.3 A, defrost sensor resistance 10.2 kOhm, suction pressure 118 psig", note, StringComparison.Ordinal);
        Assert.EndsWith("Overall: needs follow-up. Notes: Grinding from the fan motor at start-up.", note, StringComparison.Ordinal);

        using var second = JsonDocument.Parse(requests.Bodies[1]);
        Assert.Equal("submit_inspection", second.RootElement.GetProperty("forwardedProps").GetProperty("a2uiAction").GetProperty("userAction").GetProperty("name").GetString());
        Assert.DoesNotContain(second.RootElement.GetProperty("messages").EnumerateArray(), m => m.GetProperty("role").GetString() == "activity");
    }

    [Fact]
    public async Task A_replayed_action_is_refused_before_the_stream()
    {
        await IssueAsync("t-replay");
        var first = await PostAsync(SubmitRun("t-replay", "r-2"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var again = await PostAsync(SubmitRun("t-replay", "r-3"));

        await AssertProblemAsync(again, HttpStatusCode.Conflict, "Already submitted");
    }

    [Fact]
    public async Task An_action_for_a_surface_this_thread_never_saw_is_refused()
    {
        await IssueAsync("t-other");

        var forged = await PostAsync(SubmitRun("t-forged", "r-2"));

        await AssertProblemAsync(forged, HttpStatusCode.Conflict, "Unknown surface");
    }

    [Fact]
    public async Task An_action_from_another_component_is_refused()
    {
        await IssueAsync("t-component");

        var response = await PostAsync(SubmitRun("t-component", "r-2", a => a["sourceComponentId"] = "notes"));

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "Unknown action");
    }

    [Fact]
    public async Task Values_that_do_not_fit_the_template_are_refused_field_by_field()
    {
        await IssueAsync("t-invalid");

        var response = await PostAsync(SubmitRun("t-invalid", "r-2", a =>
        {
            a["context"]!["readings"]!["fan_amps"] = "230";
            a["context"]!["condition"] = new JsonArray("follow_up", "pass");
            ((JsonArray)a["context"]!["checks"]!).RemoveAt(4);
        }));

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Invalid inspection");
        var errors = problem.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order().ToList();
        Assert.Equal(new[] { "checks", "checks.outdoor_coil", "condition", "readings.fan_amps" }, errors);
        Assert.DoesNotContain(_agent.Services.GetRequiredService<IFieldServiceStore>().GetWorkOrder("WO-1003")!.Notes, n => n.StartsWith("Inspection", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_malformed_action_is_refused()
    {
        await IssueAsync("t-malformed");

        var response = await PostAsync(SubmitRun("t-malformed", "r-2", a => a["name"] = "Submit Inspection"));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Malformed action");
    }

    [Fact]
    public async Task An_expired_form_is_refused()
    {
        await IssueAsync("t-expired");
        _time.Advance(TimeSpan.FromMinutes(121));

        var response = await PostAsync(SubmitRun("t-expired", "r-2"));

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "Form expired");
    }

    public void Dispose() => _agent.Dispose();

    private Task IssueAsync(string threadId) =>
        AGUIWire.RunRawAsync(_agent.CreateClient(), "/agent", AGUIWire.UserRun("Start the inspection checklist for WO-1003.", threadId, "r-1"), Ct);

    // The request the Desk's client stack sent to the running agent (captured), re-addressed to this thread.
    private static string SubmitRun(string threadId, string runId, Action<JsonObject>? edit = null)
    {
        var input = (JsonObject)JsonNode.Parse(File.ReadAllText(Recordings.PathOf(Recordings.SubmitRequest)))!;
        input["threadId"] = threadId;
        input["runId"] = runId;
        edit?.Invoke((JsonObject)input["forwardedProps"]!["a2uiAction"]!["userAction"]!);
        return input.ToJsonString();
    }

    private async Task<HttpResponseMessage> PostAsync(string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/agent") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("text/event-stream");
        var response = await _agent.CreateClient().SendAsync(request, Ct);
        await response.Content.LoadIntoBufferAsync(Ct);
        return response;
    }

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string title)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(title, document.RootElement.GetProperty("title").GetString());
        return document.RootElement.Clone();
    }

    /// <summary>Keeps every request body the client sends.</summary>
    private sealed class RequestRecorder : DelegatingHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return await base.SendAsync(request, cancellationToken);
        }
    }
}

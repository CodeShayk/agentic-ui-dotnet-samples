using System.Text.Json;
using AGUI.Abstractions;
using Beacon.Testing;
using Ch09.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Ch09.Tests;

/// <summary>
/// What each audience gets when a tool fails: the model and the Desk see a sanitized
/// <c>{"error": "..."}</c> result with a reference, the log holds the exception, and the run itself
/// still succeeds. Plus what happens without the boundary, and an expected failure that is just a result.
/// </summary>
public sealed class ToolErrorTests
{
    [Fact]
    public async Task A_failing_tool_reaches_the_model_and_the_Desk_as_a_sanitized_error_with_a_reference()
    {
        await using var agent = new AgentHost();
        var events = await agent.RunAsync("Prepare WO-1005 for Elena");

        // The run carries on: a tool failure is a result, not a run failure.
        Assert.Empty(events.OfType<RunErrorEvent>());
        Assert.IsType<RunFinishedEvent>(events[^1]);

        var results = events.OfType<ToolCallResultEvent>().ToList();
        Assert.Equal(3, results.Count);

        using var warranty = JsonDocument.Parse(results[2].Content.ToString());
        var error = warranty.RootElement.GetProperty("error").GetString()!;
        Assert.StartsWith("The warranty service did not answer, so this lookup failed.", error);
        var reference = error[^7..^1];
        Assert.Matches("^[0-9A-F]{6}$", reference);

        // The other two lookups are unaffected.
        Assert.All(results.Take(2), r => Assert.DoesNotContain("\"error\"", r.Content.ToString()));

        // Nothing internal reaches the wire: not the host names, not the exception's text.
        var wire = string.Join('\n', events.Select(e => AGUIWire.ToWireJson(e).GetRawText()));
        Assert.DoesNotContain("contoso-air.internal", wire);
        Assert.DoesNotContain("503", wire);

        // The model explains it to Dana, reference included.
        var answer = string.Concat(events.OfType<TextMessageContentEvent>().Select(e => e.Delta));
        Assert.Contains($"I could not check the warranty: The warranty service did not answer", answer);
        Assert.Contains($"(Reference {reference})", answer);

        // The log has the details, under the same reference, and the call that failed.
        var entry = Assert.Single(agent.Logs, l => l.Level == LogLevel.Error);
        Assert.Equal("Ch09.Beacon.Agent.Tools.ToolErrorBoundary", entry.Category);
        Assert.Equal($"Tool check_warranty failed on call {results[2].ToolCallId} (reference {reference}).", entry.Message);
        Assert.Contains("warranty-db-02.contoso-air.internal", Assert.IsType<HttpRequestException>(entry.Exception).Message);
    }

    [Fact]
    public async Task Without_the_boundary_the_model_sees_function_invocations_generic_text()
    {
        await using var agent = new AgentHost(new Dictionary<string, string?> { ["Beacon:ToolErrorBoundary"] = "false" });
        var events = await agent.RunAsync("Prepare WO-1005 for Elena");

        // FunctionInvokingChatClient catches the exception and hands the model a fixed string, which
        // AGUI.Server sends on as the result. Safe, but nothing in it says "this failed" to a UI.
        var warranty = events.OfType<ToolCallResultEvent>().Last();
        Assert.Equal("Error: Function failed.", warranty.Content.ToString());

        var entry = Assert.Single(agent.Logs, l => l.Level == LogLevel.Error);
        Assert.Equal("Microsoft.Extensions.AI.FunctionInvokingChatClient", entry.Category);
        Assert.IsType<HttpRequestException>(entry.Exception);
    }

    [Fact]
    public async Task An_expected_failure_is_an_ordinary_result_and_logs_nothing()
    {
        await using var agent = new AgentHost();
        var events = await agent.RunAsync("What's the status of WO-9999?");

        var result = Assert.Single(events.OfType<ToolCallResultEvent>());
        using var content = JsonDocument.Parse(result.Content.ToString());
        Assert.Equal("Work order WO-9999 was not found.", content.RootElement.GetProperty("error").GetString());

        Assert.DoesNotContain(agent.Logs, l => l.Level >= LogLevel.Warning);
    }
}

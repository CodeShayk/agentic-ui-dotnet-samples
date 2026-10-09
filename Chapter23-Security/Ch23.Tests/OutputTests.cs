using System.Net.Http.Headers;
using AGUI.Abstractions;
using AGUI.Formatting;
using Beacon.Testing;
using Ch23.Tests.Infrastructure;

namespace Ch23.Tests;

/// <summary>What leaves the server: on the wire and in the logs.</summary>
public sealed class OutputTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Frames_carry_no_rawEvent_and_no_null_fields()
    {
        await using var host = Hosts.Agent();

        var frames = await AGUIWire.RunRawAsync(host.CreateClient(TestTokens.Dana), "/agent",
            Runs.Turn("thread-1", "run-1", Runs.User("user-1", "Order CMP-CA36 for WO-1001")), Ct);

        Assert.NotEmpty(frames);
        Assert.All(frames, frame =>
        {
            Assert.False(frame.TryGetProperty("rawEvent", out _));
            Assert.Empty(AGUIWire.NullProperties(frame));
        });

        // The approval says when it stops being answerable.
        var interrupt = frames[^1].GetProperty("outcome").GetProperty("interrupts")[0];
        Assert.True(DateTimeOffset.TryParse(interrupt.GetProperty("expiresAt").GetString(), out var expiresAt));
        var due = host.Clock.GetUtcNow().AddMinutes(10);
        Assert.InRange(expiresAt, due.AddSeconds(-1), due);
    }

    [Fact]
    public async Task RUN_STARTED_declares_AG_UI_1_0_live_and_when_a_thread_is_restored()
    {
        await using var host = Hosts.Agent();
        var dana = host.CreateClient(TestTokens.Dana);

        var live = await AGUIWire.RunRawAsync(dana, "/agent",
            Runs.Turn("thread-1", "run-1", Runs.User("user-1", "What is the status of WO-1001?")), Ct);
        Assert.Equal("1.0", live[0].GetProperty("protocolVersion").GetString());

        using var request = new HttpRequestMessage(HttpMethod.Get, "/agent/threads/thread-1/events");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(SseEventStreamFormatter.ServerSentEventsMediaType));
        using var response = await dana.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        await using var body = await response.Content.ReadAsStreamAsync(Ct);
        await foreach (var evt in new SseEventStreamFormatter().ReadAsync(body, Ct))
        {
            Assert.Equal("1.0", Assert.IsType<RunStartedEvent>(evt).ProtocolVersion);
            break;
        }
    }

    [Fact]
    public async Task Tool_results_go_out_as_compact_JSON()
    {
        await using var host = Hosts.Agent();

        var events = await Runs.RunAsync(host.CreateClient(TestTokens.Priya),
            Runs.Turn("thread-1", "run-1", Runs.User("user-1", "Order CMP-CA36 for WO-1001")), Ct);

        // A read and a refusal at the tool, both stored and echoed back every turn: no indentation, no line breaks.
        var results = events.OfType<ToolCallResultEvent>().Select(r => r.Content.ToString()).ToList();
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.DoesNotContain('\n', r));
    }

    [Fact]
    public async Task Logs_hash_user_IDs_and_erase_conversation_content()
    {
        await using var host = Hosts.Agent();
        var http = host.CreateClient(TestTokens.Dana);

        var (interrupt, _) = await ApprovalEnforcementTests.AskForCompressorAsync(http);
        await Runs.RunAsync(http, ApprovalEnforcementTests.Resume("run-2", Runs.Answer(interrupt.Id, true)), Ct);

        var security = host.Logs.Where(l => l.EventId is >= 2301 and <= 2310).ToList();
        Assert.NotEmpty(security);

        // "dana" never appears; a keyed hash with key ID 2301 does, the same one every time.
        Assert.All(security, l => Assert.DoesNotContain("dana", l.Message, StringComparison.OrdinalIgnoreCase));
        var hashes = security.Select(l => System.Text.RegularExpressions.Regex.Match(l.Message, @"2301:[A-Za-z0-9+/=]+").Value).Distinct().ToList();
        Assert.Single(hashes);

        // The audit's arguments and reasons are conversation content: erased in the log, kept in the entry.
        var executed = Assert.Single(security, l => l.EventId == 2310 && l.Message.StartsWith("Executed", StringComparison.Ordinal));
        Assert.Contains("place_part_order ''", executed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("CMP-CA36", executed.Message, StringComparison.Ordinal);
        Assert.Contains("CMP-CA36",
            ApprovalEnforcementTests.Audit(host).Entries.Single(e => e.Event == Ch23.Beacon.Security.Approvals.ApprovalEvent.Executed).Arguments,
            StringComparison.Ordinal);
    }
}

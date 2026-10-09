using System.Net;
using System.Text.Json;
using AGUI.Abstractions;
using Beacon.Testing;
using Ch19.Beacon.Agent.Capabilities;
using Ch19.Tests.Infrastructure;

namespace Ch19.Tests;

/// <summary>The agent keeps its word: what the document says it doesn't do, its runs don't do.</summary>
public sealed class DeclarationEnforcementTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static readonly AGUITool OpenWorkOrder = new()
    {
        Name = "open_work_order",
        Description = "Opens a work order on the dispatcher's screen.",
        Parameters = JsonDocument.Parse("""{"type":"object","properties":{"workOrderId":{"type":"string"}},"required":["workOrderId"]}""").RootElement,
    };

    [Fact]
    public async Task The_reduced_agent_refuses_a_recording_before_the_run_starts()
    {
        await using var host = new AgentHost(CapabilityProfile.Reduced);
        var input = Run(TestMedia.UserMessage("u-1", "Here's the noise EQ-501 makes.", TestMedia.AudioPart()));

        using var response = await ClientStack.PostAsync(host.CreateClient(), input);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel));
        Assert.Equal("Attachment not accepted", problem.RootElement.GetProperty("title").GetString());
        Assert.Contains("multimodal.input.audio is false", problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_old_attachment_in_the_history_is_dropped_instead_of_failing_every_later_turn()
    {
        await using var host = new AgentHost(CapabilityProfile.Reduced);
        var input = Run(
            TestMedia.UserMessage("u-1", "Nameplate for WO-1001.", TestMedia.ImagePart()),
            new AGUIAssistantMessage { Id = "a-1", Content = "Thanks." },
            new AGUIUserMessage { Id = "u-2", Content = "What is the status of WO-1001?" });

        var events = await AGUIWire.RunAsync(host.CreateClient(), "/agent", input, Cancel);

        Assert.IsType<RunFinishedEvent>(events[^1]);
        Assert.Single(host.Logs.WithEventId(1912));
    }

    [Fact]
    public async Task The_full_agent_takes_the_photo_and_its_model_receives_it()
    {
        await using var host = new AgentHost(CapabilityProfile.Full);
        var input = Run(TestMedia.UserMessage("u-1", "Nameplate for WO-1001.", TestMedia.ImagePart()));

        var events = await AGUIWire.RunAsync(host.CreateClient(), "/agent", input, Cancel);

        Assert.Contains("your photo (image/png", Text(events), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_and_by_reference_attachments_are_refused_even_when_the_kind_is_accepted()
    {
        await using var host = new AgentHost(CapabilityProfile.Full);
        var big = Run(TestMedia.UserMessage("u-1", "Big photo.", TestMedia.ImagePart(new byte[6 * 1024 * 1024])));
        var byUrl = Run(TestMedia.UserMessage("u-1", "Photo by URL.", new AGUIImageInputContent
        {
            Source = new AGUIInputContentUrlSource { Value = "http://169.254.169.254/latest/meta-data", MimeType = "image/png" },
        }));

        using var bigResponse = await ClientStack.PostAsync(host.CreateClient(), big);
        using var urlResponse = await ClientStack.PostAsync(host.CreateClient(), byUrl);

        Assert.Equal(HttpStatusCode.BadRequest, bigResponse.StatusCode);
        Assert.Contains("larger than", await bigResponse.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, urlResponse.StatusCode);
        Assert.Contains("inline data only", await urlResponse.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CapabilityProfile.Full, true)]
    [InlineData(CapabilityProfile.Reduced, false)]
    public async Task Client_tools_reach_the_model_only_where_they_are_declared_accepted(CapabilityProfile profile, bool screenToolCalled)
    {
        await using var host = new AgentHost(profile);
        var input = AGUIWire.UserRun("Open WO-1001 for me.", "t-19", "r-1");
        input.Tools = [OpenWorkOrder];

        var events = await AGUIWire.RunAsync(host.CreateClient(), "/agent", input, Cancel);
        var calls = events.OfType<ToolCallStartEvent>().Select(e => e.ToolCallName).ToList();

        Assert.Equal(screenToolCalled, calls.Contains("open_work_order"));
        if (!screenToolCalled)
        {
            Assert.Contains("get_work_order", calls); // answered with the agent's own tool instead
            Assert.Single(host.Logs.WithEventId(1911));
        }
    }

    [Theory]
    [InlineData(CapabilityProfile.Full, true)]
    [InlineData(CapabilityProfile.Reduced, false)]
    public async Task Reasoning_leaves_the_agent_only_where_it_is_declared(CapabilityProfile profile, bool reasons)
    {
        await using var host = new AgentHost(profile);

        var events = await AGUIWire.RunAsync(host.CreateClient(), "/agent", AGUIWire.UserRun("Why would EQ-501 short-cycle? Diagnose it.", "t-19", "r-1"), Cancel);

        Assert.Equal(reasons, AGUIWire.Types(events).Any(t => t.StartsWith("REASONING_", StringComparison.Ordinal)));
        Assert.Contains("failing run capacitor", Text(events), StringComparison.Ordinal); // the answer itself is the same
    }

    private static RunAgentInput Run(params AGUIMessage[] messages) => new()
    {
        ThreadId = "t-19",
        RunId = "r-1",
        ProtocolVersion = "1.0",
        Messages = [.. messages],
    };

    private static string Text(IEnumerable<BaseEvent> events) =>
        string.Concat(events.OfType<TextMessageContentEvent>().Select(e => e.Delta));
}

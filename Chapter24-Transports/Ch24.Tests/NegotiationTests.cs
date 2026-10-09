using System.Net;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Client;
using AGUI.Formatting;
using AGUI.Protobuf;
using Microsoft.Extensions.AI;

namespace Ch24.Tests;

public sealed class NegotiationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null, AgentHost.SseType)]
    [InlineData("text/event-stream", AgentHost.SseType)]
    [InlineData("*/*", AgentHost.SseType)]
    [InlineData("application/vnd.ag-ui.event+proto, text/event-stream", AgentHost.ProtobufType)]
    [InlineData("application/vnd.ag-ui.event+proto", AgentHost.ProtobufType)]
    [InlineData("application/vnd.ag-ui.event+proto;q=0, text/event-stream", AgentHost.SseType)]
    public async Task Accept_header_picks_the_format(string? accept, string expected)
    {
        await using var host = new AgentHost();
        using var http = host.CreateClient();

        using var response = await AgentHost.PostAsync(http, AgentHost.Run(), accept, Ct);
        var events = await AgentHost.ReadEventsAsync(response, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, response.Content.Headers.ContentType?.MediaType);
        Assert.IsType<RunStartedEvent>(events[0]);
        Assert.IsType<RunFinishedEvent>(events[^1]);
    }

    // Beacon's second departure from the binding, which says to answer SSE here: application/* admits protobuf
    // by the letter of the spec, but Beacon selects protobuf only by name and SSE only for text/event-stream or */*.
    [Theory]
    [InlineData("application/json")]
    [InlineData("application/*")]
    public async Task Unacceptable_format_gets_406_that_names_the_alternatives_and_runs_nothing(string accept)
    {
        await using var host = new AgentHost();
        using var http = host.CreateClient();
        var input = AgentHost.Run();

        using var refused = await AgentHost.PostAsync(http, input, accept, Ct);
        var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Ct)).RootElement;

        Assert.Equal(HttpStatusCode.NotAcceptable, refused.StatusCode);
        Assert.Contains("text/event-stream", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Contains("application/vnd.ag-ui.event+proto", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);

        // Nothing ran, so nothing was recorded: the same run ID with a usable Accept goes through.
        using var accepted = await AgentHost.PostAsync(http, input, "text/event-stream", Ct);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task Protobuf_only_client_gets_406_where_protobuf_is_off_and_a_protobuf_first_client_falls_back()
    {
        await using var host = new AgentHost(("Beacon:Streaming:EnableProtobuf", "false"));

        // The mistake: no SSE on the list.
        using var protobufOnly = host.CreateNegotiatingClient(new ProtobufEventStreamFormatter());
        using var strict = new AGUIChatClient(new AGUIChatClientOptions(protobufOnly, "/agent"));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => strict.GetResponseAsync(Question(), cancellationToken: Ct));
        Assert.Equal(HttpStatusCode.NotAcceptable, error.StatusCode);

        // Beacon Field's list: protobuf first, SSE as the fallback.
        using var protobufFirst = host.CreateNegotiatingClient(new ProtobufEventStreamFormatter(), new SseEventStreamFormatter());
        using var field = new AGUIChatClient(new AGUIChatClientOptions(protobufFirst, "/agent"));
        var answer = await field.GetResponseAsync(Question(), cancellationToken: Ct);
        Assert.Contains("WO-1001", answer.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_headers_keep_every_hop_from_caching_or_buffering()
    {
        await using var host = new AgentHost();
        using var http = host.CreateClient();

        using var response = await AgentHost.PostAsync(http, AgentHost.Run(), "text/event-stream", Ct);

        Assert.True(response.Headers.CacheControl is { NoCache: true, NoStore: true });
        Assert.Equal("no", Assert.Single(response.Headers.GetValues("X-Accel-Buffering")));
        Assert.Equal(AgentHost.SseType, response.Content.Headers.ContentType?.MediaType);
    }

    private static List<ChatMessage> Question() =>
        [new(ChatRole.User, "What is the status of WO-1001?") { MessageId = $"user_{Guid.NewGuid():N}" }];
}

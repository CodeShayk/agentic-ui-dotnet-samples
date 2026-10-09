using System.Net;
using System.Text;
using AGUI.Client;
using Beacon.Client.Core;
using Ch14.Beacon.Desk.Components.Approvals;
using Ch14.Beacon.Desk.Connection;
using Ch14.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch14.Tests;

/// <summary>
/// The agent's reason for a refusal reaches the Desk, which the SDK's transport alone would prevent: a dropped
/// answer whose order went through is never presented as one Dana should ask for again.
/// </summary>
public sealed class RefusalTests
{
    // Wire 14.4's bodies, as Ch14.Beacon.Agent wrote them, and a 400 from the same gate.
    [Theory]
    [InlineData(409, """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Interrupt not open","status":409,"detail":"Interrupt ficc_call-0001-1 is not open on this thread."}""",
        "Interrupt not open", "Interrupt ficc_call-0001-1 is not open on this thread.")]
    [InlineData(409, """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Approval does not match","status":409,"detail":"Interrupt apr_67be81b0265c9b10c4502c79c8bd7ae5 approves a different call than the one proposed."}""",
        "Approval does not match", "Interrupt apr_67be81b0265c9b10c4502c79c8bd7ae5 approves a different call than the one proposed.")]
    [InlineData(409, """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Interrupt already answered","status":409,"detail":"Interrupt apr_67be81b0265c9b10c4502c79c8bd7ae5 was answered in run r-2, and its call ran."}""",
        "Interrupt already answered", "Interrupt apr_67be81b0265c9b10c4502c79c8bd7ae5 was answered in run r-2, and its call ran.")]
    [InlineData(400, """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"Invalid approval","status":400,"detail":"An approval payload must be an object with a boolean approved property."}""",
        "Invalid approval", "An approval payload must be an object with a boolean approved property.")]
    public async Task A_problem_body_reaches_the_caller_with_its_title_and_detail(int status, string body, string title, string detail)
    {
        var refusals = new AgentRefusals();
        using var http = new HttpClient(new ProblemDetailsHandler(refusals) { InnerHandler = new Answering((HttpStatusCode)status, body, ProblemDetailsHandler.ProblemMediaType) });

        var refusal = await Assert.ThrowsAsync<AgentRefusedException>(() => http.PostAsync("http://agent.test/agent", null, Runs.Ct));

        Assert.Equal(((HttpStatusCode)status, title, detail), (refusal.StatusCode, refusal.Title, refusal.Detail));
        Assert.Same(refusal, refusals.Latest);
    }

    [Theory]
    [InlineData(409, "text/plain", "Conflict")]                     // a 4xx without a problem body
    [InlineData(503, ProblemDetailsHandler.ProblemMediaType, "{}")]  // a 5xx is the server's failure, not a refusal
    public async Task Other_responses_pass_through_and_clear_the_last_refusal(int status, string mediaType, string body)
    {
        var refusals = new AgentRefusals();
        refusals.Record(new AgentRefusedException(HttpStatusCode.Conflict, "Interrupt not open", null));
        using var http = new HttpClient(new ProblemDetailsHandler(refusals) { InnerHandler = new Answering((HttpStatusCode)status, body, mediaType) });

        using var response = await http.PostAsync("http://agent.test/agent", null, Runs.Ct);

        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Null(refusals.Latest);
    }

    [Fact]
    public async Task A_dropped_answer_whose_order_went_through_is_never_presented_as_one_to_ask_again()
    {
        await using var host = new AgentHost();
        var refusals = new AgentRefusals();
        var drop = new DropSecondRequest();
        using var http = host.CreateDefaultClient(new ProblemDetailsHandler(refusals), drop);
        http.DefaultRequestHeaders.Add(DeskClient.HeaderName, "desk-1a2b3c4d");
        var tap = WireEventTap.ForHttp(http, new Uri(http.BaseAddress!, "/agent").ToString());
        using var chat = new AGUIChatClient(new AGUIChatClientOptions { Transport = tap });
        using var conversation = new AgentConversation(chat, new ConversationOptions { Tap = tap, TimeProvider = host.Clock });

        await conversation.SendAsync(new ChatMessage(ChatRole.User,
            "Priya says the capacitor tests fine. Order a replacement compressor for WO-1001.") { MessageId = "user-1" }, cancellationToken: Runs.Ct);
        var request = ApprovalRequest.Current(conversation.View, refusals.Latest)!;

        // Dana approves. The agent places PO-9001, and the connection drops before the Desk reads RUN_STARTED.
        await ApprovalAnswers.SendAsync(conversation, request, new ApprovalDecision(ApprovalChoice.Approve), Runs.Ct);

        var lost = ApprovalRequest.Current(conversation.View, refusals.Latest)!;
        Assert.Equal(FailureKind.StreamEnded, lost.LastFailure!.Kind);
        Assert.StartsWith("Your answer may not have reached Beacon.", ApprovalDialog.Refusal(lost), StringComparison.Ordinal);
        Assert.Single(host.Store.ListPartOrders());

        // She sends it again, as the dialog invites: an exact repeat, which the agent refuses with its own 409.
        await ApprovalAnswers.SendAsync(conversation, lost, new ApprovalDecision(ApprovalChoice.Approve), Runs.Ct);

        var refused = ApprovalRequest.Current(conversation.View, refusals.Latest)!;
        Assert.Equal((FailureKind.Rejected, HttpStatusCode.Conflict), (refused.LastFailure!.Kind, refused.LastFailure.StatusCode));
        Assert.EndsWith("and its call ran.", refusals.Latest!.Detail, StringComparison.Ordinal);
        Assert.True(refused.AlreadyCarriedOut);
        Assert.Equal("Beacon already acted on this approval in an earlier run. Check the order in a new chat before asking again.",
            ApprovalDialog.Refusal(refused));
        Assert.Single(host.Store.ListPartOrders());
    }

    [Fact]
    public void The_reason_belongs_to_a_refusal_only_when_the_statuses_agree()
    {
        var approval = new ApprovalRequest
        {
            Id = "apr_1",
            ToolCallId = "call-0001-1",
            ToolName = "place_part_order",
            Title = "Approve a part order",
            Message = "Order 1 x CMP-CA36 for WO-1001: $1,795.00.",
            Arguments = default,
            LastFailure = new RunFailure(FailureKind.Rejected, "refused") { StatusCode = HttpStatusCode.Conflict },
            LastRefusalTitle = ApprovalRequest.AlreadyAnsweredTitle,
        };

        Assert.True(approval.AlreadyCarriedOut);
        Assert.False((approval with { LastFailure = new RunFailure(FailureKind.StreamEnded, "dropped") }).AlreadyCarriedOut);
        Assert.False((approval with { LastRefusalTitle = "Interrupt not open" }).AlreadyCarriedOut);
    }

    [Fact]
    public void The_agent_client_has_no_header_timeout_and_a_ten_second_connect_timeout()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddAgentHttp();
        services.AddScoped(_ => new AgentRefusals());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        using var http = AgentHttp.CreateClient(scope.ServiceProvider);

        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
        var handler = scope.ServiceProvider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(AgentHttp.ClientName);
        while (handler is DelegatingHandler { InnerHandler: { } inner })
        {
            handler = inner;
        }

        Assert.Equal(TimeSpan.FromSeconds(10), Assert.IsType<SocketsHttpHandler>(handler).ConnectTimeout);
    }

    /// <summary>Answers every request with one status and body.</summary>
    private sealed class Answering(HttpStatusCode status, string body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType), RequestMessage = request });
    }

    /// <summary>
    /// Lets the second request (the answer) run to the end on the agent, then loses the response, as a connection
    /// that drops after the server committed would.
    /// </summary>
    private sealed class DropSecondRequest : DelegatingHandler
    {
        private int _requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (Interlocked.Increment(ref _requests) != 2)
            {
                return response;
            }

            await response.Content.ReadAsByteArrayAsync(cancellationToken); // the whole run, PO-9001 included
            response.Dispose();
            throw new IOException("The connection was reset.");
        }
    }
}

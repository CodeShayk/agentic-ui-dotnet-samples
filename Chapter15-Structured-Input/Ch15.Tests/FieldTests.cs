using System.Collections.Immutable;
using System.Net;
using Beacon.Client.Core;
using Ch15.Beacon.Field;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Ch15.Tests;

/// <summary>
/// Beacon Field's status line words a refused answer as the Desk does, and both clients call the agent as
/// Chapter 6 recommends: no timeout on the wait for headers and a 10-second connect timeout.
/// </summary>
public sealed class FieldTests
{
    private static readonly InterruptView Serial = new("inp_serial", InterruptKind.Input) { Reason = "input_required" };

    [Theory]
    [InlineData(FailureKind.Rejected, HttpStatusCode.Conflict, "Beacon didn't accept those answers; the questions may have expired at the agent. Send them again, or skip them.")]
    [InlineData(FailureKind.Rejected, HttpStatusCode.BadRequest, "Beacon couldn't use those answers. Correct them and send again, or skip the questions.")]
    [InlineData(FailureKind.StreamEnded, null, "Your answers may not have reached Beacon. Send them again, or skip the questions.")]
    public void Answers_the_agent_didnt_take_say_so_and_how_to_go_on(FailureKind kind, HttpStatusCode? status, string expected)
    {
        var failure = new RunFailure(kind, "details") { StatusCode = status };
        var view = ConversationView.Empty with
        {
            Status = ConversationStatus.Failed,
            Failure = failure,
            Interrupts = ImmutableList.Create(Serial with { LastAnswerFailure = failure }),
        };

        Assert.Equal(expected, FieldStatus.Describe(view));
    }

    [Theory]
    [InlineData(FailureKind.Unreachable, null, "Beacon can't be reached. Check that the agent is running.")]
    [InlineData(FailureKind.Rejected, HttpStatusCode.Conflict, "Beacon refused the request (HTTP 409).")]
    [InlineData(FailureKind.StreamEnded, null, "Something went wrong. Try again.")]
    public void Other_failures_have_their_own_words(FailureKind kind, HttpStatusCode? status, string expected)
    {
        var view = ConversationView.Empty with { Status = ConversationStatus.Failed, Failure = new RunFailure(kind, "details") { StatusCode = status } };

        Assert.Equal(expected, FieldStatus.Describe(view));
    }

    [Fact]
    public async Task The_Desks_agent_client_has_no_header_timeout_and_a_bounded_connect()
    {
        await using var desk = new WebApplicationFactory<Ch15.Beacon.Desk.Program>();

        var http = desk.Services.GetRequiredService<IHttpClientFactory>().CreateClient("BeaconAgent");
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);

        // The factory wraps the primary handler in its own delegating handlers.
        var handler = desk.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("BeaconAgent");
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        Assert.Equal(TimeSpan.FromSeconds(10), Assert.IsType<SocketsHttpHandler>(handler).ConnectTimeout);
    }

    [Fact]
    public void The_Fields_agent_client_has_no_header_timeout_and_a_bounded_connect()
    {
        using var http = AgentHttp.Create();
        using var handler = AgentHttp.CreateHandler();

        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(10), handler.ConnectTimeout);
    }
}

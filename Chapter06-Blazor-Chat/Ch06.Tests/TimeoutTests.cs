using System.Net;
using System.Net.Sockets;
using AGUI.Client;
using Microsoft.Extensions.AI;

namespace Ch06.Tests;

/// <summary>
/// The exception DeskChat's handlers are written for. An HttpClient timeout reaches the caller of
/// AGUIChatClient as a cancellation (TaskCanceledException with an inner TimeoutException), not as an
/// HttpRequestException, while the caller's own token is not canceled.
/// </summary>
public sealed class TimeoutTests
{
    [Fact]
    public async Task A_header_timeout_arrives_as_a_cancellation_the_caller_did_not_request()
    {
        // An "agent" that accepts the connection and never sends a header, like a server waiting on a slow model.
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var accepted = silent.AcceptTcpClientAsync(TestContext.Current.CancellationToken).AsTask();
        var endpoint = $"http://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}/agent";

        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(300) };
        using IChatClient agent = new AGUIChatClient(new AGUIChatClientOptions(http, endpoint));
        using var callersOwn = new CancellationTokenSource();

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in agent.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "What's the status of WO-1001?") { MessageId = "user-1" }],
                new ChatOptions(),
                callersOwn.Token))
            {
            }
        });

        Assert.IsType<TaskCanceledException>(thrown);
        Assert.IsType<TimeoutException>(thrown.InnerException);
        Assert.False(callersOwn.IsCancellationRequested); // so a `when (token.IsCancellationRequested)` filter won't match it

        (await accepted).Dispose();
    }
}

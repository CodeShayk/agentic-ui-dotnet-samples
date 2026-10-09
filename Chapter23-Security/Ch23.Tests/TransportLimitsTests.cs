using System.Net;
using AGUI.Abstractions;
using Ch23.Beacon.Security.Output;
using Ch23.Tests.Infrastructure;

namespace Ch23.Tests;

/// <summary>CORS, rate limits, one run per thread and the run budget.</summary>
public sealed class TransportLimitsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("http://localhost:5231", true)]
    [InlineData("https://evil.example", false)]
    public async Task Only_Beacon_Desk_gets_a_CORS_grant(string origin, bool granted)
    {
        await using var host = Hosts.Agent();

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/agent");
        preflight.Headers.Add("Origin", origin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
        using var response = await host.CreateClient(token: null).SendAsync(preflight, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(granted, response.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowed) && allowed.Single() == origin);
    }

    [Fact]
    public async Task Each_user_has_their_own_run_budget()
    {
        await using var host = Hosts.Agent(new Dictionary<string, string?> { ["Beacon:Security:RunsPerMinute"] = "2" });
        var dana = host.CreateClient(TestTokens.Dana);

        for (var i = 1; i <= 2; i++)
        {
            await Runs.RunAsync(dana, Runs.Turn($"thread-{i}", $"run-{i}", Runs.User($"user-{i}", "Hello")), Ct);
        }

        using var third = await Runs.PostAsync(dana, Runs.Turn("thread-3", "run-3", Runs.User("user-3", "Hello")), Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.NotNull(third.Headers.RetryAfter);

        // Dana's budget is hers alone.
        await Runs.RunAsync(host.CreateClient(TestTokens.Priya), Runs.Turn("thread-4", "run-4", Runs.User("user-4", "Hello")), Ct);
    }

    [Fact]
    public async Task A_run_that_outlives_its_budget_ends_with_RUN_ERROR()
    {
        await using var host = Hosts.Agent(new Dictionary<string, string?> { ["Beacon:Security:RunTimeoutSeconds"] = "1" }, new StallingChatClient());

        var events = await Runs.RunAsync(host.CreateClient(TestTokens.Dana),
            Runs.Turn("thread-1", "run-1", Runs.User("user-1", "Hello")), Ct);

        var error = Assert.IsType<RunErrorEvent>(events[^1]);
        Assert.Equal(SecureEventStream.RunTimeoutCode, error.Code);
        Assert.Contains(host.Logs, l => l.EventId == 2307);
    }

    [Fact]
    public async Task A_second_run_on_a_busy_thread_is_409()
    {
        await using var host = Hosts.Agent(new Dictionary<string, string?> { ["Beacon:Security:RunTimeoutSeconds"] = "5" }, new StallingChatClient());
        var dana = host.CreateClient(TestTokens.Dana);

        using var first = await Runs.PostAsync(dana, Runs.Turn("thread-1", "run-1", Runs.User("user-1", "Hello")), Ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await Runs.PostAsync(dana, Runs.Turn("thread-1", "run-2", Runs.User("user-2", "Hello again")), Ct);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }
}

using Ch02.HandBuilt.Console;

namespace Ch02.Tests;

/// <summary>Runs that don't end well: a failing model, and a client that stops listening.</summary>
public sealed class FailureAndCancellationTests
{
    [Fact]
    public async Task A_model_failure_mid_answer_ends_the_run_with_RUN_ERROR()
    {
        await using var agent = new TestAgent(new FailingChatClient("Short-cycling usually "));

        var events = await Wire.RunAsync(agent.CreateClient(), Wire.UserRun("Why is EQ-501 short-cycling?"), TestContext.Current.CancellationToken);

        Assert.Equal(["RUN_STARTED", "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "RUN_ERROR"], Wire.Types(events));

        var error = events[^1];
        Assert.Equal("MODEL_ERROR", error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        Assert.DoesNotContain(FailingChatClient.SecretDetail, error.GetRawText());
    }

    [Fact]
    public async Task A_model_failure_before_any_text_still_closes_the_run_with_RUN_ERROR()
    {
        await using var agent = new TestAgent(new FailingChatClient());

        var events = await Wire.RunAsync(agent.CreateClient(), Wire.UserRun("Hello"), TestContext.Current.CancellationToken);

        Assert.Equal(["RUN_STARTED", "RUN_ERROR"], Wire.Types(events));
    }

    [Fact]
    public async Task The_console_client_reports_RUN_ERROR_as_a_failed_run()
    {
        await using var agent = new TestAgent(new FailingChatClient("Short-cycling usually "));
        var http = agent.CreateClient();
        var conversation = new AgentConversation(http, new Uri(http.BaseAddress!, "/agent"));

        var result = await conversation.SendAsync("Why is EQ-501 short-cycling?", _ => { }, TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Stopping_the_client_cancels_the_model_call_on_the_server()
    {
        var model = new StallingChatClient();
        await using var agent = new TestAgent(model);
        var http = agent.CreateClient();
        var conversation = new AgentConversation(http, new Uri(http.BaseAddress!, "/agent"));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Stop as soon as the first delta arrives, the way Ctrl+C does in the console client.
        var result = await conversation.SendAsync("Why is EQ-501 short-cycling?", _ => stop.Cancel(), stop.Token);

        Assert.Equal(RunStatus.Stopped, result.Status);
        await model.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // What the user saw before stopping stays in the conversation.
        Assert.Equal(["user", "assistant"], conversation.History.Select(m => m.Role));
        Assert.Equal("Let me check ", conversation.History[^1].Content);
    }
}

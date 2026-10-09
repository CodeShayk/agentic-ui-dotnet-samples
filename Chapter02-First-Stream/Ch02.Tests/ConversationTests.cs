using System.Text.Json;
using Ch02.HandBuilt.Console;
using Microsoft.Extensions.AI;

namespace Ch02.Tests;

/// <summary>Threads and runs: the client keeps the conversation, the server keeps nothing.</summary>
public sealed class ConversationTests
{
    [Fact]
    public async Task Every_run_on_a_thread_resends_the_whole_conversation()
    {
        var model = new RecordingChatClient("A weak run capacitor ", "is the usual cause.");
        await using var agent = new TestAgent(model);
        var recorder = new RequestRecorder();
        var http = agent.CreateDefaultClient(recorder);
        var conversation = new AgentConversation(http, new Uri(http.BaseAddress!, "/agent"));
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = await conversation.SendAsync("Why would EQ-501 short-cycle?", _ => { }, cancellationToken);
        var second = await conversation.SendAsync("What should Priya check first?", _ => { }, cancellationToken);

        Assert.Equal(RunStatus.Finished, first.Status);
        Assert.Equal(RunStatus.Finished, second.Status);

        // On the wire: one thread, a new run each time, and the history growing by a full turn.
        var (run1, run2) = (recorder.Bodies[0], recorder.Bodies[1]);
        Assert.Equal(run1.GetProperty("threadId").GetString(), run2.GetProperty("threadId").GetString());
        Assert.NotEqual(run1.GetProperty("runId").GetString(), run2.GetProperty("runId").GetString());
        Assert.Equal(1, run1.GetProperty("messages").GetArrayLength());
        Assert.Equal(3, run2.GetProperty("messages").GetArrayLength());

        // In the model call: the server rebuilt the conversation from that request alone.
        var secondCall = model.Requests[1];
        Assert.Equal(["system", "user", "assistant", "user"], secondCall.Select(m => m.Role.Value));
        Assert.Equal("A weak run capacitor is the usual cause.", secondCall[2].Text);

        // The assistant message keeps the ID the server minted on TEXT_MESSAGE_START.
        var assistant = conversation.History[1];
        Assert.StartsWith("msg-", assistant.Id);
        Assert.Equal(assistant.Id, secondCall[2].MessageId);
    }

    /// <summary>Records the JSON body of every request the client sends.</summary>
    private sealed class RequestRecorder : DelegatingHandler
    {
        public List<JsonElement> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Bodies.Add(document.RootElement.Clone());
            return await base.SendAsync(request, cancellationToken);
        }
    }
}

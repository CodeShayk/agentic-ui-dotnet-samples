using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using AGUI.Client;
using Beacon.Client.Core;
using Ch14.Beacon.Desk.Components.Approvals;
using Ch14.Beacon.Desk.Components.Chat;
using Ch14.Tests.Infrastructure;
using Microsoft.Extensions.AI;

namespace Ch14.Tests;

/// <summary>
/// The Desk's client stack against the agent: the SDK's HTTP transport, a tap, AGUIChatClient and
/// AgentConversation, answering through <see cref="ApprovalAnswers"/> exactly as the dialog does.
/// </summary>
public sealed class OfficialClientTests
{
    [Fact]
    public async Task Approving_through_RespondAsync_resumes_the_run_and_the_restated_call_stays_one_call()
    {
        await using var host = new AgentHost();
        using var client = new DeskStack(host, "desk-1a2b3c4d");

        await client.Conversation.SendAsync(new ChatMessage(ChatRole.User,
            "Priya says the capacitor tests fine. Order a replacement compressor for WO-1001.") { MessageId = "user-1" }, cancellationToken: Runs.Ct);

        // The Interrupts slice holds the approval; the dialog's model is projected from it.
        var request = Assert.IsType<ApprovalRequest>(ApprovalRequest.Current(client.Conversation.View));
        Assert.Equal(ConversationStatus.AwaitingInput, client.Conversation.View.Status);
        Assert.Equal(["quantity"], request.EditableFields.Select(f => f.Name));
        Assert.IsType<ToolApprovalRequestContent>(request.Request);

        await ApprovalAnswers.SendAsync(client.Conversation, request, new ApprovalDecision(ApprovalChoice.Approve), Runs.Ct);

        // What AGUIChatClient sent: the answer as a resume entry, the approval echoing the call it saw.
        var resume = Assert.Single(client.Transport.Inputs[^1].Resume!);
        Assert.Equal((request.Id, ResumeStatus.Resolved), (resume.InterruptId, resume.Status));
        Assert.True(resume.Payload!.Value.GetProperty("approved").GetBoolean());
        Assert.Equal(request.ToolCallId, resume.Payload!.Value.GetProperty("toolCall").GetProperty("callId").GetString());

        // History comes from the view, so Beacon's sentence before the request survives. (AGUIChatClient on
        // its own drops every message that carries approval content, text included.)
        Assert.Contains(client.Transport.Inputs[^1].Messages, m => m is AGUIAssistantMessage { Content: { } text } && text.StartsWith("The capacitor checks out", StringComparison.Ordinal));

        var view = client.Conversation.View;
        Assert.Equal(ConversationStatus.Idle, view.Status);
        Assert.Single(host.Store.ListPartOrders());

        // The resumed run restated the call under a new parentMessageId. It is still one call, on the
        // message that first carried it, and its parsed arguments are the proposal...
        var restated = client.Transport.Events[^1].OfType<ToolCallStartEvent>().Single();
        var call = Assert.Single(view.ToolCalls);
        Assert.Equal(request.ToolCallId, restated.ToolCallId);
        Assert.NotEqual(call.ParentMessageId, restated.ParentMessageId);
        Assert.Null(view.FindMessage(restated.ParentMessageId!));
        Assert.True(JsonNode.DeepEquals(Runs.CompressorOrder, JsonNode.Parse(call.Arguments!.Value.GetRawText())));

        // ...while the argument text got the restatement appended, which is why nothing displays it.
        Assert.Equal(call.Arguments!.Value.GetRawText() + call.Arguments!.Value.GetRawText(), call.ArgumentsText);

        // The Desk shows one activity line for the call, with what actually ran.
        var transcript = DeskTranscript.From(view);
        var line = Assert.Single(transcript.ToolLines);
        Assert.Equal("Ordering 1 x CMP-CA36 for WO-1001: placed as PO-9001: 1 x CMP-CA36, $1,795.00.", ToolLine.Describe(Assert.Single(line.Value)));
    }

    [Fact]
    public async Task An_edit_goes_as_a_resume_entry_with_editedArgs_and_runs_as_edited()
    {
        await using var host = new AgentHost();
        using var client = new DeskStack(host, "desk-1a2b3c4d");

        await client.Conversation.SendAsync(new ChatMessage(ChatRole.User,
            "Charge Harbor View Dental $2,145.00 for the WO-1001 compressor replacement.") { MessageId = "user-1" }, cancellationToken: Runs.Ct);
        var request = ApprovalRequest.Current(client.Conversation.View)!;
        Assert.Equal(["amount", "description"], request.EditableFields.Select(f => f.Name));

        var edited = (JsonObject)JsonNode.Parse(request.Arguments.GetRawText())!;
        edited["amount"] = 1795.00m;
        await ApprovalAnswers.SendAsync(client.Conversation, request,
            new ApprovalDecision(ApprovalChoice.Edit, "Labor is covered by Harbor View's Priority plan.", edited), Runs.Ct);

        var sent = Assert.Single(client.Transport.Inputs[^1].Resume!).Payload!.Value;
        Assert.Equal(1795.00m, sent.GetProperty("editedArgs").GetProperty("amount").GetDecimal());
        Assert.False(sent.TryGetProperty("toolCall", out _));

        var charge = Assert.Single(host.Audit, e => e.Event == Ch14.Beacon.Agent.Approvals.ApprovalEvent.Executed);
        Assert.Equal(("CHG-7001", 1795.00m), (charge.Result, charge.Amount));
        Assert.Equal("Charging $2,145.00 for WO-1001: charged as CHG-7001: $1,795.00.",
            ToolLine.Describe(Assert.Single(client.Conversation.View.ToolCalls)));
    }

    [Fact]
    public async Task An_answer_the_agent_refuses_leaves_the_approval_open_and_dismissing_it_moves_the_thread_on()
    {
        await using var host = new AgentHost();

        // The Desk's clock is behind the agent's, so the Desk still thinks the approval is open when the agent
        // has let it expire: the agent refuses the answer with a 409 before any run starts.
        using var client = new DeskStack(host, "desk-1a2b3c4d", clientClock: new ManualClock());
        await client.Conversation.SendAsync(new ChatMessage(ChatRole.User,
            "Priya says the capacitor tests fine. Order a replacement compressor for WO-1001.") { MessageId = "user-1" }, cancellationToken: Runs.Ct);
        var request = ApprovalRequest.Current(client.Conversation.View)!;
        host.Clock.Advance(TimeSpan.FromMinutes(11));

        await ApprovalAnswers.SendAsync(client.Conversation, request, new ApprovalDecision(ApprovalChoice.Approve), Runs.Ct);

        // The turn failed as Rejected, and the approval is still open, saying why; the dialog explains it.
        var view = client.Conversation.View;
        Assert.Equal((ConversationStatus.Failed, FailureKind.Rejected), (view.Status, view.Failure!.Kind));
        var refused = Assert.IsType<ApprovalRequest>(ApprovalRequest.Current(view));
        Assert.Equal((FailureKind.Rejected, HttpStatusCode.Conflict), (refused.LastFailure!.Kind, refused.LastFailure.StatusCode));
        Assert.Null(DeskChat.ErrorOf(view));

        // Abandoning is always accepted: the run starts, the approval closes and nothing is ordered.
        await ApprovalAnswers.SendAsync(client.Conversation, refused, new ApprovalDecision(ApprovalChoice.Dismiss), Runs.Ct);

        Assert.Null(ApprovalRequest.Current(client.Conversation.View));
        Assert.Equal(ConversationStatus.Idle, client.Conversation.View.Status);
        Assert.Empty(host.Store.ListPartOrders());
    }

    /// <summary>Beacon Desk's client stack over the in-memory agent, recording what crossed the transport.</summary>
    private sealed class DeskStack : IDisposable
    {
        public DeskStack(AgentHost host, string clientId, TimeProvider? clientClock = null)
        {
            var http = host.Client(clientId);
            Transport = new RecordingTransport(new AGUIChatClientOptions(http, new Uri(http.BaseAddress!, "/agent").ToString()).Transport);
            var tap = new WireEventTap(Transport);
            Chat = new AGUIChatClient(new AGUIChatClientOptions { Transport = tap });
            Conversation = new AgentConversation(Chat, new ConversationOptions { Tap = tap, TimeProvider = clientClock ?? host.Clock });
        }

        public RecordingTransport Transport { get; }

        public AGUIChatClient Chat { get; }

        public AgentConversation Conversation { get; }

        public void Dispose()
        {
            Conversation.Dispose();
            Chat.Dispose();
        }
    }

    /// <summary>Passes runs through and keeps every input and the events each one produced.</summary>
    private sealed class RecordingTransport(IAGUITransport inner) : IAGUITransport
    {
        private readonly ConcurrentQueue<(RunAgentInput Input, List<BaseEvent> Events)> _runs = new();

        public IReadOnlyList<RunAgentInput> Inputs => [.. _runs.Select(r => r.Input)];

        public IReadOnlyList<IReadOnlyList<BaseEvent>> Events => [.. _runs.Select(r => (IReadOnlyList<BaseEvent>)r.Events)];

        public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            List<BaseEvent> events = [];
            _runs.Enqueue((input, events));
            await foreach (var evt in inner.SendAsync(input, cancellationToken))
            {
                events.Add(evt);
                yield return evt;
            }
        }
    }
}

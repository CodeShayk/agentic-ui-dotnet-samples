using Beacon.Client.Core;
using Bunit;
using Ch16.Beacon.Agent.Scripted;
using Ch16.Beacon.Desk.Components.Chat;
using Ch16.Tests.Infrastructure;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Ch16.Tests;

/// <summary>The Desk's chat rendered with bUnit over a real diagnosis from the in-memory agent.</summary>
public sealed class DeskChatTests : BunitContext
{
    [Fact]
    public async Task The_answer_renders_and_is_announced_alone_while_the_reasoning_waits_collapsed()
    {
        await using var host = new AgentHost();
        using var desk = new DeskStack(host);
        Services.AddSingleton(desk.Conversation);
        Services.AddSingleton(TimeProvider.System);
        JSInterop.Mode = JSRuntimeMode.Loose; // FocusAsync
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));

        var chat = Render<DeskChat>(p => p.Add(x => x.RenderInterval, TimeSpan.Zero));
        await chat.InvokeAsync(() => desk.Conversation.SendAsync(Runs.Diagnose, cancellationToken: Runs.Ct));

        chat.WaitForAssertion(() => Assert.Equal(ConversationStatus.Idle, desk.Conversation.View.Status));
        chat.WaitForAssertion(() => Assert.Contains("heat rejection, not the compressor", chat.Find("[data-bd-announcer]").TextContent, StringComparison.Ordinal));

        // The transcript's live region read the answer and nothing of the reasoning.
        var announced = chat.Find("[data-bd-announcer]").TextContent;
        Assert.DoesNotContain("points to the compressor tripping", announced, StringComparison.Ordinal);

        // One panel above the answer, collapsed, with no reasoning and no sealed value in the markup.
        var toggle = chat.Find("button.ch16-reasoning__toggle");
        Assert.Equal("false", toggle.GetAttribute("aria-expanded"));
        Assert.Equal("2 thoughts · 1 lookup", chat.Find(".ch16-reasoning__status").TextContent);
        Assert.DoesNotContain(DiagnosisText.Summary1, chat.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("bx1.", chat.Markup, StringComparison.Ordinal);

        // Opening it shows the steps in order, still without the sealed values.
        toggle.Click();
        var steps = chat.FindAll(".ch16-step");
        Assert.Equal(3, steps.Count);
        Assert.Contains("compressor tripping on overload", steps[0].TextContent, StringComparison.Ordinal);
        Assert.Equal("Read the fault history for EQ-501", steps[1].TextContent.Trim());
        Assert.Contains("[phone number removed]", steps[2].TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("bx1.", chat.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_request_is_reported_as_a_refusal_not_as_an_agent_that_cant_be_reached()
    {
        // A follow-up carrying a sealed value over the 64 KB cap: the agent answers 400 before any run starts.
        await using var host = new AgentHost();
        using var chat = new AGUI.Client.AGUIChatClient(new AGUI.Client.AGUIChatClientOptions(host.CreateClient(), "/agent"));
        using var conversation = new AgentConversation(chat, new ConversationOptions
        {
            ConfigureInput = (input, _) => input.Messages =
            [
                new AGUI.Abstractions.AGUIReasoningMessage { Id = "r-1", Content = string.Empty, EncryptedValue = new string('A', 70_000) },
                Runs.User("u-1", Runs.Diagnose),
            ],
        });

        await conversation.SendAsync(Runs.Diagnose, cancellationToken: Runs.Ct);

        Assert.Equal((FailureKind.Rejected, System.Net.HttpStatusCode.BadRequest), (conversation.View.Failure!.Kind, conversation.View.Failure.StatusCode));
        Assert.Equal("Beacon's agent refused the request (HTTP 400). Start a new chat if it happens again.", DeskChat.ErrorOf(conversation.View));
        Assert.Equal("Beacon's agent can't be reached. Check that it's running.",
            DeskChat.ErrorOf(ConversationView.Empty with { Status = ConversationStatus.Failed, Failure = new RunFailure(FailureKind.Unreachable, "down") }));
    }
}

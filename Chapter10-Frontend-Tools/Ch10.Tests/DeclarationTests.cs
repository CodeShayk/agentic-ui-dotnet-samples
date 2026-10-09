using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Client;
using Beacon.Client.Core;
using Beacon.Domain;
using Bunit;
using Ch10.Beacon.Desk.Components.Chat;
using Ch10.Beacon.Desk.Screen;
using Ch10.Beacon.Desk.Tools;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.AI;

namespace Ch10.Tests;

/// <summary>What the clients declare, what the client runs, and the Desk's confirmation card.</summary>
public sealed class DeclarationTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task AGUIChatClient_declares_each_AIFunction_with_its_JSON_Schema_on_every_run()
    {
        var transport = new ReplayTransport([new RunStartedEvent { ThreadId = "t-1", RunId = "r-1" }, new RunFinishedEvent { ThreadId = "t-1", RunId = "r-1" }]);
        var deskTools = new DeskTools(new DeskScreen(), new InMemoryFieldServiceStore()).Create();
        var fieldTools = FieldRig.Create().Tools.Create();
        using var client = new AGUIChatClient(new AGUIChatClientOptions { Transport = transport });
        using var conversation = new AgentConversation(client, new ConversationOptions { ThreadId = "t-1", Tools = [.. deskTools, .. fieldTools] });

        await conversation.SendAsync("Hello", cancellationToken: Ct);

        var tools = transport.Inputs[0].Tools!.ToDictionary(t => t.Name);
        var open = tools["open_work_order"].Parameters!.Value;
        Assert.Equal("object", open.GetProperty("type").GetString());
        Assert.Equal("string", open.GetProperty("properties").GetProperty("workOrderId").GetProperty("type").GetString());
        Assert.Equal("The work order ID, for example WO-1001.", open.GetProperty("properties").GetProperty("workOrderId").GetProperty("description").GetString());
        Assert.Equal(["workOrderId"], open.GetProperty("required").EnumerateArray().Select(e => e.GetString()));

        // The CancellationToken parameter is bound by the client, not declared; the enum is declared by its names.
        var photo = tools["take_photo"].Parameters!.Value.GetProperty("properties");
        Assert.False(photo.TryGetProperty("cancellationToken", out _));
        Assert.Equal(["nameplate", "part-label", "damage", "installation"], photo.GetProperty("subject").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(tools["get_location"].Parameters!.Value.GetProperty("properties").EnumerateObject());
    }

    [Fact]
    public async Task A_frontend_call_sent_in_the_chunked_form_is_never_run_by_AGUIChatClient()
    {
        BaseEvent[] chunked =
        [
            new RunStartedEvent { ThreadId = "t-1", RunId = "r-1" },
            new ToolCallChunkEvent { ToolCallId = "call-1", ToolCallName = "open_work_order", ParentMessageId = "msg-1", Delta = """{"workOrderId":"WO-1001"}""" },
            new RunFinishedEvent { ThreadId = "t-1", RunId = "r-1" },
        ];
        var screen = new DeskScreen();
        var transport = new ReplayTransport(chunked);
        using var client = new AGUIChatClient(new AGUIChatClientOptions { Transport = transport });
        using var conversation = new AgentConversation(client, new ConversationOptions
        {
            ThreadId = "t-1",
            Tools = new DeskTools(screen, new InMemoryFieldServiceStore()).Create(),
        });

        await conversation.SendAsync("Open WO-1001", cancellationToken: Ct);

        // The reducer sees the call; AGUIChatClient 1.0.0 never turns a chunk into FunctionCallContent.
        Assert.Null(screen.State.OpenWorkOrderId);
        Assert.Single(transport.Inputs);
        var call = Assert.Single(conversation.View.ToolCalls);
        Assert.Equal(("open_work_order", ToolCallStatus.AwaitingClient), (call.Name, call.Status));
    }

    [Fact]
    public void The_confirmation_card_takes_focus_and_only_its_buttons_or_Escape_answer()
    {
        using var bunit = new BunitContext();
        bunit.JSInterop.Mode = JSRuntimeMode.Loose;
        bool? answer = null;
        var request = new ConfirmationRequest("confirm_1", "Beacon wants to fill in the part order form",
            ["Work order: WO-1001", "Part: CAP-45-5, Dual run capacitor 45/5 uF"], "Fill in the form", "Not now");

        var card = bunit.Render<ConfirmationCard>(p => p
            .Add(c => c.Request, request)
            .Add(c => c.OnAnswer, (bool confirmed) => answer = confirmed));

        // Focus lands on the card itself, so a screen reader reads the question and its values first.
        var focused = (ElementReference)bunit.JSInterop.VerifyFocusAsyncInvoke().Arguments[0]!;
        var section = card.Find("section");
        Assert.Equal(focused.Id, section.GetAttribute("blazor:elementReference"));
        Assert.Equal(("alertdialog", "-1"), (section.GetAttribute("role"), section.GetAttribute("tabindex")));
        Assert.Equal(["Work order: WO-1001", "Part: CAP-45-5, Dual run capacitor 45/5 uF"], card.FindAll("li").Select(li => li.TextContent));

        // Enter or Space on the card answers nothing: only the button a keystroke lands on does.
        section.KeyDown(new KeyboardEventArgs { Key = "Enter" });
        section.KeyDown(new KeyboardEventArgs { Key = " " });
        Assert.Null(answer);

        // Tab reaches the two buttons, in this order, and both are real buttons that Enter and Space press.
        var buttons = card.FindAll("button");
        Assert.Equal([("Fill in the form", "button"), ("Not now", "button")], buttons.Select(b => (b.TextContent, b.GetAttribute("type"))));

        section.KeyDown(new KeyboardEventArgs { Key = "Escape" });
        Assert.False(answer);

        buttons[0].Click();
        Assert.True(answer);
    }
}

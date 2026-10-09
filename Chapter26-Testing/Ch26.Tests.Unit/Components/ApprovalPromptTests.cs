using Bunit;
using Ch26.Beacon.Desk.Workspace;

namespace Ch26.Tests.Unit.Components;

public sealed class ApprovalPromptTests : BunitContext
{
    private static readonly ApprovalRequest Compressor = new(
        "apr_1",
        "Approve this part order?",
        "Approval required for tool call: place_part_order",
        [new("Work order", "WO-1001"), new("Part", "CMP-CA36"), new("Quantity", "1")]);

    public ApprovalPromptTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    [Fact]
    public void The_prompt_is_a_labeled_dialog_that_takes_focus()
    {
        var prompt = Render<ApprovalPrompt>(p => p.Add(c => c.Request, Compressor));

        var dialog = prompt.Find("[role=dialog]");
        Assert.Equal(prompt.Find("h3").Id, dialog.GetAttribute("aria-labelledby"));
        Assert.Equal("Approve this part order?", prompt.Find("h3").TextContent);
        Assert.Equal(["WO-1001", "CMP-CA36", "1"], prompt.FindAll("dd").Select(dd => dd.TextContent));

        // A keyboard or screen-reader user lands on the request, not somewhere behind it.
        JSInterop.VerifyFocusAsyncInvoke().Arguments[0].ShouldBeElementReferenceTo(prompt.Find("h3"));
    }

    [Fact]
    public void Approve_answers_once_however_fast_the_clicks()
    {
        List<bool> answers = [];
        var prompt = Render<ApprovalPrompt>(p => p
            .Add(c => c.Request, Compressor)
            .Add(c => c.OnDecided, approved => answers.Add(approved)));

        prompt.Find("button.bd-button--primary").Click();

        Assert.Equal([true], answers);
        Assert.All(prompt.FindAll("button"), b => Assert.True(b.HasAttribute("disabled")));
    }

    [Fact]
    public void Deny_answers_false()
    {
        List<bool> answers = [];
        var prompt = Render<ApprovalPrompt>(p => p
            .Add(c => c.Request, Compressor)
            .Add(c => c.OnDecided, approved => answers.Add(approved)));

        prompt.Find("button.bd-button--stop").Click();

        Assert.Equal([false], answers);
    }
}

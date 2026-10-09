using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Ch26.Tests.EndToEnd;

/// <summary>
/// Dana's two most important journeys, through a real browser, a real Blazor circuit and a real stream.
/// Few on purpose: each test costs seconds, depends on ports and a browser, and fails for more reasons
/// than the code under test. The locators are roles and accessible names, so they double as a check that
/// the Desk is usable without a mouse.
/// </summary>
public sealed partial class DeskEndToEndTests : IClassFixture<BeaconApps>, IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage _page = default!;

    public async ValueTask InitializeAsync()
    {
        if (!BeaconApps.Enabled)
        {
            return;
        }

        SetDefaultExpectTimeout(15_000);
        _playwright = await Playwright.CreateAsync();

        // The installed Microsoft Edge, headless: no browser download, and nothing opens on screen.
        _browser = await _playwright.Chromium.LaunchAsync(new() { Channel = "msedge", Headless = true });
        _page = await _browser.NewPageAsync();
        await _page.GotoAsync(BeaconApps.DeskUrl);
    }

    [Fact]
    public async Task Dana_asks_about_WO_1001_and_the_work_order_opens()
    {
        Assert.SkipUnless(BeaconApps.Enabled, "Set RUN_E2E_TESTS=true to run the browser tests.");

        await AskAsync("What is the status of WO-1001?");

        await Expect(_page.GetByRole(AriaRole.Heading, new() { Name = "WO-1001", Level = 1 })).ToBeVisibleAsync();

        // The agent process is shared by every test in the class, and the approval test changes WO-1001's
        // status when it orders a part. Assert what no other test changes.
        await Expect(_page.GetByRole(AriaRole.Region, new() { Name = "Conversation" })).ToContainTextAsync("Customer: Harbor View Dental");
        await Expect(_page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Ready");
        await Expect(_page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Dana_approves_the_compressor_order_and_it_is_placed()
    {
        Assert.SkipUnless(BeaconApps.Enabled, "Set RUN_E2E_TESTS=true to run the browser tests.");

        await AskAsync("Order a replacement compressor CMP-CA36 for WO-1001.");

        var approval = _page.GetByRole(AriaRole.Dialog, new() { Name = "Approve this part order?" });
        await Expect(approval).ToBeVisibleAsync();
        await Expect(approval).ToContainTextAsync("CMP-CA36");
        await Expect(_page.GetByRole(AriaRole.Textbox, new() { Name = "Message Beacon" })).ToBeDisabledAsync();

        await approval.GetByRole(AriaRole.Button, new() { Name = "Approve" }).ClickAsync();

        await Expect(approval).ToHaveCountAsync(0);
        await Expect(_page.GetByRole(AriaRole.Region, new() { Name = "Conversation" })).ToContainTextAsync(OrderPlaced());
        await Expect(_page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Ready");
    }

    private async Task AskAsync(string question)
    {
        var composer = _page.GetByRole(AriaRole.Textbox, new() { Name = "Message Beacon" });
        await Expect(composer).ToBeEnabledAsync(); // enabled once the Blazor circuit is interactive
        await composer.FillAsync(question);
        await composer.PressAsync("Enter");
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
    }

    [GeneratedRegex(@"Order PO-\d+ is placed")]
    private static partial Regex OrderPlaced();
}

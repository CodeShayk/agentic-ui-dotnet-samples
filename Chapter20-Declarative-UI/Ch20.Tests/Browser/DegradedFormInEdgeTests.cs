using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Ch20.Tests.Browser;

/// <summary>
/// Chapter 20's browser proof, with Chapter 6's setup: the running agent and Desk, the installed Microsoft
/// Edge, headless, driven by Playwright for .NET. The RT-120 form uses a Slider, which this Desk can't draw:
/// the form arrives degraded, is announced, shows a labeled placeholder and still submits. Skipped unless
/// RUN_E2E_TESTS=true; build the agent and the Desk first, because the fixture runs them without building.
/// </summary>
public sealed class DegradedFormInEdgeTests(DeskApps apps) : IClassFixture<DeskApps>, IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage _page = default!;

    public async ValueTask InitializeAsync()
    {
        if (!DeskApps.Enabled)
        {
            return;
        }

        _ = apps;
        SetDefaultExpectTimeout(20_000);
        _playwright = await Playwright.CreateAsync();

        // The installed Edge, headless, with no permissions granted: nothing on screen, no device the page could reach.
        _browser = await _playwright.Chromium.LaunchAsync(new() { Channel = "msedge", Headless = true });
        _page = await _browser.NewPageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
    }

    [Fact]
    public async Task A_form_with_a_control_this_Desk_cannot_draw_arrives_degraded_and_still_submits()
    {
        Assert.SkipUnless(DeskApps.Enabled, "Set RUN_E2E_TESTS=true to run the browser tests.");
        await _page.GotoAsync(DeskApps.DeskUrl);

        // Keep every text the polite live region announces, however briefly it stays.
        await _page.EvaluateAsync("""
            () => {
                window.announced = [];
                new MutationObserver(() => {
                    const text = document.querySelector('[data-bd-announcer]')?.textContent?.trim();
                    if (text && window.announced[window.announced.length - 1] !== text) { window.announced.push(text); }
                }).observe(document.body, { subtree: true, childList: true, characterData: true });
            }
            """);

        var suggestion = _page.GetByRole(AriaRole.Button, new() { Name = "Start the inspection checklist for WO-1005." });
        await Expect(suggestion).ToBeEnabledAsync(); // enabled once the circuit is interactive
        await suggestion.ClickAsync();

        var form = _page.Locator("section.ch20-surface[data-outcome=degraded]");
        await Expect(form).ToBeVisibleAsync();
        await Expect(_page.Locator(".bd-status__text")).ToHaveTextAsync("Ready"); // the introduction has finished
        await Expect(form.Locator(".ch20-surface__notice")).ToHaveTextAsync("Part of this form needs a newer Beacon Desk. It's shown as a summary; everything else works.");
        var placeholder = form.Locator(".ch20-degraded");
        await Expect(placeholder).ToContainTextAsync("Compressor vibration (0 none to 5 severe)");
        await Expect(placeholder).ToContainTextAsync("Current value: 0");
        await Expect(placeholder).ToContainTextAsync("This Desk can't show this control (Slider) yet.");
        await Expect(_page.Locator(".ch20-inspector__item[data-surface='inspection-WO-1005'] .ch20-problem--warning"))
            .ToHaveTextAsync("unknown_component at /1/updateComponents/components/10/component: This client can't draw 'Slider' yet and shows a placeholder.");

        // Everything else works: four checks, two readings, the verdict, and the submit button.
        foreach (var box in await form.Locator("input[type=checkbox]").AllAsync())
        {
            await box.CheckAsync();
        }

        var readings = form.Locator("input[inputmode=decimal]");
        await readings.Nth(0).FillAsync("31.5");
        await readings.Nth(1).FillAsync("57");
        await form.GetByRole(AriaRole.Radio, new() { Name = "Pass" }).CheckAsync();
        await form.GetByRole(AriaRole.Button, new() { Name = "Submit inspection" }).ClickAsync();

        await Expect(form.Locator(".ch20-surface__status")).ToHaveTextAsync("Submitted to Beacon.");
        await Expect(_page.Locator(".bd-message").Last).ToContainTextAsync(
            "4 of 4 checks passed. Readings: compressor current, L1 31.5 A, discharge air temperature 57 F, compressor vibration 0 of 5. Overall: pass.");

        // The form's arrival was announced as a transcript entry of its own.
        var announced = await _page.EvaluateAsync<string[]>("() => window.announced");
        Assert.Contains(announced, a => a.Contains("Form: RT-120 rooftop unit inspection for WO-1005. Part of it needs a newer Desk.", StringComparison.Ordinal));
        TestContext.Current.TestOutputHelper?.WriteLine($"Announced: {string.Join(" | ", announced)}");
    }
}

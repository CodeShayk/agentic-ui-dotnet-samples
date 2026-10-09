using System.Diagnostics;
using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Ch06.Tests.Browser;

/// <summary>
/// The book's browser proof. The running Desk, in the installed Microsoft Edge, headless, driven by
/// Playwright for .NET through roles and accessible names, the way a keyboard or screen-reader user
/// finds things. It checks what bUnit can't: a real circuit, real focus, a real fetch from WebAssembly
/// and a real DOM that could run script. Measurements go to the test output. Skipped unless
/// RUN_E2E_TESTS=true; build the agent and the Desk first, because the fixture runs them without building.
/// The checks assume the agent's default pacing (60 ms between chunks). To see the render throttle at a
/// fast model's pace, set AI__ScriptedChunkDelayMilliseconds=5 and run only
/// The_handover_note_renders_at_a_throttled_pace: at that speed the typing indicator is gone too quickly to check.
/// </summary>
public sealed class DeskInEdgeTests : IClassFixture<DeskApps>, IAsyncLifetime
{
    private static readonly byte[] RenderBatch = Encoding.UTF8.GetBytes("JS.RenderBatch");

    private readonly List<(long Timestamp, bool IsRender)> _frames = [];
    private readonly List<string> _dialogs = [];
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage _page = default!;

    public async ValueTask InitializeAsync()
    {
        if (!DeskApps.Enabled)
        {
            return;
        }

        SetDefaultExpectTimeout(15_000);
        _playwright = await Playwright.CreateAsync();

        // The installed Edge, headless: nothing to download and nothing on screen. The context grants no
        // permissions, so the page couldn't reach a camera, a microphone or the location even if it asked.
        _browser = await _playwright.Chromium.LaunchAsync(new() { Channel = "msedge", Headless = true });
        _page = await _browser.NewPageAsync();

        // Any alert(), confirm() or prompt() would mean model output ran as script.
        _page.Dialog += (_, dialog) =>
        {
            lock (_dialogs) { _dialogs.Add(dialog.Message); }
            _ = dialog.DismissAsync();
        };

        // Under Interactive Server, every render reaches the browser as a JS.RenderBatch message on the circuit.
        _page.WebSocket += (_, socket) => socket.FrameReceived += (_, frame) =>
        {
            var isRender = frame.Binary is { } bytes
                ? bytes.AsSpan().IndexOf(RenderBatch) >= 0
                : frame.Text?.Contains("JS.RenderBatch", StringComparison.Ordinal) == true;
            lock (_frames) { _frames.Add((Stopwatch.GetTimestamp(), isRender)); }
        };
    }

    [Fact]
    public async Task On_the_server_page_an_answer_streams_behind_a_typing_indicator()
    {
        Assert.SkipUnless(DeskApps.Enabled, "Set RUN_E2E_TESTS=true to run the browser tests.");
        await OpenAsync(DeskApps.DeskUrl, "Running on the server");

        // Type and press Enter at once: under Interactive Server a quick Enter must not be lost.
        var sent = await AskAsync("What's the status of WO-1001?");
        await Expect(Transcript.Locator(".bd-message--user")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("WO-1001"));

        // The typing indicator covers the tool call; then the first words appear.
        await _page.WaitForSelectorAsync(".bd-message--pending", new() { State = WaitForSelectorState.Attached });
        await _page.WaitForSelectorAsync(".bd-message--streaming .bd-markdown p", new() { State = WaitForSelectorState.Attached });
        var firstWords = Stopwatch.GetElapsedTime(sent);

        await WaitForAnswerAsync();
        var done = Stopwatch.GetElapsedTime(sent);
        await Expect(_page.Locator("[data-bd-announcer]")).ToContainTextAsync("Beacon: WO-1001 is Scheduled");

        Report($"First words {firstWords.TotalMilliseconds:0} ms after Enter; Ready after {done.TotalMilliseconds:0} ms; {RendersSince(sent)}.");
    }

    [Fact]
    public async Task The_handover_note_renders_at_a_throttled_pace()
    {
        Assert.SkipUnless(DeskApps.Enabled, "Set RUN_E2E_TESTS=true to run the browser tests.");
        await OpenAsync(DeskApps.DeskUrl, "Running on the server");

        var sent = await AskAsync("Write a handover note for WO-1001");
        await WaitForAnswerAsync();

        // The recording of this run has 61 TEXT_MESSAGE_CONTENT events. Renders are at least 50 ms apart,
        // apart from the flushes when the question is sent and when the run ends.
        Report($"Handover note: Ready after {Stopwatch.GetElapsedTime(sent).TotalMilliseconds:0} ms; {RendersSince(sent)}.");
    }

    [Fact]
    public async Task Stop_keeps_the_partial_note_and_returns_focus_to_the_message_box()
    {
        Assert.SkipUnless(DeskApps.Enabled, "Set RUN_E2E_TESTS=true to run the browser tests.");
        await OpenAsync(DeskApps.DeskUrl, "Running on the server");

        await AskAsync("Write a handover note for WO-1001");
        await _page.WaitForSelectorAsync(".bd-message--streaming .bd-markdown p", new() { State = WaitForSelectorState.Attached });
        await _page.GetByRole(AriaRole.Button, new() { Name = "Stop" }).ClickAsync();

        await Expect(Status).ToHaveTextAsync("Stopped. The partial answer is kept.");
        await Expect(Transcript.Locator(".bd-message--partial")).ToContainTextAsync("Stopped before Beacon finished.");
        await Expect(Composer).ToBeFocusedAsync();
    }

    [Fact]
    public async Task The_hostile_handover_note_runs_no_script_and_loads_no_image()
    {
        Assert.SkipUnless(DeskApps.Enabled, "Set RUN_E2E_TESTS=true to run the browser tests.");
        await OpenAsync(DeskApps.DeskUrl, "Running on the server");

        await AskAsync("Write a handover note for WO-1001");
        await WaitForAnswerAsync();

        await Expect(Transcript.Locator("img")).ToHaveCountAsync(0);
        await Expect(Transcript.Locator("blockquote")).ToContainTextAsync("<img src=\"x\" onerror=\"alert('portal')\">");
        await Expect(Transcript.Locator("blockquote")).ToContainTextAsync("confirm the visit here");
        await Expect(Transcript.Locator("a")).ToHaveCountAsync(1);
        await Expect(Transcript.Locator("a")).ToHaveAttributeAsync("href", "https://contoso.example/manuals/ca-36x");
        lock (_dialogs) { Assert.Empty(_dialogs); }
    }

    [Fact]
    public async Task On_the_browser_page_the_answer_streams_through_fetch()
    {
        Assert.SkipUnless(DeskApps.Enabled, "Set RUN_E2E_TESTS=true to run the browser tests.");
        var opened = Stopwatch.GetTimestamp();
        await OpenAsync(DeskApps.DeskUrl + "browser", "Running in the browser");
        var interactive = Stopwatch.GetElapsedTime(opened);

        var sent = await AskAsync("Write a handover note for WO-1001");

        // Sample the streaming message until the run ends. With streamed responses it grows in many steps.
        List<(TimeSpan At, int Length)> growth = [];
        while (await Status.TextContentAsync() != "Ready" && Stopwatch.GetElapsedTime(sent) < TimeSpan.FromSeconds(30))
        {
            var length = (await _page.Locator(".bd-message--streaming .bd-markdown").AllTextContentsAsync()).Sum(t => t.Length);
            if (length > 0 && (growth.Count == 0 || growth[^1].Length != length))
            {
                growth.Add((Stopwatch.GetElapsedTime(sent), length));
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await Expect(Status).ToHaveTextAsync("Ready");
        Assert.True(growth.Count >= 10, $"Expected the note to grow in many steps while the run went on; saw {growth.Count}.");

        var gaps = growth.Zip(growth.Skip(1), (a, b) => (b.At - a.At).TotalMilliseconds).Order().ToList();
        Report($"/browser interactive {interactive.TotalMilliseconds:0} ms after navigation; first text at {growth[0].At.TotalMilliseconds:0} ms; " +
               $"{growth.Count} growth steps, median {gaps[gaps.Count / 2]:0} ms apart.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
    }

    private ILocator Composer => _page.GetByRole(AriaRole.Textbox, new() { Name = "Message Beacon" });

    private ILocator Transcript => _page.GetByRole(AriaRole.Region, new() { Name = "Conversation" });

    private ILocator Status => _page.GetByRole(AriaRole.Status);

    private async Task OpenAsync(string url, string badge)
    {
        await _page.GotoAsync(url);
        await Expect(_page.GetByText(badge)).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(Composer).ToBeEnabledAsync(); // enabled once the chat is interactive
    }

    // Returns the moment Enter was pressed.
    private async Task<long> AskAsync(string question)
    {
        await Composer.FillAsync(question);
        var sent = Stopwatch.GetTimestamp();
        await Composer.PressAsync("Enter");
        return sent;
    }

    // The status reads "Ready" before Enter's round trip lands, so wait for the finished answer itself.
    private async Task WaitForAnswerAsync()
    {
        await Expect(Transcript.Locator(".bd-message--assistant:not(.bd-message--streaming):not(.bd-message--pending)"))
            .ToHaveCountAsync(1, new() { Timeout = 30_000 });
        await Expect(Status).ToHaveTextAsync("Ready");
    }

    private string RendersSince(long sent)
    {
        List<long> renders;
        int frames;
        lock (_frames)
        {
            renders = [.. _frames.Where(f => f.Timestamp >= sent && f.IsRender).Select(f => f.Timestamp)];
            frames = _frames.Count(f => f.Timestamp >= sent);
        }

        var gaps = renders.Zip(renders.Skip(1), (a, b) => Stopwatch.GetElapsedTime(a, b).TotalMilliseconds).Order().ToList();
        var median = gaps.Count > 0 ? gaps[gaps.Count / 2] : 0;
        return $"{renders.Count} render batches (median gap {median:0} ms) in {frames} WebSocket frames";
    }

    private static void Report(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);
}

using AGUI.Client;
using Ch25.Beacon.Console;
using Microsoft.Extensions.AI;

// Ch25.Beacon.Console [endpoint] [--ask "question"] [--capture <folder>] [--replay <file.sse>] [--otlp <endpoint>] [--show-spans]
var arguments = ConsoleArguments.Parse(args);
var endpoint = arguments.Endpoint;
var question = arguments.Ask;
var captureFolder = arguments.Capture;
var replayPath = arguments.Replay;

// Spans go to the OTLP endpoint from --otlp (or OTEL_EXPORTER_OTLP_ENDPOINT); --show-spans also prints them.
using var tracing = ClientTelemetry.Start(arguments.Otlp, arguments.ShowSpans);

HttpMessageHandler handler = new HttpClientHandler();
if (captureFolder is not null)
{
    handler = new WireCaptureHandler(captureFolder) { InnerHandler = handler };
}

// A run streams for as long as the agent works, so the HTTP timeout is off and cancellation is ours.
using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

// Replaying swaps the HTTP transport for the capture file; everything above the transport is unchanged.
List<string> replayed = [];
var transportOptions = replayPath is null
    ? new AGUIChatClientOptions(http, endpoint)
    : new AGUIChatClientOptions { Transport = new ReplayTransport(replayPath, evt => replayed.Add(evt.Type)) };

// A client-side chat span around each run, without content; AGUIChatClient's own source covers
// its tool loop and the frontend tools it runs.
using var beacon = ClientTelemetry.Instrument(new AGUIChatClient(transportOptions));
var session = new BeaconChatSession(beacon);

if (replayPath is not null)
{
    Console.WriteLine($"Replaying {Path.GetFileName(replayPath)} through AGUIChatClient");
    await AskAsync(question ?? "(replayed run)");
    Console.WriteLine($"  [{replayed.Count} events: {Shape(replayed)}]");
    return;
}

if (question is not null)
{
    await AskAsync(question);
    return;
}

Console.WriteLine($"Beacon console on AG-UI ({endpoint}). An empty line quits.");
while (true)
{
    Console.Write("\nyou> ");
    var line = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(line))
    {
        break;
    }

    await AskAsync(line);
}

async Task AskAsync(string text)
{
    Console.Write("beacon> ");
    try
    {
        var turn = await session.SendAsync(text, Console.Write);
        Console.WriteLine();
        if (turn.Error is not null)
        {
            Console.WriteLine($"  [run failed: {turn.Error}]");
        }

        var usage = string.Join(", ", turn.Usage.Select(u =>
            $"{u.Model}: {u.Usage.InputTokenCount} in / {u.Usage.OutputTokenCount} out"));
        Console.WriteLine($"  [run {turn.RunId} | {(usage.Length > 0 ? usage : "no usage reported")} | trace {turn.TraceId}]");
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"\n  [cannot reach the agent: {ex.Message}]");
    }
}

// "TEXT_MESSAGE_CONTENT x6" instead of six lines.
static string Shape(List<string> types)
{
    List<string> parts = [];
    for (var i = 0; i < types.Count;)
    {
        var run = 1;
        while (i + run < types.Count && types[i + run] == types[i])
        {
            run++;
        }

        parts.Add(run > 1 ? $"{types[i]} x{run}" : types[i]);
        i += run;
    }

    return string.Join(" ", parts);
}

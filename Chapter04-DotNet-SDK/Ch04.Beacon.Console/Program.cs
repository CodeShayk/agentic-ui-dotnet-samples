using AGUI.Client;
using Ch04.Beacon.Console;
using Microsoft.Extensions.AI;

var endpoint = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "http://localhost:5044/agent";
var showRequests = args.Contains("--show-requests");

using var http = showRequests
    ? new HttpClient(new RunInputLogger(Console.Error) { InnerHandler = new HttpClientHandler() })
    : new HttpClient();

// HttpClient.Timeout bounds the wait for response headers, which AGUI.Server sends only with the
// model's first update, so a slow model would trip it. Ctrl+C stops a run instead.
http.Timeout = Timeout.InfiniteTimeSpan;

using IChatClient beacon = new AGUIChatClient(new AGUIChatClientOptions(http, endpoint));

var session = new BeaconChatSession(beacon);

// Ctrl+C while Beacon is answering stops that run; at the prompt it exits as usual.
CancellationTokenSource? answering = null;
Console.CancelKeyPress += (_, e) =>
{
    if (Interlocked.Exchange(ref answering, null) is { } run)
    {
        e.Cancel = true;
        run.Cancel();
    }
};

Console.WriteLine($"Beacon console on AG-UI ({endpoint})");
Console.WriteLine("Type a question. An empty line quits; Ctrl+C stops an answer.");

while (true)
{
    Console.Write("\nyou> ");
    var line = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(line))
    {
        break;
    }

    var run = new CancellationTokenSource();
    Volatile.Write(ref answering, run);

    Console.Write("beacon> ");
    try
    {
        var turn = await session.SendAsync(line, Console.Write, run.Token);
        Console.WriteLine();

        if (turn.Error is not null)
        {
            Console.WriteLine($"  [run failed: {turn.Error}]");
        }

        Console.WriteLine(
            $"  [thread {turn.ThreadId} | run {turn.RunId} | sent {turn.MessagesSent} message(s) | " +
            $"ConversationId: {turn.ConversationId ?? "null"}]");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n  [stopped]");
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"\n  [cannot reach the agent: {ex.Message}]");
    }
    finally
    {
        Interlocked.Exchange(ref answering, null);
    }
}

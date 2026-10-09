using Ch02.HandBuilt.Console;

var endpoint = new Uri(args.Length > 0 ? args[0] : "http://localhost:5020/agent");
using var http = new HttpClient();
var conversation = new AgentConversation(http, endpoint);

// Ctrl+C while an answer streams stops that answer; at the prompt it ends the program as usual.
CancellationTokenSource? activeRun = null;
Console.CancelKeyPress += (_, e) =>
{
    if (Volatile.Read(ref activeRun) is { } run)
    {
        e.Cancel = true;
        try
        {
            run.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run ended at the same moment; nothing left to stop.
        }
    }
};

Console.WriteLine($"Beacon console on thread {conversation.ThreadId} ({endpoint})");
Console.WriteLine("Ask a question and press Enter. Ctrl+C stops an answer; an empty line quits.");

while (true)
{
    Console.Write("\n> ");
    var question = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(question))
    {
        break;
    }

    if (Console.IsInputRedirected)
    {
        Console.WriteLine(question); // show piped-in questions, as if typed
    }

    using var run = new CancellationTokenSource();
    Volatile.Write(ref activeRun, run);
    try
    {
        var result = await conversation.SendAsync(question, Console.Write, run.Token);
        Console.WriteLine(result.Status switch
        {
            RunStatus.Stopped => " [stopped]",
            RunStatus.Failed => $"\n[the run failed: {result.Detail}]",
            RunStatus.Truncated => "\n[the connection closed before the run finished]",
            RunStatus.Rejected => $"[the agent rejected the request: {result.Detail}]",
            _ => string.Empty,
        });
    }
    catch (HttpRequestException exception)
    {
        Console.WriteLine($"[could not reach the agent: {exception.Message}]");
    }
    finally
    {
        Volatile.Write(ref activeRun, null);
    }
}

using AGUI.Client;
using Beacon.Client.Core;
using Ch08.Beacon.Cli;
using Ch08.Field.Core;
using Ch08.Field.Core.Connection;
using Ch08.Field.Core.Recovery;

// beacon ask "<question>" [--endpoint <url>] [--events]
// beacon chat [--endpoint <url>]
//
// Exit codes: see ExitCodes, or beacon --help.

if (args.Length == 0 || args[0] is "-h" or "--help" or "help" || args[0] is not ("ask" or "chat"))
{
    Console.Error.WriteLine("""
        Usage:
          beacon ask "<question>" [--endpoint <url>] [--events]
          beacon chat [--endpoint <url>]

        --endpoint  The agent's AG-UI endpoint (default: BEACON_AGENT_ENDPOINT or http://localhost:5080/agent).
        --events    Print every AG-UI event as a line of JSON on standard output instead of the answer.

        Exit codes:
          0    answered                       4    the agent is waiting for input
          1    usage                          5    timed out
          2    the agent reported an error    6    the agent refused the request (HTTP 4xx)
          3    the connection failed          130  stopped with Ctrl+C
        """);
    return ExitCodes.Usage;
}

var command = args[0];
var events = args.Contains("--events");
var endpointArgument = Option(args, "--endpoint") ?? Environment.GetEnvironmentVariable("BEACON_AGENT_ENDPOINT");
var question = command == "ask" ? args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && a != endpointArgument) : null;
if (command == "ask" && string.IsNullOrWhiteSpace(question))
{
    Console.Error.WriteLine("beacon: ask needs a question.");
    return ExitCodes.Usage;
}

Uri endpoint;
try
{
    // An operator's tool may use plain HTTP to an agent on this machine, and only there.
    endpoint = AgentEndpoint.Resolve(endpointArgument, FieldPlatform.Windows, isEmulator: false, allowCleartext: true);
}
catch (Exception ex) when (ex is InvalidOperationException or UriFormatException)
{
    Console.Error.WriteLine($"beacon: {ex.Message}");
    return ExitCodes.Usage;
}

// The same stack as Beacon Field: retry before the first event, Chapter 7's tap and conversation.
using var http = FieldHttp.CreateClient(connectTimeout: TimeSpan.FromSeconds(10));
IAGUITransport transport = new AGUIChatClientOptions(http, endpoint.ToString()).Transport;
if (events)
{
    transport = new JsonLinesTap(transport, Console.Out);
}

using var field = FieldClient.Create(transport);
var renderer = new ConsoleRenderer(Console.Out, Console.Error, showText: !events);
field.Transport.ConnectionChanged += (_, e) => renderer.Report(e);
field.Conversation.Changed += (_, e) => renderer.Render(field.Conversation.View);

// Ctrl+C stops the turn (the agent sees the request end); a second Ctrl+C ends the process.
var stopping = false;
Console.CancelKeyPress += (_, e) =>
{
    if (!stopping && field.Conversation.View.IsBusy)
    {
        stopping = e.Cancel = true;
        field.Conversation.Stop();
    }
};

if (command == "ask")
{
    return await AskAsync(question!);
}

Console.Error.WriteLine($"Beacon at {endpoint}. Type a question, /new for a new conversation, /quit to leave.");
while (Console.ReadLine() is { } line && line != "/quit")
{
    if (line == "/new")
    {
        field.Conversation.Reset();
        Console.Error.WriteLine("beacon: new conversation");
    }
    else if (!string.IsNullOrWhiteSpace(line))
    {
        await AskAsync(line);
    }
}

return ExitCodes.Answered;

async Task<int> AskAsync(string text)
{
    stopping = false;
    var runsBefore = field.Conversation.View.Runs.Count;
    await field.Conversation.SendAsync(text);

    var view = field.Conversation.View;
    renderer.Finish(view, TurnRecovery.Assess(view, runsBefore, ToolSafety.Beacon));
    return ExitCodes.For(view);
}

static string? Option(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

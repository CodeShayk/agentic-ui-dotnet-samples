using AGUI.Client;
using Ch24.Beacon.Console.Measurement;
using Ch24.Beacon.Console.Transport;
using Microsoft.Extensions.AI;

// Ch24.Beacon.Console <command> [options]
//   ask "question" [--sse]                          one question, the way Beacon Field asks it
//   compare [--runs 10]                             Chapter 4's glue against this chapter's endpoint, SSE and protobuf
//   negotiate                                       what SSE-only, protobuf-first and protobuf-only clients get
//   load [--users 200] [--runs 5] [--protobuf] [--http2] [--multiple-connections] [--retry] [--budget-ms 250]
// Common: [--endpoint http://localhost:5240/agent] [--question "What is the status of WO-1001?"]
var command = args.FirstOrDefault() ?? "ask";
var endpoint = Option("--endpoint") ?? "http://localhost:5240/agent";
var question = Option("--question") ?? (command == "ask" && args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
    ? args[1]
    : "What is the status of WO-1001?");

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

switch (command)
{
    case "ask":
        await AskAsync(args.Contains("--sse") ? WireFormat.Sse : WireFormat.Protobuf);
        break;
    case "compare":
        await CompareAsync(int.Parse(Option("--runs") ?? "10", System.Globalization.CultureInfo.InvariantCulture));
        break;
    case "negotiate":
        await NegotiateAsync();
        break;
    case "load":
        await LoadAsync();
        break;
    default:
        Console.WriteLine($"Unknown command '{command}'. Use ask, compare, negotiate or load.");
        break;
}

// Beacon Field's client: protobuf first, SSE as the fallback, retries only before the first event.
async Task AskAsync(WireFormat wire)
{
    using var http = BeaconHttp.Create(wire);
    var sdkTransport = new AGUIChatClientOptions(http, endpoint).Transport;
    var transport = new RetryBeforeFirstEventTransport(sdkTransport, maxAttempts: 3,
        onRetry: (attempt, ex) => Console.WriteLine($"  [attempt {attempt} failed before the first event ({ex.Message}); retrying]"));

    using IChatClient beacon = new AGUIChatClient(new AGUIChatClientOptions { Transport = transport });

    // The run's own bound: HttpClient.Timeout is off (BeaconHttp), so this ends a run that never finishes.
    using var run = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
    run.CancelAfter(RunProbe.RunTimeout);

    Console.Write("beacon> ");
    List<ChatMessage> history = [new(ChatRole.User, question) { MessageId = $"user_{Guid.NewGuid():N}" }];
    try
    {
        await foreach (var update in beacon.GetStreamingResponseAsync(history, new ChatOptions(), run.Token))
        {
            Console.Write(update.Text);
        }

        Console.WriteLine();
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"\n  [run refused or unreachable: {ex.Message}]");
    }
    catch (OperationCanceledException) when (run.IsCancellationRequested && !cancel.IsCancellationRequested)
    {
        Console.WriteLine($"\n  [no answer within {RunProbe.RunTimeout.TotalSeconds:0} seconds]");
    }
}

async Task CompareAsync(int runs)
{
    (string Name, string Url, WireFormat Wire)[] setups =
    [
        ("Chapter 4 glue, SSE", $"{endpoint}/baseline", WireFormat.Sse),
        ("Chapter 4 glue, protobuf", $"{endpoint}/baseline", WireFormat.Protobuf),
        ("Chapter 24 endpoint, SSE", endpoint, WireFormat.Sse),
        ("Chapter 24 endpoint, protobuf", endpoint, WireFormat.Protobuf),
    ];

    Console.WriteLine($"\"{question}\", median of {runs} runs after warm-up, times from the moment the question was asked");
    Console.WriteLine($"{"Setup",-30} {"Events",6} {"Bytes",7} {"Headers",9} {"RUN_STARTED",12} {"First text",11} {"Finished",9}");
    foreach (var (name, url, wire) in setups)
    {
        using var http = BeaconHttp.Create(wire);
        for (var i = 0; i < 3; i++)
        {
            await RunProbe.RunAsync(http, url, question, cancellationToken: cancel.Token);
        }

        List<RunMeter> meters = [];
        for (var i = 0; i < runs; i++)
        {
            meters.Add(await RunProbe.RunAsync(http, url, question, cancellationToken: cancel.Token));
        }

        Console.WriteLine(
            $"{name,-30} {meters[0].Events,6} {Median(meters.Select(m => (double)m.Bytes)),7:0} " +
            $"{Ms(meters.Select(m => m.Headers)),9} {Ms(meters.Select(m => m.RunStarted)),12} " +
            $"{Ms(meters.Select(m => m.FirstText)),11} {Ms(meters.Select(m => m.Finished)),9}   [{meters[0].ContentType}; {meters[0].Outcome}]");
    }
}

async Task NegotiateAsync()
{
    foreach (var wire in new[] { WireFormat.Sse, WireFormat.Protobuf, WireFormat.ProtobufOnly })
    {
        using var http = BeaconHttp.Create(wire);
        var meter = await RunProbe.RunAsync(http, endpoint, question, cancellationToken: cancel.Token);
        Console.WriteLine($"{wire,-13} -> {(int?)meter.Status} {meter.ContentType ?? "-"}, {meter.Events} events, {meter.Bytes} bytes, {meter.Outcome}");
        if (meter.ErrorBody is { Length: > 0 } body)
        {
            Console.WriteLine($"{"",17}{body}");
        }
    }
}

async Task LoadAsync()
{
    var options = new LoadTestOptions(
        endpoint,
        Users: int.Parse(Option("--users") ?? "200", System.Globalization.CultureInfo.InvariantCulture),
        RunsPerUser: int.Parse(Option("--runs") ?? "5", System.Globalization.CultureInfo.InvariantCulture),
        Wire: args.Contains("--protobuf") ? WireFormat.Protobuf : WireFormat.Sse,
        Http2: args.Contains("--http2"),
        MultipleHttp2Connections: args.Contains("--multiple-connections"),
        Retry: args.Contains("--retry"),
        FirstTextBudget: TimeSpan.FromMilliseconds(double.Parse(Option("--budget-ms") ?? "250", System.Globalization.CultureInfo.InvariantCulture)),
        Question: question);

    var report = await LoadTest.RunAsync(options, cancel.Token);

    Console.WriteLine($"{options.Users} users x {options.RunsPerUser} runs, {options.Wire} over HTTP/{report.HttpVersions}" +
        $"{(options.Http2 ? (options.MultipleHttp2Connections ? ", multiple HTTP/2 connections" : ", one HTTP/2 connection") : string.Empty)}" +
        $"{(options.Retry ? ", retry before first event" : string.Empty)}");
    Console.WriteLine($"  runs        {report.Runs} in {report.Elapsed.TotalSeconds:0.00} s ({report.Runs / report.Elapsed.TotalSeconds:0} runs/s); " +
        $"outcomes: {string.Join(", ", report.Outcomes.Select(o => $"{o.Key} {o.Value}"))}; retried {report.Retried}");
    Console.WriteLine($"  headers     {report.Headers}");
    Console.WriteLine($"  RUN_STARTED {report.RunStarted}");
    Console.WriteLine($"  first text  {report.FirstText}");
    Console.WriteLine($"  finished    {report.Finished}");
    Console.WriteLine($"  bytes/run   {report.BytesPerRun:0}");
    Console.WriteLine($"  budget      p95 first text <= {options.FirstTextBudget.TotalMilliseconds:0} ms and no failed runs: {(report.WithinBudget ? "PASS" : "FAIL")}");
}

static string Ms(IEnumerable<TimeSpan?> values)
{
    var present = values.Where(v => v.HasValue).Select(v => v!.Value.TotalMilliseconds).ToList();
    return present.Count == 0 ? "-" : $"{Median(present):0.0} ms";
}

static double Median(IEnumerable<double> values)
{
    var sorted = values.Order().ToList();
    return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
}

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

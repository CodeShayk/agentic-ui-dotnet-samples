using System.Net.ServerSentEvents;
using System.Text.Json;
using Ch03.StreamValidator;

// Usage: Ch03.ValidateStream [stream-file] [--input run-agent-input.json] [--quiet] [--strict]
// Reads Server-Sent Events (what curl -N prints), a JSON array of events, or a conformance fixture
// ({ "stream": [...] }) from the file or from standard input. Exit code: 0 valid, 1 invalid, 2 usage.
string? streamPath = null, inputPath = null;
bool quiet = false, strict = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--input" when i + 1 < args.Length: inputPath = args[++i]; break;
        case "--quiet": quiet = true; break;
        case "--strict": strict = true; break;
        case var path when !path.StartsWith("--", StringComparison.Ordinal): streamPath = path; break;
        default:
            Console.Error.WriteLine("Usage: Ch03.ValidateStream [stream-file] [--input run-agent-input.json] [--quiet] [--strict]");
            return 2;
    }
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

// Knowing the request lets the validator check the thread, the requested run and frontend tools.
var options = inputPath is null
    ? null
    : StreamValidatorOptions.FromRunAgentInput(JsonDocument.Parse(await File.ReadAllTextAsync(inputPath)).RootElement);

var validator = new AGUIStreamValidator(options);
var undecodable = 0;

await using var source = streamPath is null ? Console.OpenStandardInput() : File.OpenRead(streamPath);
await foreach (var frame in StreamReader.ReadFramesAsync(source, cancellation.Token))
{
    JsonElement evt;
    try
    {
        evt = JsonDocument.Parse(frame).RootElement.Clone();
    }
    catch (JsonException ex)
    {
        // Turning bytes into events is the reader's job; a frame that is not JSON is no event at all.
        undecodable++;
        Console.WriteLine($"#{validator.EventCount,-3} (not JSON) {ex.Message}");
        continue;
    }

    var index = validator.EventCount;
    var found = validator.Observe(evt);
    if (!quiet)
    {
        Console.WriteLine($"#{index,-3} {Describe(evt)}");
    }

    foreach (var diagnostic in found)
    {
        Console.WriteLine($"     {diagnostic}");
    }
}

var report = validator.Complete();
foreach (var diagnostic in report.Diagnostics.Where(d => d.EventIndex == report.EventCount))
{
    Console.WriteLine($"     {diagnostic}");
}

Console.WriteLine();
Console.WriteLine(undecodable == 0 ? report.ToString() : $"{report}, {undecodable} undecodable frame(s)");

var failed = !report.IsValid || undecodable > 0 || (strict && report.Warnings.Any());
return failed ? 1 : 0;

// One line per event: its type and the identifiers that tie it to other events.
static string Describe(JsonElement evt)
{
    if (evt.ValueKind != JsonValueKind.Object || !evt.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
    {
        return "(no type)";
    }

    string? Read(string name) =>
        evt.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    var detail = string.Join("  ", new[]
    {
        Read("runId") is { } runId ? $"run {runId}" : null,
        Read("subagentRunId") is { } subagent ? $"[{subagent}]" : null,
        Read("messageId") ?? Read("toolCallId") ?? Read("stepName") ?? Read("name"),
        Read("toolCallName"),
        Read("delta") is { } delta ? $"\"{(delta.Length > 40 ? delta[..40] + "..." : delta)}\"" : null,
    }.Where(part => part is not null));

    return $"{type.GetString(),-26} {detail}".TrimEnd();
}

/// <summary>Reads event payloads from Server-Sent Events, a JSON array, or a conformance fixture.</summary>
internal static class StreamReader
{
    public static async IAsyncEnumerable<string> ReadFramesAsync(
        Stream source,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Peek at the first non-whitespace byte to tell JSON from SSE without buffering a live stream.
        var prefix = new MemoryStream();
        int next;
        while ((next = source.ReadByte()) >= 0 && char.IsWhiteSpace((char)next))
        {
        }

        if (next < 0)
        {
            yield break;
        }

        prefix.WriteByte((byte)next);

        if (next is '[' or '{')
        {
            await source.CopyToAsync(prefix, cancellationToken);
            prefix.Position = 0;
            var root = (await JsonDocument.ParseAsync(prefix, cancellationToken: cancellationToken)).RootElement;
            var events = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("stream", out var stream) ? stream : root;
            IEnumerable<JsonElement> items = events.ValueKind == JsonValueKind.Array ? events.EnumerateArray() : [events];
            foreach (var evt in items)
            {
                yield return evt.GetRawText();
            }

            yield break;
        }

        // Server-Sent Events: comments and keep-alives are skipped; event:, id: and retry: are ignored.
        prefix.Position = 0;
        await foreach (var item in SseParser.Create(new ConcatenatedStream(prefix, source)).EnumerateAsync(cancellationToken))
        {
            yield return item.Data;
        }
    }

    /// <summary>The peeked byte followed by the rest of the input.</summary>
    private sealed class ConcatenatedStream(Stream first, Stream second) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = first.Read(buffer, offset, count);
            return read > 0 ? read : second.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await first.ReadAsync(buffer, cancellationToken);
            return read > 0 ? read : await second.ReadAsync(buffer, cancellationToken);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

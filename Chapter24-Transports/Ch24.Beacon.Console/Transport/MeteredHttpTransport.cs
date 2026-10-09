using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;
using Ch24.Beacon.Console.Measurement;

namespace Ch24.Beacon.Console.Transport;

/// <summary>
/// The SDK's HTTP transport, plus a stopwatch and a byte counter. It sends the run exactly as
/// <c>AGUIChatClientOptions(HttpClient, endpoint)</c> does (POST, headers-first, decode with the
/// format <see cref="AGUIEventStreamHandler"/> negotiated) and records each milestone on a
/// <see cref="RunMeter"/>.
/// </summary>
public sealed class MeteredHttpTransport(HttpClient http, string endpoint, RunMeter meter) : IAGUITransport
{
    public async IAsyncEnumerable<BaseEvent> SendAsync(
        RunAgentInput input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        meter.Attempts++;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(input, AGUIJsonSerializerContext.Default.RunAgentInput),
        };

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        meter.Headers = meter.Now;
        meter.Status = response.StatusCode;
        meter.HttpVersion = response.Version;
        meter.ContentType = response.Content.Headers.ContentType?.MediaType;

        if (!response.IsSuccessStatusCode)
        {
            // A refusal (406, 409, 503...) explains itself in the body; keep it for the diagnosis.
            meter.ErrorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        // Count what crosses the wire: wrap the body before the negotiated formatter reads it.
        var body = new CountingStream(await response.Content.ReadAsStreamAsync(cancellationToken));
        var counted = new StreamContent(body);
        foreach (var header in response.Content.Headers)
        {
            counted.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        response.Content = counted;

        await foreach (var evt in response.ReadAGUIEventStreamAsync(cancellationToken))
        {
            meter.Record(evt, body.BytesRead);
            yield return evt;
        }

        meter.Bytes = body.BytesRead;
    }
}

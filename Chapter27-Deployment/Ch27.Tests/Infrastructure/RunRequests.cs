using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Formatting;

namespace Ch27.Tests;

internal static class RunRequests
{
    private static readonly JsonSerializerOptions WireOptions = new() { TypeInfoResolver = AGUIJsonUtilities.DefaultTypeInfoResolver };

    /// <summary>POSTs a run and returns the response without reading or checking it.</summary>
    public static Task<HttpResponseMessage> PostAsJsonRunAsync(this HttpClient http, RunAgentInput input, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/agent")
        {
            Content = JsonContent.Create(input, WireOptions.GetTypeInfo(typeof(RunAgentInput))),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(SseEventStreamFormatter.ServerSentEventsMediaType));
        return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }
}

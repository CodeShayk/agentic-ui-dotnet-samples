using System.Net.Http.Headers;
using System.Net.Http.Json;
using AGUI.Abstractions;
using AGUI.Client;
using Beacon.Client.Core;
using Microsoft.Extensions.AI;

namespace Ch19.Tests.Infrastructure;

/// <summary>The clients' stack (transport, tap, AGUIChatClient, conversation) and a raw POST for refusals.</summary>
public static class ClientStack
{
    public static AgentConversation Conversation(HttpClient http, Uri endpoint, IList<AITool>? tools = null)
    {
        var tap = WireEventTap.ForHttp(http, endpoint.AbsoluteUri);
        return new AgentConversation(
            new AGUIChatClient(new AGUIChatClientOptions { Transport = tap }),
            new ConversationOptions { Tap = tap, Tools = tools ?? [], TurnTimeout = TimeSpan.FromSeconds(30) });
    }

    /// <summary>POSTs a run and returns the response unread, for asserting on refusals before any event.</summary>
    public static Task<HttpResponseMessage> PostAsync(HttpClient http, RunAgentInput input)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/agent")
        {
            Content = JsonContent.Create(input, AGUIJsonSerializerContext.Default.RunAgentInput),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
    }
}

using System.Net;
using System.Text.Json;

namespace Ch14.Beacon.Desk.Connection;

/// <summary>
/// Keeps the agent's explanation of a refusal. The SDK's HTTP transport turns a failure status into an
/// <see cref="HttpRequestException"/> and discards the response, problem body and all, so this handler reads a
/// 4xx <c>application/problem+json</c> body first and throws an <see cref="AgentRefusedException"/> that carries
/// its title and detail. <c>AgentConversation</c> keeps only the status of a failure, so the handler also records
/// the refusal in the circuit's <see cref="AgentRefusals"/>, where the approval dialog reads it.
/// </summary>
/// <param name="refusals">Where this circuit's latest refusal is kept; <see langword="null"/> records nothing.</param>
public sealed class ProblemDetailsHandler(AgentRefusals? refusals = null) : DelegatingHandler
{
    public const string ProblemMediaType = "application/problem+json";

    // A problem document is a few hundred bytes. A longer body isn't read.
    private const int MaxProblemBytes = 16 * 1024;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        refusals?.Clear(); // the latest refusal always belongs to the latest request

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is < 400 or >= 500 || response.Content.Headers.ContentType?.MediaType != ProblemMediaType)
        {
            return response;
        }

        AgentRefusedException refusal;
        using (response)
        {
            var (title, detail) = await ReadProblemAsync(response.Content, cancellationToken).ConfigureAwait(false);
            refusal = new AgentRefusedException(response.StatusCode, title, detail);
        }

        refusals?.Record(refusal);
        throw refusal;
    }

    // RFC 9457: title and detail are strings. Anything unreadable leaves a refusal with only its status.
    private static async Task<(string? Title, string? Detail)> ReadProblemAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            await content.LoadIntoBufferAsync(MaxProblemBytes, cancellationToken).ConfigureAwait(false);
            using var problem = JsonDocument.Parse(await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
            return (Text(problem.RootElement, "title"), Text(problem.RootElement, "detail"));
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException)
        {
            return (null, null);
        }

        static string? Text(JsonElement problem, string name) =>
            problem.ValueKind == JsonValueKind.Object && problem.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}

/// <summary>
/// The agent refused a request with a 4xx status and said why. It is an <see cref="HttpRequestException"/>
/// with <see cref="HttpRequestException.StatusCode"/> set, so everything that classifies failures by status
/// (<c>AgentConversation</c>'s <c>Rejected</c>) treats it as the refusal it is.
/// </summary>
/// <param name="statusCode">The 4xx status.</param>
/// <param name="title">The problem's <c>title</c>: the same for every refusal of its kind.</param>
/// <param name="detail">The problem's <c>detail</c>: this occurrence.</param>
public sealed class AgentRefusedException(HttpStatusCode statusCode, string? title, string? detail)
    : HttpRequestException(
        HttpRequestError.Unknown,
        $"The agent refused the request (HTTP {(int)statusCode}): {title ?? "no reason given"}.",
        inner: null,
        statusCode)
{
    /// <summary>The problem's <c>title</c>, such as "Interrupt already answered".</summary>
    public string? Title { get; } = title;

    /// <summary>The problem's <c>detail</c>.</summary>
    public string? Detail { get; } = detail;
}

/// <summary>The agent's latest refusal on this circuit, with its reason.</summary>
public sealed class AgentRefusals
{
    private AgentRefusedException? _latest;

    /// <summary>The refusal of the latest request, or <see langword="null"/> when it wasn't refused with a problem body.</summary>
    public AgentRefusedException? Latest => Volatile.Read(ref _latest);

    public void Record(AgentRefusedException refusal) => Volatile.Write(ref _latest, refusal);

    public void Clear() => Volatile.Write(ref _latest, null);
}

namespace Ch04.Beacon.Console;

/// <summary>
/// Writes each request body, the <c>RunAgentInput</c>, before it is sent, so you can see exactly
/// what <c>AGUIChatClient</c> puts on the wire. Enable it with <c>--show-requests</c>.
/// </summary>
public sealed class RunInputLogger(TextWriter output) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            await output.WriteLineAsync($"{request.Method} {request.RequestUri} {body}");
        }

        return await base.SendAsync(request, cancellationToken);
    }
}

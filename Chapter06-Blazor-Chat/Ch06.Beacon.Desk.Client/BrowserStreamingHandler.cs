using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Ch06.Beacon.Desk.Client;

/// <summary>
/// Tells the browser's <c>fetch</c> whether to hand the response body over as it arrives. .NET 10 and
/// later stream by default; setting it on each request keeps the AG-UI stream incremental even if the
/// app turns that default off, and makes the dependency visible.
/// </summary>
public sealed class BrowserStreamingHandler(bool enabled = true) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.SetBrowserResponseStreamingEnabled(enabled);
        return base.SendAsync(request, cancellationToken);
    }
}

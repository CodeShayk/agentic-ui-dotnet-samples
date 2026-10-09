namespace Ch24.Beacon.Console.Transport;

/// <summary>
/// Sets the HTTP version on every request. <see cref="HttpClient.DefaultRequestVersion"/> only applies
/// to requests <see cref="HttpClient"/> creates itself (<c>GetAsync</c>, <c>PostAsync</c>); the AG-UI
/// transport builds its own <see cref="HttpRequestMessage"/>, which starts at HTTP/1.1.
/// </summary>
public sealed class RequestVersionHandler(Version version, HttpVersionPolicy policy) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Version = version;
        request.VersionPolicy = policy;
        return base.SendAsync(request, cancellationToken);
    }
}

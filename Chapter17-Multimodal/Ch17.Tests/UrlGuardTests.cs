using System.Net;
using Beacon.Testing;
using Ch17.Beacon.Agent.Media;
using Ch17.Media;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Ch17.Tests;

/// <summary>URL sources as a server-side request forgery risk: every way to point Beacon's server somewhere it shouldn't go.</summary>
public sealed class UrlGuardTests
{
    private const string Plate = "https://media.contoso-climate.example/wo-1003/nameplate.png";

    private static readonly IOptions<MediaOptions> Settings = Options.Create(new MediaOptions
    {
        AllowedUrlHosts = [FakeDns.MediaHost, "files.contoso-climate.example"],
    });

    private static MediaUrlFetcher Fetcher(FakeMediaServer server, HostResolver dns) =>
        new(new FakeHttpClientFactory(server), new UrlSourceGuard(Settings, dns), MediaPolicy.Beacon, Settings);

    public static TheoryData<string, HostResolver, string> RefusedBeforeAnyRequest => new()
    {
        { "http://media.contoso-climate.example/wo-1003/nameplate.png", FakeDns.Public, "Beacon fetches attachments only over HTTPS" },
        { "https://localhost/nameplate.png", FakeDns.Of(("localhost", "127.0.0.1")), "the URL's host isn't one Beacon fetches attachments from" },
        { "https://10.0.0.8/nameplate.png", FakeDns.Public, "Beacon doesn't fetch attachments from IP addresses" },
        { "https://169.254.169.254/latest/meta-data/", FakeDns.Public, "Beacon doesn't fetch attachments from IP addresses" },
        { "https://[::1]/nameplate.png", FakeDns.Public, "Beacon doesn't fetch attachments from IP addresses" },
        { "https://user:secret@media.contoso-climate.example/nameplate.png", FakeDns.Public, "the URL carries credentials" },
        { "https://media.contoso-climate.example:8443/nameplate.png", FakeDns.Public, "Beacon fetches attachments only from the standard HTTPS port" },
        { "https://evil.example/nameplate.png", FakeDns.Of(("evil.example", "20.1.2.3")), "the URL's host isn't one Beacon fetches attachments from" },
        { Plate, FakeDns.Of((FakeDns.MediaHost, "10.20.30.40")), "the URL's host resolves to a private, loopback or reserved address" },
        { Plate, FakeDns.Of((FakeDns.MediaHost, "20.42.65.92"), (FakeDns.MediaHost, "127.0.0.1")), "the URL's host resolves to a private, loopback or reserved address" },
        { Plate, FakeDns.Of(), "the URL's host name doesn't resolve" },
    };

    [Theory]
    [MemberData(nameof(RefusedBeforeAnyRequest))]
    public async Task Refused_without_sending_a_request(string url, HostResolver dns, string reason)
    {
        var server = new FakeMediaServer().Serve(url, Samples.Nameplate, "image/png");

        var result = await Fetcher(server, dns).FetchAsync(new Uri(url), "image/png", TestContext.Current.CancellationToken);

        Assert.Null(result.Content);
        Assert.Equal(MediaRefusalCode.UrlRefused, result.Code);
        Assert.Equal(reason, result.Reason);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_redirect_to_a_metadata_address_is_refused_at_the_second_hop()
    {
        var server = new FakeMediaServer().Redirect(Plate, "https://169.254.169.254/latest/meta-data/iam/");

        var result = await Fetcher(server, FakeDns.Public).FetchAsync(new Uri(Plate), "image/png", TestContext.Current.CancellationToken);

        Assert.Equal("it redirected to a URL Beacon won't fetch: Beacon doesn't fetch attachments from IP addresses", result.Reason);
        Assert.Equal([new Uri(Plate)], server.Requests);
    }

    [Fact]
    public async Task A_redirect_to_an_allowed_host_with_a_private_address_is_refused()
    {
        // The second host is on the allow-list, but its DNS answer points inside the network.
        var server = new FakeMediaServer().Redirect(Plate, "https://files.contoso-climate.example/internal/plate.png");
        var dns = FakeDns.Of((FakeDns.MediaHost, "20.42.65.92"), ("files.contoso-climate.example", "192.168.1.20"));

        var result = await Fetcher(server, dns).FetchAsync(new Uri(Plate), "image/png", TestContext.Current.CancellationToken);

        Assert.Equal("it redirected to a host Beacon won't fetch from: the URL's host resolves to a private, loopback or reserved address", result.Reason);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task A_redirect_to_plain_HTTP_is_refused()
    {
        var server = new FakeMediaServer().Redirect(Plate, "http://media.contoso-climate.example/wo-1003/nameplate.png");

        var result = await Fetcher(server, FakeDns.Public).FetchAsync(new Uri(Plate), null, TestContext.Current.CancellationToken);

        Assert.Equal("it redirected to a URL Beacon won't fetch: Beacon fetches attachments only over HTTPS", result.Reason);
    }

    [Fact]
    public async Task Redirect_chains_are_bounded()
    {
        var server = new FakeMediaServer()
            .Redirect(Plate, "https://media.contoso-climate.example/1")
            .Redirect("https://media.contoso-climate.example/1", "https://media.contoso-climate.example/2")
            .Redirect("https://media.contoso-climate.example/2", "https://media.contoso-climate.example/3")
            .Redirect("https://media.contoso-climate.example/3", "https://media.contoso-climate.example/4");

        var result = await Fetcher(server, FakeDns.Public).FetchAsync(new Uri(Plate), null, TestContext.Current.CancellationToken);

        Assert.Equal("it redirected too many times", result.Reason);
        Assert.Equal(4, server.Requests.Count);
    }

    [Fact]
    public async Task An_allowed_URL_is_fetched_and_checked_like_inline_data()
    {
        var server = new FakeMediaServer()
            .Redirect(Plate, "https://files.contoso-climate.example/blobs/7f3a.png")
            .Serve("https://files.contoso-climate.example/blobs/7f3a.png", Samples.Nameplate, "image/png");

        var result = await Fetcher(server, FakeDns.Public).FetchAsync(new Uri(Plate), "image/*", TestContext.Current.CancellationToken);

        Assert.Equal("image/png", result.Content!.MediaType); // undeclared ("image/*"): the bytes decide
        Assert.Equal(Samples.Nameplate, result.Content.Data.ToArray());
    }

    [Fact]
    public async Task A_fetched_file_must_be_what_it_claims()
    {
        var server = new FakeMediaServer().Serve(Plate, Samples.WindowsProgram, "image/png");

        var result = await Fetcher(server, FakeDns.Public).FetchAsync(new Uri(Plate), "image/png", TestContext.Current.CancellationToken);

        Assert.Equal(MediaRefusalCode.TypeMismatch, result.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_fetched_file_over_the_limit_is_refused_whether_or_not_it_says_so(bool withLength)
    {
        var server = new FakeMediaServer().Serve(Plate, Samples.PngOfSize((int)MediaPolicy.Beacon.LargestFile + 1), "image/png", withLength);

        var result = await Fetcher(server, FakeDns.Public).FetchAsync(new Uri(Plate), "image/png", TestContext.Current.CancellationToken);

        Assert.Equal("the file is over 10.0 MB", result.Reason);
    }

    [Fact]
    public async Task The_connection_itself_refuses_a_private_address()
    {
        // The real handler, with no pre-check: the connect callback resolves, sees loopback and never opens a socket.
        var guard = new UrlSourceGuard(Settings, FakeDns.Of((FakeDns.MediaHost, "127.0.0.1")));
        using var http = new HttpClient(MediaUrlFetcher.CreateHandler(guard));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(Plate, TestContext.Current.CancellationToken));

        Assert.IsType<MediaFetchRefusedException>(error.InnerException);
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("20.42.65.92", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.0.10", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::10", false)]
    [InlineData("::ffff:10.0.0.5", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("64:ff9b::a00:1", false)]
    public void Only_public_unicast_addresses_are_allowed(string address, bool isPublic) =>
        Assert.Equal(isPublic, AddressPolicy.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task Through_the_agent_an_allowed_URL_becomes_inline_data_for_the_model()
    {
        var ct = TestContext.Current.CancellationToken;
        var model = new RecordedChatClient(RecordedChatClient.TextTurn("msg-1", "Got it."));
        var server = new FakeMediaServer().Serve(Plate, Samples.Nameplate, "image/png");
        await using var agent = new AgentHost(model, services: AgentHost.FakeInternet(server, FakeDns.Public));

        await AGUIWire.RunRawAsync(agent.CreateClient(), "/agent", Runs.User(Runs.ImageUrl(Plate)), ct);

        var data = Assert.IsType<DataContent>(model.Calls.Single().Messages.Single().Contents.Single());
        Assert.Equal(Samples.Nameplate, data.Data.ToArray());
        Assert.Single(server.Requests);
    }
}

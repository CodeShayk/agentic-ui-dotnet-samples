using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using Ch17.Beacon.Agent;
using Ch17.Beacon.Agent.Media;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch17.Tests;

/// <summary>The synthetic media: the three sample files, and bytes built in code for the hostile cases.</summary>
public static class Samples
{
    public static byte[] Nameplate { get; } = Read("eq-601-nameplate.png");

    public static byte[] FanRecording { get; } = Read("eq-601-fan-grinding.wav");

    public static byte[] Bulletin { get; } = Read("hp-24s-service-bulletin.pdf");

    /// <summary>The start of a Windows program: "MZ", then padding. Harmless, but not an image.</summary>
    public static byte[] WindowsProgram { get; } = [(byte)'M', (byte)'Z', 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, .. new byte[200]];

    /// <summary>An MP4 video's opening box ("ftyp" with the isom brand).</summary>
    public static byte[] Mp4Video { get; } = [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', .. new byte[64]];

    /// <summary>A PNG signature followed by zeros: passes the sniff, fails the size limit when large enough.</summary>
    public static byte[] PngOfSize(int size)
    {
        var bytes = new byte[size];
        Nameplate.AsSpan(0, 8).CopyTo(bytes);
        return bytes;
    }

    public static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "samples", name));
}

/// <summary>Builds <see cref="RunAgentInput"/>s with media parts.</summary>
public static class Runs
{
    public static RunAgentInput User(params AGUIInputContent[] parts) => new()
    {
        ThreadId = "thread-17",
        RunId = $"run-{Guid.NewGuid():N}",
        Messages = [new AGUIUserMessage { Id = "user-1", Content = new AGUIContent(parts) }],
    };

    public static AGUITextInputContent Text(string text) => new() { Text = text };

    public static AGUIImageInputContent Image(byte[] data, string mimeType = "image/png", string? filename = null) =>
        new() { Source = Data(data, mimeType), Metadata = Filename(filename) };

    public static AGUIAudioInputContent Audio(byte[] data, string mimeType = "audio/wav") => new() { Source = Data(data, mimeType) };

    public static AGUIDocumentInputContent Document(byte[] data, string mimeType = "application/pdf") => new() { Source = Data(data, mimeType) };

    public static AGUIVideoInputContent Video(byte[] data, string mimeType = "video/mp4") => new() { Source = Data(data, mimeType) };

    public static AGUIImageInputContent ImageUrl(string url, string? mimeType = null) =>
        new() { Source = new AGUIInputContentUrlSource { Value = url, MimeType = mimeType } };

    public static AGUIDocumentInputContent ProviderFile(string handle) =>
        new() { Source = new AGUIInputContentFileSource { Value = handle, Provider = "openai", MimeType = "application/pdf" } };

    private static AGUIInputContentDataSource Data(byte[] data, string mimeType) =>
        new() { Value = Convert.ToBase64String(data), MimeType = mimeType };

    private static JsonElement? Filename(string? filename) =>
        filename is null ? null : JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["filename"] = filename });
}

/// <summary>Chapter 17's agent in-process, optionally over a recorded model and a fake media server.</summary>
public sealed class AgentHost : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public AgentHost(IChatClient? model = null, Action<IServiceCollection>? services = null, Action<IWebHostBuilder>? host = null)
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AI:ScriptedChunkDelayMilliseconds", "0");
            host?.Invoke(builder);
            builder.ConfigureTestServices(s =>
            {
                if (model is not null)
                {
                    s.AddKeyedSingleton(Program.ModelKey, model);
                }

                services?.Invoke(s);
            });
        });
    }

    public WebApplicationFactory<Program> Factory => _factory;

    public IServiceProvider Services => _factory.Services;

    public HttpClient CreateClient(params DelegatingHandler[] handlers) => _factory.CreateDefaultClient(handlers);

    /// <summary>Routes the agent's URL fetches to <paramref name="server"/> and its DNS lookups to <paramref name="dns"/>.</summary>
    public static Action<IServiceCollection> FakeInternet(FakeMediaServer server, HostResolver dns) => s =>
    {
        s.AddSingleton(dns);
        s.AddHttpClient(MediaUrlFetcher.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => server);
    };

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}

/// <summary>Records the bodies of the requests a client sends, so tests can read the wire JSON.</summary>
public sealed class RequestCapture : DelegatingHandler
{
    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
        }

        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>A pretend internet for URL sources: canned responses by URL, and a record of every request.</summary>
public sealed class FakeMediaServer : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public List<Uri> Requests { get; } = [];

    public FakeMediaServer Serve(string url, byte[] bytes, string contentType, bool withLength = true)
    {
        _routes[url] = () =>
        {
            HttpContent content = withLength ? new ByteArrayContent(bytes) : new UnknownLengthContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };
        return this;
    }

    public FakeMediaServer Redirect(string url, string location)
    {
        _routes[url] = () => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } };
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(_routes.TryGetValue(request.RequestUri!.ToString(), out var respond)
            ? respond()
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found", Encoding.UTF8) });
    }
}

/// <summary>A body with no Content-Length header, as a server that streams (or lies) would send it.</summary>
public sealed class UnknownLengthContent(byte[] bytes) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

public sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

public static class FakeDns
{
    public const string MediaHost = "media.contoso-climate.example";

    /// <summary>A resolver over a fixed table; unknown names don't resolve.</summary>
    public static HostResolver Of(params (string Host, string Address)[] table) => (host, _) =>
    {
        var addresses = table.Where(e => string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase)).Select(e => IPAddress.Parse(e.Address)).ToArray();
        return addresses.Length > 0 ? Task.FromResult(addresses) : Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound));
    };

    /// <summary>Contoso's media hosts on public addresses. Nothing connects to them: the fake server answers.</summary>
    public static HostResolver Public => Of((MediaHost, "20.42.65.92"), ("files.contoso-climate.example", "20.42.65.93"));
}

/// <summary>Reads a raw SSE response into frames, without re-serializing them.</summary>
public static class Sse
{
    public static async Task<IReadOnlyList<JsonElement>> PostAsync(HttpClient http, string json, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/agent") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        List<JsonElement> frames = [];
        await foreach (var item in System.Net.ServerSentEvents.SseParser.Create(body).EnumerateAsync(cancellationToken))
        {
            using var document = JsonDocument.Parse(item.Data);
            frames.Add(document.RootElement.Clone());
        }

        return frames;
    }

    public static string TypeOf(JsonElement frame) => frame.GetProperty("type").GetString()!;

    public static string Text(IEnumerable<JsonElement> frames) =>
        string.Concat(frames.Where(f => TypeOf(f) == "TEXT_MESSAGE_CONTENT").Select(f => f.GetProperty("delta").GetString()));
}

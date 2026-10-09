using System.Text.Json;

namespace Ch25.Beacon.Console;

/// <summary>
/// Saves each AG-UI run to a folder as the client sees it: the request as <c>{runId}.request.json</c>
/// and the response body, byte for byte and as it streams, as <c>{runId}.sse</c>. Because it sits in
/// the client's <see cref="HttpClient"/>, it records what actually arrived, proxies included.
/// A capture holds the whole conversation, so treat it like the data it contains.
/// </summary>
public sealed class WireCaptureHandler(string folder) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var name = $"run-{DateTime.UtcNow:HHmmssfff}";
        if (request.Content is not null)
        {
            var json = await request.Content.ReadAsStringAsync(cancellationToken);
            using (var input = JsonDocument.Parse(json))
            {
                if (input.RootElement.TryGetProperty("runId", out var runId) && runId.GetString() is { Length: > 0 } id)
                {
                    name = string.Concat(id.Split(Path.GetInvalidFileNameChars()));
                }
            }

            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, name + ".request.json"), json, cancellationToken);
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
        {
            return response;
        }

        // Swap the body for one that copies every byte to the capture file as the client reads it.
        var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var capture = File.Create(Path.Combine(folder, name + ".sse"));
        var teed = new StreamContent(new TeeStream(body, capture));
        foreach (var header in response.Content.Headers)
        {
            teed.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        response.Content = teed;
        return response;
    }

    /// <summary>A read-only stream that writes what it reads to a second stream.</summary>
    private sealed class TeeStream(Stream source, Stream copy) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            copy.Write(buffer, offset, read);
            copy.Flush();
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            await copy.WriteAsync(buffer[..read], cancellationToken);
            await copy.FlushAsync(cancellationToken);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                source.Dispose();
                copy.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await source.DisposeAsync();
            await copy.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}

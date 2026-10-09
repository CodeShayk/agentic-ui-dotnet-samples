using AGUI.Client;
using Ch24.Beacon.Console.Transport;
using Microsoft.Extensions.AI;

namespace Ch24.Beacon.Console.Measurement;

/// <summary>Runs one question through <see cref="AGUIChatClient"/> and returns what the meter saw.</summary>
public static class RunProbe
{
    /// <summary>The bound on one run, the client's counterpart of the agent's two-minute run budget.</summary>
    public static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(2);

    public static async Task<RunMeter> RunAsync(
        HttpClient http,
        string endpoint,
        string question,
        bool retry = false,
        Action<string>? onText = null,
        CancellationToken cancellationToken = default,
        TimeSpan? runTimeout = null)
    {
        var meter = new RunMeter();
        IAGUITransport transport = new MeteredHttpTransport(http, endpoint, meter);
        if (retry)
        {
            transport = new RetryBeforeFirstEventTransport(transport, maxAttempts: 5);
        }

        using var client = new AGUIChatClient(new AGUIChatClientOptions { Transport = transport });
        List<ChatMessage> messages = [new(ChatRole.User, question) { MessageId = $"user_{Guid.NewGuid():N}" }];

        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var bound = runTimeout ?? RunTimeout;
        run.CancelAfter(bound);
        try
        {
            await foreach (var update in client.GetStreamingResponseAsync(messages, new ChatOptions(), run.Token))
            {
                if (update.Text is { Length: > 0 } text)
                {
                    onText?.Invoke(text);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException { InnerException: TimeoutException })
        {
            meter.Fail(ex);
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            meter.Fail(new TimeoutException($"The run took longer than {bound.TotalSeconds:0.#} seconds."));
        }

        return meter;
    }
}

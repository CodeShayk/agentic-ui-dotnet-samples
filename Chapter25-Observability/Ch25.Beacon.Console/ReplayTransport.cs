using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;
using AGUI.Formatting;

namespace Ch25.Beacon.Console;

/// <summary>
/// An <see cref="IAGUITransport"/> that answers every run from a captured <c>.sse</c> file instead of
/// a server. Plugged into <see cref="AGUIChatClient"/>, it sends a recorded run through exactly the
/// code path a live run takes, so a rendering or reducer bug reproduces without the model.
/// </summary>
public sealed class ReplayTransport(string capturePath, Action<BaseEvent>? onEvent = null) : IAGUITransport
{
    public async IAsyncEnumerable<BaseEvent> SendAsync(
        RunAgentInput input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var capture = File.OpenRead(capturePath);
        await foreach (var evt in new SseEventStreamFormatter().ReadAsync(capture, cancellationToken))
        {
            onEvent?.Invoke(evt);
            yield return evt;
        }
    }
}

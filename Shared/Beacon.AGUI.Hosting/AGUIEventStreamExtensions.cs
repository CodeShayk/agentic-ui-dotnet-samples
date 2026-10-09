using System.Runtime.CompilerServices;
using AGUI.Abstractions;

namespace Beacon.AGUI.Hosting;

/// <summary>Transformations of an AG-UI event stream on its way to the formatter.</summary>
public static class AGUIEventStreamExtensions
{
    /// <summary>
    /// Declares <paramref name="protocolVersion"/> on every <c>RUN_STARTED</c> in the stream that doesn't
    /// already declare a version. The value is the producer's own version, never an echo of the input's,
    /// and a <c>RUN_STARTED</c> that carries a declaration (a replayed run from an older producer, say)
    /// keeps it, so a replay of mixed-age history stays truthful. The events are updated in place.
    /// </summary>
    /// <param name="events">The stream, from <c>AsAGUIEventStreamAsync</c> or built by hand.</param>
    /// <param name="protocolVersion">A version identifier the spec publishes, <c>MAJOR.MINOR</c>, such as <c>"1.0"</c>.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The same events, in the same order.</returns>
    /// <exception cref="ArgumentException"><paramref name="protocolVersion"/> isn't <c>MAJOR.MINOR</c>.</exception>
    public static IAsyncEnumerable<BaseEvent> WithProtocolVersion(
        this IAsyncEnumerable<BaseEvent> events,
        string protocolVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (!IsVersionIdentifier(protocolVersion))
        {
            throw new ArgumentException($"'{protocolVersion}' isn't an AG-UI version identifier (MAJOR.MINOR, such as \"1.0\").", nameof(protocolVersion));
        }

        return Stamp(events, protocolVersion, cancellationToken);
    }

    // The spec's identifiers are the segment a frozen version lives under: MAJOR.MINOR, compared numerically.
    internal static bool IsVersionIdentifier(string? value) =>
        value?.Split('.') is [{ Length: > 0 } major, { Length: > 0 } minor]
        && major.All(char.IsAsciiDigit)
        && minor.All(char.IsAsciiDigit);

    private static async IAsyncEnumerable<BaseEvent> Stamp(
        IAsyncEnumerable<BaseEvent> events,
        string protocolVersion,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (evt is RunStartedEvent { ProtocolVersion: null or "" } started)
            {
                started.ProtocolVersion = protocolVersion;
            }

            yield return evt;
        }
    }
}

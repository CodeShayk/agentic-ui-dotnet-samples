using System.Text.Json;

namespace Ch03.StreamValidator;

/// <summary>
/// Validates an AG-UI 1.0 event stream as wire JSON. Feed it events in arrival order with
/// <see cref="Observe"/>, then call <see cref="Complete"/> when the stream ends. Findings come back
/// as diagnostics, never as exceptions, so one bad event doesn't hide the next one.
/// </summary>
public sealed class AGUIStreamValidator
{
    private readonly RunVerifier _verifier;
    private readonly ChunkExpander _chunks;
    private readonly List<StreamDiagnostic> _diagnostics = [];
    private bool _completed;

    public AGUIStreamValidator(StreamValidatorOptions? options = null)
    {
        _verifier = new RunVerifier(options ?? new StreamValidatorOptions());
        _chunks = new ChunkExpander(_verifier.OwnerOfOpenItem);
    }

    /// <summary>How many events the validator has seen.</summary>
    public int EventCount { get; private set; }

    /// <summary>Every diagnostic so far, in the order found.</summary>
    public IReadOnlyList<StreamDiagnostic> Diagnostics => _diagnostics;

    /// <summary>Checks the next event and returns what it found about this event.</summary>
    public IReadOnlyList<StreamDiagnostic> Observe(JsonElement wireEvent)
    {
        ObjectDisposedException.ThrowIf(_completed, this);

        var sink = new DiagnosticSink(EventCount++, eventType: null);

        // 1. Enforcement: is this a well-formed event of a type the protocol describes?
        if (EventShapes.Check(wireEvent, sink) is { } evt)
        {
            // 2. Expansion: a chunk becomes the start, content and end events it stands for.
            foreach (var expanded in _chunks.Expand(evt, sink))
            {
                // 3. Verification: lifecycle, pairing, ownership.
                _verifier.Apply(expanded, sink);
            }
        }

        _diagnostics.AddRange(sink.Items);
        return sink.Items;
    }

    /// <summary>Runs the end-of-stream checks and returns the full report.</summary>
    public ValidationReport Complete()
    {
        if (!_completed)
        {
            _completed = true;
            var sink = new DiagnosticSink(EventCount, eventType: null);
            _verifier.Complete(sink);
            _diagnostics.AddRange(sink.Items);
        }

        return new ValidationReport([.. _diagnostics], EventCount, _verifier.RunCount);
    }

    /// <summary>Validates a complete stream.</summary>
    public static ValidationReport Validate(IEnumerable<JsonElement> events, StreamValidatorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(events);

        var validator = new AGUIStreamValidator(options);
        foreach (var evt in events)
        {
            validator.Observe(evt);
        }

        return validator.Complete();
    }

    /// <summary>Checks one event against the schema alone, with no ordering rules.</summary>
    public static IReadOnlyList<StreamDiagnostic> CheckEvent(JsonElement wireEvent)
    {
        var sink = new DiagnosticSink(0, eventType: null);
        EventShapes.Check(wireEvent, sink);
        return sink.Items;
    }
}

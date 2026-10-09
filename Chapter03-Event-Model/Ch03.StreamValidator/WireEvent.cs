using System.Text.Json;

namespace Ch03.StreamValidator;

/// <summary>
/// An event that passed enforcement, with the fields the ordering rules need already read out.
/// Chunk expansion also creates these, marked <see cref="Synthesized"/>, for the start, content and
/// end events a chunk stands for.
/// </summary>
internal sealed record WireEvent(int Index, string Type, JsonElement Json)
{
    public string? MessageId { get; init; }

    public string? ToolCallId { get; init; }

    public string? StepName { get; init; }

    /// <summary>The invocation a SUBAGENT_* event announces or closes (its identity, not attribution).</summary>
    public string? SubagentRunId { get; init; }

    /// <summary>The <c>subagentRunId</c> tag on an attributable event; null means the parent agent.</summary>
    public string? Owner { get; init; }

    public string? Role { get; init; }

    public string? Name { get; init; }

    public string? ToolCallName { get; init; }

    public string? ParentMessageId { get; init; }

    public string? Delta { get; init; }

    public bool Synthesized { get; init; }
}

/// <summary>Collects the diagnostics for one event.</summary>
internal sealed class DiagnosticSink(int eventIndex, string? eventType)
{
    private readonly List<StreamDiagnostic> _items = [];

    public int EventIndex { get; } = eventIndex;

    public string? EventType { get; set; } = eventType;

    public IReadOnlyList<StreamDiagnostic> Items => _items;

    public bool HasErrors => _items.Any(d => d.Severity == DiagnosticSeverity.Error);

    public void Report(ValidationRule rule, string message, string? path = null) =>
        _items.Add(new StreamDiagnostic(rule, EventIndex, EventType, message, path));
}

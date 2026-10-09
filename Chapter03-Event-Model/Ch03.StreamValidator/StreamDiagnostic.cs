namespace Ch03.StreamValidator;

/// <summary>How serious a finding is.</summary>
public enum DiagnosticSeverity
{
    /// <summary>
    /// The producer broke a rule, but a conforming consumer survives it: it drops, strips or skips
    /// the material, or the rule is a SHOULD.
    /// </summary>
    Warning,

    /// <summary>
    /// The producer broke a MUST: a conforming consumer must reject the stream, or the identity of a
    /// run, message or call is broken. The one softening is the short list of legacy nulls in
    /// DEPRECATIONS.md (such as <c>"outcome": null</c>), which 1.0 consumers repair with a warning
    /// until 2027-09-17; a producer that writes one is still at fault, so it stays an error here.
    /// </summary>
    Error,
}

/// <summary>One finding about one event (or about the end of the stream).</summary>
/// <param name="Rule">The rule that was broken.</param>
/// <param name="EventIndex">
/// The zero-based position of the event in the stream. End-of-stream findings use the event count.
/// </param>
/// <param name="EventType">The wire <c>type</c> of the event, when it has one.</param>
/// <param name="Message">What went wrong, for a person to read.</param>
/// <param name="Path">A JSON Pointer to the offending value inside the event, when the problem is a field.</param>
public sealed record StreamDiagnostic(
    ValidationRule Rule,
    int EventIndex,
    string? EventType,
    string Message,
    string? Path = null)
{
    public string RuleId => Rule.Id;

    public DiagnosticSeverity Severity => Rule.Severity;

    public override string ToString()
    {
        var severity = Severity == DiagnosticSeverity.Error ? "error" : "warning";
        var where = EventType is null ? $"#{EventIndex}" : $"#{EventIndex} {EventType}";
        var path = string.IsNullOrEmpty(Path) ? string.Empty : $" at {Path}";
        return $"{severity} {RuleId} [{where}]{path}: {Message}";
    }
}

/// <summary>Everything the validator found in one stream.</summary>
public sealed record ValidationReport(
    IReadOnlyList<StreamDiagnostic> Diagnostics,
    int EventCount,
    int RunCount)
{
    public IEnumerable<StreamDiagnostic> Errors => Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);

    public IEnumerable<StreamDiagnostic> Warnings => Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning);

    /// <summary>True when a conforming consumer would accept the stream.</summary>
    public bool IsValid => !Errors.Any();

    public override string ToString() =>
        $"{EventCount} events, {RunCount} run(s), {Errors.Count()} error(s), {Warnings.Count()} warning(s)";
}

using System.Text.Json;

namespace Ch03.StreamValidator.Tests;

/// <summary>Helpers for writing streams as JSON in tests.</summary>
internal static class Wire
{
    /// <summary>Parses each line as one wire event.</summary>
    public static IReadOnlyList<JsonElement> Events(params string[] lines) =>
        [.. lines.Select(line => JsonDocument.Parse(line).RootElement.Clone())];

    public static ValidationReport Validate(params string[] lines) => AGUIStreamValidator.Validate(Events(lines));

    /// <summary>The distinct rule ids a report raised, sorted, for exact comparisons.</summary>
    public static IReadOnlyList<string> RuleIds(ValidationReport report) =>
        [.. report.Diagnostics.Select(d => d.RuleId).Distinct().Order(StringComparer.Ordinal)];

    // Well-formed bookends, so each invalid stream differs from a valid one by a single defect.
    public const string Started = """{"type":"RUN_STARTED","threadId":"t-1","runId":"r-1","protocolVersion":"1.0"}""";
    public const string Finished = """{"type":"RUN_FINISHED","threadId":"t-1","runId":"r-1"}""";

    public static string Directory(string relative) => Path.Combine(AppContext.BaseDirectory, "Conformance", relative);
}

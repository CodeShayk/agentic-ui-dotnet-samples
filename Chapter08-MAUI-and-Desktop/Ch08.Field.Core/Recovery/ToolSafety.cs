using System.Text.Json;
using System.Text.RegularExpressions;
using Beacon.Client.Core;

namespace Ch08.Field.Core.Recovery;

/// <summary>
/// What the client knows about the agent's tools: which ones only read, and how to check whether a
/// consequential one took effect. Anything not on the read-only list is treated as having side effects,
/// so a tool added to the agent tomorrow fails safe on the phone today.
/// </summary>
/// <param name="readOnlyTools">The names of tools that change nothing.</param>
public sealed partial class ToolSafety(IEnumerable<string> readOnlyTools)
{
    private readonly HashSet<string> _readOnly = new(readOnlyTools, StringComparer.Ordinal);

    /// <summary>Beacon's read-only tools (Chapter 5's <c>BeaconTools.CreateReadOnlyTools</c>).</summary>
    public static ToolSafety Beacon { get; } = new(
        ["get_work_order", "list_work_orders", "search_parts", "check_warranty", "get_fault_history", "get_schedule"]);

    /// <summary>True if the tool only reads, so running it twice is harmless.</summary>
    /// <param name="toolName">The tool's name.</param>
    public bool IsReadOnly(string toolName) => _readOnly.Contains(toolName);

    /// <summary>
    /// The work order <paramref name="call"/> acts on, which a read-only check can look at, or
    /// <see langword="null"/>. Beacon's consequential tools all act on a work order. The argument is model
    /// output, so only a well-formed work order ID is accepted: the check is sent to the model as a message.
    /// </summary>
    /// <param name="call">The call that may or may not have happened.</param>
    public string? CheckTarget(ToolCallView call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return Argument(call, "workOrderId") is { } id && WorkOrderId().Match(id) is { Success: true } match && match.Length == id.Length
            ? id
            : null;
    }

    /// <summary>The first work order a message names, for when the run's calls never arrived; or <see langword="null"/>.</summary>
    /// <param name="message">The user's message.</param>
    public string? CheckTarget(string? message) =>
        message is not null && WorkOrderId().Match(message) is { Success: true } match ? match.Value : null;

    /// <summary>A question that only reads and shows what happened to <paramref name="target"/>: a placed order moves its work order to AwaitingParts.</summary>
    /// <param name="target">A work order ID from <see cref="CheckTarget(ToolCallView)"/> or <see cref="CheckTarget(string)"/>.</param>
    public string CheckQuestion(string target) => $"What is the status of {target}?";

    // Arguments are untrusted model output; they're only ever shown or echoed as text.
    internal static string? Argument(ToolCallView call, string name) =>
        call.Arguments is { ValueKind: JsonValueKind.Object } arguments
        && arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [GeneratedRegex(@"\bWO-[0-9]{1,8}\b", RegexOptions.CultureInvariant)]
    private static partial Regex WorkOrderId();
}

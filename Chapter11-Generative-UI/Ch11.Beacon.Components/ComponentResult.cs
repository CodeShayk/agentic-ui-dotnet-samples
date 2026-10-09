using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ch11.Beacon.Components;

/// <summary>
/// What the server answers every component call with: the <c>TOOL_CALL_RESULT</c> content of a
/// <c>show_*</c> call. The model reads it to learn whether its component was shown; a client reads it
/// to know what to render.
/// </summary>
/// <remarks>
/// <para><b>accepted</b>: <see cref="Props"/> are the props to render. The server checked them against the
/// schema and against its own data, so they can differ from the arguments the model streamed
/// (<see cref="Corrections"/> says how). <see cref="Actions"/> are the interactions the server allows.</para>
/// <para><b>rejected</b>: nothing may be rendered from the arguments; <see cref="Errors"/> says why, in the
/// server's words. <see cref="Component"/> is <see langword="null"/> when the call named no component at all.</para>
/// </remarks>
public sealed record ComponentResult
{
    public const string AcceptedStatus = "accepted";
    public const string RejectedStatus = "rejected";

    public required string Status { get; init; }

    public string? Component { get; init; }

    public JsonElement? Props { get; init; }

    public IReadOnlyList<ComponentAction>? Actions { get; init; }

    public IReadOnlyList<string>? Corrections { get; init; }

    public IReadOnlyList<string>? Errors { get; init; }

    /// <summary>The components the server offers, sent with a rejection so the model can choose again.</summary>
    public IReadOnlyList<string>? Available { get; init; }

    [JsonIgnore]
    public bool IsAccepted => Status == AcceptedStatus;

    public static ComponentResult Accepted(string component, JsonElement props, IReadOnlyList<ComponentAction>? actions = null, IReadOnlyList<string>? corrections = null) =>
        new()
        {
            Status = AcceptedStatus,
            Component = component,
            Props = props,
            Actions = actions is { Count: > 0 } ? actions : null,
            Corrections = corrections is { Count: > 0 } ? corrections : null,
        };

    public static ComponentResult Rejected(string? component, IReadOnlyList<string> errors, IReadOnlyList<string>? available = null) =>
        new() { Status = RejectedStatus, Component = component, Errors = errors, Available = available };

    /// <summary>The envelope as JSON, the form a tool returns it in and the wire carries it.</summary>
    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, ComponentJsonContext.Default.ComponentResult);

    /// <summary>
    /// Reads a tool result as an envelope. Results of ordinary tools (a work order, a list of parts)
    /// aren't envelopes and return <see langword="false"/>, which is how a client tells a component call
    /// it doesn't know from a tool call it should render as activity.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out ComponentResult? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(text) || text.TrimStart()[0] != '{')
        {
            return false;
        }

        try
        {
            result = JsonSerializer.Deserialize(text, ComponentJsonContext.Default.ComponentResult);
        }
        catch (JsonException)
        {
            return false;
        }

        return result is { Status: AcceptedStatus or RejectedStatus };
    }
}

/// <summary>An interaction the server allows on a rendered component, such as ordering the part on a card.</summary>
/// <param name="Name">The action's name; the client maps it to what it sends back.</param>
/// <param name="Label">The button text.</param>
/// <param name="Enabled">Whether the action is available now.</param>
/// <param name="Reason">Why it isn't, when it isn't.</param>
public sealed record ComponentAction(string Name, string Label, bool Enabled, string? Reason = null);

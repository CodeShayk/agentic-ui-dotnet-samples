using System.Text.RegularExpressions;

namespace Ch16.Beacon.Agent.Reasoning;

/// <summary>How much of the model's reasoning the agent puts on the wire.</summary>
public enum ReasoningDisclosure
{
    /// <summary>Everything the provider shows, with the <see cref="ReasoningPolicy.Redactions"/> patterns removed.</summary>
    Full,

    /// <summary>A short account: the provider is asked for a summary, and the agent caps what it passes on.</summary>
    Summary,

    /// <summary>No reasoning text at all. Sealed reasoning still travels, so follow-up questions keep their context.</summary>
    Hidden,
}

/// <summary>One pattern the policy removes from reasoning before anyone sees it.</summary>
/// <param name="Name">What the pattern finds, for the server log.</param>
/// <param name="Pattern">The pattern.</param>
/// <param name="Replacement">What replaces each match.</param>
public sealed record Redaction(string Name, Regex Pattern, string Replacement);

/// <summary>
/// The server's decision about reasoning: what a client may show and therefore store. It is configuration
/// (<c>Beacon:Reasoning</c>), never a request field, because everything in <c>RunAgentInput</c> comes from the client.
/// </summary>
public sealed partial class ReasoningPolicy
{
    public const string SectionName = "Beacon:Reasoning";

    public ReasoningDisclosure Disclosure { get; set; } = ReasoningDisclosure.Summary;

    /// <summary>With <see cref="ReasoningDisclosure.Summary"/>, the most characters of one reasoning message that reach the wire.</summary>
    public int MaxShownCharacters { get; set; } = 400;

    /// <summary>
    /// Patterns removed from reasoning text before it leaves the server: phone numbers in the North American
    /// format, email addresses and card-like runs of 13 to 16 digits. Models copy whatever is in their context
    /// into their thinking, but patterns are a safety net, not a guarantee: names, street addresses, other
    /// countries' phone formats and speculation about people pass through. The control for those is
    /// <see cref="Disclosure"/>: <see cref="ReasoningDisclosure.Summary"/> from a provider that summarizes, or
    /// <see cref="ReasoningDisclosure.Hidden"/>.
    /// </summary>
    public IList<Redaction> Redactions { get; } =
    [
        new("phone number", PhoneNumber(), "[phone number removed]"),
        new("email address", EmailAddress(), "[email removed]"),
        new("card number", CardNumber(), "[card number removed]"),
    ];

    [GeneratedRegex(@"(?<![\w-])(?:\(?\d{3}\)?[ .-]?)?\d{3}[ .-]\d{4}(?![\w-])")]
    private static partial Regex PhoneNumber();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+")]
    private static partial Regex EmailAddress();

    [GeneratedRegex(@"\b(?:\d[ -]?){13,16}\b")]
    private static partial Regex CardNumber();
}

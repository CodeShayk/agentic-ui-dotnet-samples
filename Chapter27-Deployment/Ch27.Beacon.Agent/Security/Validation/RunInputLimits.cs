// Copied from Chapter 23 (Ch23.Beacon.Security).
namespace Ch27.Beacon.Agent.Security.Validation;

/// <summary>
/// Bounds on what one <c>RunAgentInput</c> may carry, bound from <c>Beacon:Limits</c>. Every number is a
/// cost decision as much as a security one: each message the client sends is parsed, stored and, if
/// accepted, paid for in tokens.
/// </summary>
public sealed class RunInputLimits
{
    public const string SectionName = "Beacon:Limits";

    /// <summary>
    /// The whole request body, history included, enforced by the server before the JSON is parsed. A client
    /// that reaches it sends only its new messages (the server holds the history) or starts a new thread.
    /// </summary>
    public long MaxRequestBodyBytes { get; set; } = 256 * 1024;

    public int MaxIdLength { get; set; } = 128;

    /// <summary>Messages the server doesn't already hold. Echoes of its own messages don't count.</summary>
    public int MaxNewMessages { get; set; } = 50;

    /// <summary>Characters of text in one new user message.</summary>
    public int MaxUserMessageChars { get; set; } = 4_000;

    /// <summary>Characters in any other new message the client sends (assistant, system, ...), even ones the server ignores.</summary>
    public int MaxOtherMessageChars { get; set; } = 16_000;

    /// <summary>Characters in a new tool result, which only a frontend tool can produce.</summary>
    public int MaxToolResultChars { get; set; } = 4_000;

    public int MaxTools { get; set; } = 8;

    public int MaxToolSchemaChars { get; set; } = 4_000;

    public int MaxContextEntries { get; set; } = 8;

    public int MaxContextChars { get; set; } = 2_000;

    public int MaxStateChars { get; set; } = 2_000;

    public int MaxForwardedPropsChars { get; set; } = 2_000;

    public int MaxResumeEntries { get; set; } = 4;

    public int MaxResumePayloadChars { get; set; } = 4_000;

    /// <summary>Frontend tools a client may declare. Anything else is refused.</summary>
    public string[] AllowedClientTools { get; set; } = ["show_work_order"];
}

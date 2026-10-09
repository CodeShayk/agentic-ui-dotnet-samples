using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Ch14.Beacon.Agent.Approvals;

/// <summary>
/// The run an approval belongs to: the thread, the run and the client that sent it. The handler puts
/// it on the run's <see cref="ChatOptions"/>, so the layers that propose and check approvals inside the
/// chat pipeline know which run they are in.
/// </summary>
/// <param name="ThreadId">The AG-UI thread.</param>
/// <param name="RunId">The AG-UI run.</param>
/// <param name="ClientId">The client's own label for itself. A label, not an identity: Chapter 23 adds the user.</param>
public sealed record ApprovalRun(string ThreadId, string RunId, string ClientId)
{
    /// <summary>The key on <see cref="ChatOptions.AdditionalProperties"/>.</summary>
    public const string Key = "beacon.approval_run";

    /// <summary>The run these options belong to, if the handler set one.</summary>
    public static ApprovalRun? From(ChatOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(Key, out ApprovalRun? run) is true ? run : null;
}

/// <summary>
/// Which client sent a request, from the <c>X-Beacon-Client</c> header every Beacon client sets. The
/// audit records it so a decision can be traced to the Desk tab or device that made it.
/// </summary>
public static partial class BeaconClient
{
    public const string HeaderName = "X-Beacon-Client";

    /// <summary>What the audit records when a request carries no usable label.</summary>
    public const string Unidentified = "unidentified";

    public static string IdOf(HttpRequest request) =>
        request.Headers[HeaderName].ToString() is { Length: > 0 } value && ValidId().IsMatch(value) ? value : Unidentified;

    // The label ends up in logs, so it is held to a short, plain alphabet.
    [GeneratedRegex("^[A-Za-z0-9._:-]{1,64}$")]
    private static partial Regex ValidId();
}

// Adapted from Chapter 14 (Ch14.Beacon.Agent/Approvals/ApprovalRun.cs). Chapter 23 adds the caller.
using System.Text.RegularExpressions;
using Ch23.Beacon.Security.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;

namespace Ch23.Beacon.Security.Approvals;

/// <summary>
/// The run an approval belongs to: the authenticated caller, the thread, the run and the client label.
/// The handler puts it on the run's <see cref="ChatOptions"/>, so the layers that propose and check
/// approvals inside the chat pipeline know whose run they are in.
/// </summary>
/// <param name="ThreadId">The AG-UI thread.</param>
/// <param name="RunId">The AG-UI run.</param>
/// <param name="ClientId">The client's own label (Chapter 14). Useful in an audit; never an identity.</param>
/// <param name="Caller">Who is calling, from the validated token.</param>
public sealed record ApprovalRun(string ThreadId, string RunId, string ClientId, BeaconCaller Caller)
{
    /// <summary>The key on <see cref="ChatOptions.AdditionalProperties"/>.</summary>
    public const string Key = "beacon.approval_run";

    public string UserId => Caller.UserId;

    /// <summary>The run these options belong to, if the handler set one.</summary>
    public static ApprovalRun? From(ChatOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(Key, out ApprovalRun? run) is true ? run : null;
}

/// <summary>Which client sent a request, from the <c>X-Beacon-Client</c> header Beacon's clients set (Chapter 14).</summary>
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

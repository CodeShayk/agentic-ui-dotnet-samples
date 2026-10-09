using AGUI.Abstractions;
using Ch19.Beacon.Adaptive.Versioning;

namespace Ch19.Beacon.Agent.Versioning;

/// <summary>
/// Judges the client's <c>RunAgentInput.protocolVersion</c> before the run starts. A newer minor of
/// the 1.x line is served (additions are safe by construction) with a warning; a declaration that
/// isn't a version is handled the same way; only a major line this agent doesn't implement is refused,
/// with an HTTP error before any event is written.
/// </summary>
public sealed partial class ProtocolVersionGate(ILogger<ProtocolVersionGate> logger)
{
    public IResult? Admit(RunAgentInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var declared = input.ProtocolVersion;
        var ours = ProtocolVersion.Current;

        switch (ProtocolVersions.Judge(declared, ours))
        {
            case VersionVerdict.Same:
                return null;

            case VersionVerdict.Absent:
                // A peer from before the field existed: served, and SubagentDowngradeChatClient leaves out
                // the subagent lifecycle events it couldn't decode.
                LogUndeclared(logger, input.RunId);
                return null;

            case VersionVerdict.OlderMinor:
                LogOlderMinor(logger, declared!, ours.ToString(), input.RunId);
                return null;

            case VersionVerdict.NewerMinor:
            case VersionVerdict.Uninterpretable:
                // MUST serve, answer with our own version on RUN_STARTED, SHOULD warn.
                LogNewer(logger, declared!, ours.ToString(), input.RunId);
                return null;

            default:
                LogRefused(logger, declared!, ours.ToString(), input.RunId);
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Unsupported AG-UI protocol version",
                    detail: $"This agent implements AG-UI {ours.Major}.x (it speaks {ours}); the request declared {declared}.");
        }
    }

    [LoggerMessage(EventId = 1901, Level = LogLevel.Information,
        Message = "Run {RunId}: the client declared no protocol version (a pre-1.0 peer); serving AG-UI 1.0 shapes without the subagent lifecycle.")]
    private static partial void LogUndeclared(ILogger logger, string runId);

    [LoggerMessage(EventId = 1902, Level = LogLevel.Information,
        Message = "Run {RunId}: the client speaks AG-UI {Declared}, older than this agent's {Ours}; serving without a downgrade.")]
    private static partial void LogOlderMinor(ILogger logger, string declared, string ours, string runId);

    [LoggerMessage(EventId = 1903, Level = LogLevel.Warning,
        Message = "Run {RunId}: the client declared AG-UI \"{Declared}\", newer than (or not comparable with) this agent's {Ours}. Serving the run; RUN_STARTED declares this agent's own version.")]
    private static partial void LogNewer(ILogger logger, string declared, string ours, string runId);

    [LoggerMessage(EventId = 1904, Level = LogLevel.Warning,
        Message = "Run {RunId}: refused a client declaring AG-UI {Declared}; this agent implements only the line of {Ours}.")]
    private static partial void LogRefused(ILogger logger, string declared, string ours, string runId);
}

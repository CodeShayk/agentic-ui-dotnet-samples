using Beacon.Client.Core;

namespace Ch08.Beacon.Cli;

/// <summary>How <c>beacon</c> tells a script the way a turn ended. <c>beacon --help</c> lists them.</summary>
public static class ExitCodes
{
    /// <summary>The agent answered.</summary>
    public const int Answered = 0;

    /// <summary>The command line was wrong.</summary>
    public const int Usage = 1;

    /// <summary>The agent reported <c>RUN_ERROR</c>.</summary>
    public const int AgentError = 2;

    /// <summary>The connection failed or dropped, or the agent answered with a 5xx.</summary>
    public const int ConnectionFailed = 3;

    /// <summary>The agent is waiting for input (an interrupt).</summary>
    public const int WaitingForInput = 4;

    /// <summary>The turn or the connect timed out.</summary>
    public const int TimedOut = 5;

    /// <summary>The agent refused the request with a 4xx; sending it again won't help.</summary>
    public const int Refused = 6;

    /// <summary>Ctrl+C stopped the turn.</summary>
    public const int Stopped = 130;

    /// <summary>The exit code for the turn that just ended.</summary>
    /// <param name="view">The conversation after the turn.</param>
    public static int For(ConversationView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.Status switch
        {
            ConversationStatus.Idle or ConversationStatus.Cancelled => Answered,
            ConversationStatus.AwaitingInput => WaitingForInput,
            ConversationStatus.Stopped => Stopped,
            _ => view.Failure?.Kind switch
            {
                FailureKind.AgentError => AgentError,
                FailureKind.TimedOut => TimedOut,
                FailureKind.Rejected => Refused,
                _ => ConnectionFailed,
            },
        };
    }
}

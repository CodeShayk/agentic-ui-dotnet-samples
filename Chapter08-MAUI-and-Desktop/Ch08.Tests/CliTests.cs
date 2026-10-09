using System.Net;
using Beacon.Client.Core;
using Ch08.Beacon.Cli;

namespace Ch08.Tests;

/// <summary>The beacon tool's exit codes: one per way a turn can end, so scripts can branch on them.</summary>
public sealed class CliTests
{
    [Fact]
    public void Each_ending_has_its_own_exit_code()
    {
        Assert.Equal(ExitCodes.Answered, ExitCodes.For(ConversationView.Empty with { Status = ConversationStatus.Idle }));
        Assert.Equal(ExitCodes.WaitingForInput, ExitCodes.For(ConversationView.Empty with { Status = ConversationStatus.AwaitingInput }));
        Assert.Equal(ExitCodes.Stopped, ExitCodes.For(ConversationView.Empty with { Status = ConversationStatus.Stopped }));
        Assert.Equal(ExitCodes.AgentError, ExitCodes.For(Failed(FailureKind.AgentError)));
        Assert.Equal(ExitCodes.TimedOut, ExitCodes.For(Failed(FailureKind.TimedOut)));
        Assert.Equal(ExitCodes.ConnectionFailed, ExitCodes.For(Failed(FailureKind.Unreachable)));
        Assert.Equal(ExitCodes.ConnectionFailed, ExitCodes.For(Failed(FailureKind.StreamEnded)));

        // A 4xx is a refusal, not a network problem: retrying from a script won't help.
        Assert.Equal(6, ExitCodes.For(Failed(FailureKind.Rejected, HttpStatusCode.BadRequest)));
    }

    private static ConversationView Failed(FailureKind kind, HttpStatusCode? status = null) =>
        ConversationView.Empty with { Status = ConversationStatus.Failed, Failure = new RunFailure(kind, "Failed.") { StatusCode = status } };
}

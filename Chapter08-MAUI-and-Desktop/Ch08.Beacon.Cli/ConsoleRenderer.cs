using Beacon.Client.Core;
using Ch08.Field.Core.Connection;
using Ch08.Field.Core.Recovery;

namespace Ch08.Beacon.Cli;

/// <summary>
/// Renders a conversation to a terminal: the answer's text to standard output as it streams (so it can
/// be piped), and tool activity, retries and advice to standard error.
/// </summary>
public sealed class ConsoleRenderer(TextWriter answer, TextWriter status, bool showText = true)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _printed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _activity = new(StringComparer.Ordinal);
    private string? _lastMessageId;

    /// <summary>Prints what changed since the last call. Safe to call from <c>AgentConversation.Changed</c>.</summary>
    public void Render(ConversationView view)
    {
        lock (_gate)
        {
            foreach (var call in view.ToolCalls)
            {
                var line = ToolActivity.Describe(call);
                if (!_activity.TryGetValue(call.Id, out var shown) || shown != line)
                {
                    _activity[call.Id] = line;
                    status.WriteLine($"  [{line}]");
                }
            }

            if (!showText)
            {
                return;
            }

            foreach (var message in view.Messages.OfType<AssistantMessageView>())
            {
                // The Markdown as it arrived: a terminal shows it as text, and unlike a rendering of half
                // a message, raw text only ever grows, so printing the new part is always right.
                var text = message.Text;
                _printed.TryGetValue(message.Id, out var done);
                if (text.Length <= done)
                {
                    continue;
                }

                if (_lastMessageId is not null && _lastMessageId != message.Id && done == 0)
                {
                    answer.WriteLine();
                }

                answer.Write(text[done..]);
                answer.Flush();
                _printed[message.Id] = text.Length;
                _lastMessageId = message.Id;
            }
        }
    }

    /// <summary>Ends the turn's output and says what to do if it didn't finish.</summary>
    public void Finish(ConversationView view, RecoveryAdvice advice)
    {
        lock (_gate)
        {
            if (_lastMessageId is not null)
            {
                answer.WriteLine();
                _lastMessageId = null;
            }

            if (view.Failure is { } failure)
            {
                status.WriteLine($"beacon: {failure.Message}");
            }

            if (advice.Kind != RecoveryKind.None)
            {
                status.WriteLine($"beacon: {advice.Message}");
                if (advice.FollowUp is { } followUp)
                {
                    status.WriteLine($"beacon: next: beacon ask \"{followUp}\"");
                }
            }
        }
    }

    /// <summary>Reports the retry layer's progress.</summary>
    public void Report(ConnectionEvent e)
    {
        lock (_gate)
        {
            switch (e.Stage)
            {
                case ConnectionStage.Retrying:
                    status.WriteLine($"beacon: attempt {e.Attempt} of {e.MaxAttempts} failed before the run started ({e.Error?.Message}); trying again in {e.RetryDelay.GetValueOrDefault().TotalSeconds:0.0} s with the same run ID");
                    break;
                case ConnectionStage.Truncated when e.Error is not null:
                    status.WriteLine($"beacon: the connection dropped after {e.EventsReceived} events of run {e.RunId}; not retrying");
                    break;
            }
        }
    }
}

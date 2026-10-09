using System.Text;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace Ch16.Beacon.Agent.Reasoning;

/// <summary>
/// The per-run state behind <see cref="ReasoningPolicyChatClient"/>. It follows the reasoning message the
/// converter has open (reasoning text opens one; text, a tool call, a result or an interrupt closes it) and
/// rewrites the stream so that only what the policy allows reaches it.
/// </summary>
/// <remarks>
/// Reasoning is released a sentence at a time. A phone number split across two deltas is still one match,
/// and a sentence of latency costs nothing in a panel that starts collapsed.
/// </remarks>
internal sealed class ReasoningShaper(ReasoningPolicy policy)
{
    private readonly StringBuilder _pending = new();
    private ChatResponseUpdate? _source;
    private int _shown;
    private bool _capped;

    /// <summary>Reasoning characters the policy kept off the wire in this run.</summary>
    public int WithheldCharacters { get; private set; }

    /// <summary>The kinds of data that were redacted in this run.</summary>
    public SortedSet<string> Redacted { get; } = new(StringComparer.Ordinal);

    public List<ChatResponseUpdate> Shape(ChatResponseUpdate update)
    {
        // Events injected below (BaseEvent in RawRepresentation) and updates without reasoning pass unchanged.
        if (update.RawRepresentation is BaseEvent || !update.Contents.Any(c => c is TextReasoningContent))
        {
            return Pass(update);
        }

        // Rebuild the update. AGUI.Server serializes each update into the rawEvent of the events it produces,
        // so reasoning left in an update that also carries text would reach the client inside rawEvent.
        List<ChatResponseUpdate> output = [];
        List<AIContent> passing = [];
        foreach (var content in update.Contents)
        {
            if (content is TextReasoningContent reasoning)
            {
                Flush(update, passing, output);
                _source = update;

                if (!string.IsNullOrEmpty(reasoning.Text))
                {
                    _pending.Append(reasoning.Text);
                    Release(output, complete: false);
                }

                if (reasoning.ProtectedData is { } sealedValue)
                {
                    Release(output, complete: true); // the visible part first, so the value has a message to join
                    Seal(sealedValue, output);
                }
            }
            else
            {
                if (ClosesReasoning(content))
                {
                    Flush(update, passing, output);
                    EndReasoning(output);
                }

                passing.Add(content);
            }
        }

        Flush(update, passing, output);
        return output;
    }

    /// <summary>The stream ended: release what the policy allows of the last reasoning.</summary>
    public List<ChatResponseUpdate> Complete()
    {
        List<ChatResponseUpdate> output = [];
        EndReasoning(output);
        return output;
    }

    private List<ChatResponseUpdate> Pass(ChatResponseUpdate update)
    {
        List<ChatResponseUpdate> output = [];
        if (update.Contents.Any(ClosesReasoning))
        {
            EndReasoning(output);
        }

        output.Add(update);
        return output;
    }

    // The contents AGUI.Server closes an open reasoning message for.
    private static bool ClosesReasoning(AIContent content) =>
        content is TextContent or FunctionCallContent or FunctionResultContent or InputRequestContent;

    private void EndReasoning(List<ChatResponseUpdate> output)
    {
        Release(output, complete: true);
        (_shown, _capped) = (0, false);
    }

    private void Release(List<ChatResponseUpdate> output, bool complete)
    {
        while (TakeSentence(complete) is { } sentence)
        {
            var text = Redact(sentence);

            if (policy.Disclosure == ReasoningDisclosure.Hidden || _capped)
            {
                WithheldCharacters += text.Length;
                continue;
            }

            if (policy.Disclosure == ReasoningDisclosure.Summary && _shown + text.Length > policy.MaxShownCharacters)
            {
                _capped = true;
                if (_shown > 0)
                {
                    WithheldCharacters += text.Length;
                    continue;
                }

                // A first sentence longer than the whole budget: show its beginning rather than nothing.
                var cut = Truncate(text, policy.MaxShownCharacters);
                WithheldCharacters += text.Length - cut.Length + 1;
                text = cut;
            }

            _shown += text.Length;
            output.Add(Reasoning(new TextReasoningContent(text)));
        }
    }

    private void Seal(string sealedValue, List<ChatResponseUpdate> output)
    {
        if (_shown > 0)
        {
            // A reasoning message is open on the wire, and the converter attaches the value to it.
            output.Add(Reasoning(new TextReasoningContent(null) { ProtectedData = sealedValue }));
            return;
        }

        // No reasoning text is on the wire: the policy hides it, or the provider shows none. AGUI.Server would
        // name a message it never opens, and a client drops a value for a message it doesn't know. So the value
        // gets an empty reasoning message of its own, written as events.
        var id = $"reasoning_{Guid.NewGuid():N}";
        output.Add(Raw(new ReasoningStartEvent { MessageId = id }));
        output.Add(Raw(new ReasoningMessageStartEvent { MessageId = id, Role = AGUIRoles.Reasoning }));
        output.Add(Raw(new ReasoningEncryptedValueEvent { Subtype = "message", EntityId = id, EncryptedValue = sealedValue }));
        output.Add(Raw(new ReasoningMessageEndEvent { MessageId = id }));
        output.Add(Raw(new ReasoningEndEvent { MessageId = id }));
    }

    // One sentence (with the whitespace after it), or the remainder when the reasoning is complete.
    private string? TakeSentence(bool complete)
    {
        for (var i = 0; i < _pending.Length; i++)
        {
            var c = _pending[i];
            var ends = c == '\n' || (c is '.' or '!' or '?' && i + 1 < _pending.Length && char.IsWhiteSpace(_pending[i + 1]));
            if (ends)
            {
                var end = i + 1;
                while (end < _pending.Length && char.IsWhiteSpace(_pending[end]))
                {
                    end++;
                }

                if (end == _pending.Length && !complete)
                {
                    return Take(i + 1); // keep the trailing whitespace with the next sentence's arrival
                }

                return Take(end);
            }
        }

        return complete && _pending.Length > 0 ? Take(_pending.Length) : null;
    }

    private string Take(int length)
    {
        var text = _pending.ToString(0, length);
        _pending.Remove(0, length);
        return text;
    }

    private string Redact(string text)
    {
        foreach (var redaction in policy.Redactions)
        {
            if (redaction.Pattern.IsMatch(text))
            {
                text = redaction.Pattern.Replace(text, redaction.Replacement);
                Redacted.Add(redaction.Name);
            }
        }

        return text;
    }

    private static string Truncate(string text, int length)
    {
        var space = text.LastIndexOf(' ', Math.Max(0, length - 2));
        return string.Concat(text.AsSpan(0, space > 0 ? space : Math.Max(0, length - 1)), "…");
    }

    private ChatResponseUpdate Reasoning(AIContent content) => new(ChatRole.Assistant, [content])
    {
        ResponseId = _source?.ResponseId,
        MessageId = _source?.MessageId,
        ModelId = _source?.ModelId,
    };

    private static ChatResponseUpdate Raw(BaseEvent evt) => new() { RawRepresentation = evt };

    private static void Flush(ChatResponseUpdate update, List<AIContent> passing, List<ChatResponseUpdate> output)
    {
        if (passing.Count == 0)
        {
            return;
        }

        var rest = update.Clone();
        rest.Contents = [.. passing];
        output.Add(rest);
        passing.Clear();
    }
}

// Copied from Chapter 23 (Ch23.Beacon.Security).
using System.Text.Json;
using System.Text.RegularExpressions;
using AGUI.Abstractions;

namespace Ch27.Beacon.Agent.Security.Validation;

/// <summary>
/// Checks the shape and size of a <see cref="RunAgentInput"/> before anything else touches it. It
/// answers "is this a well-formed Beacon request?", not "may this caller do this?"; that is the job
/// of authorization, the transcript and the tools. Errors are keyed by JSON path for a 400
/// problem-details response.
/// </summary>
public static partial class RunInputValidator
{
    /// <param name="input">The run input as the client wrote it.</param>
    /// <param name="limits">The caps on what a client may add.</param>
    /// <param name="isKnown">
    /// Whether the server already holds a message with this ID. A client resends the whole history every turn,
    /// including the server's own answers and tool results, and the server runs on its own copy of those. So
    /// only new messages are held to the caps, and a long conversation can't lock itself out.
    /// </param>
    public static Dictionary<string, string[]> Validate(RunAgentInput input, RunInputLimits limits, Func<string, bool>? isKnown = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);

        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Error(string path, string message)
        {
            if (!errors.TryGetValue(path, out var list))
            {
                errors[path] = list = [];
            }

            list.Add(message);
        }

        // Identifiers become keys in the server's stores and its logs, so they get a strict format.
        CheckId(input.ThreadId, "threadId", required: true);
        CheckId(input.RunId, "runId", required: true);
        CheckId(input.ParentRunId, "parentRunId", required: false);

        var messages = input.Messages ?? [];
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var newMessages = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var path = $"messages[{i}]";

            // An empty ID would make every message look like the same message to the transcript.
            if (!CheckId(message.Id, $"{path}.id", required: true))
            {
                continue;
            }

            if (!seenIds.Add(message.Id))
            {
                Error($"{path}.id", "Message IDs must be unique within a request.");
                continue;
            }

            // The server's copy is what runs, so an echoed message is only located, never capped again.
            if (isKnown?.Invoke(message.Id) is true)
            {
                continue;
            }

            if (++newMessages > limits.MaxNewMessages)
            {
                Error("messages", $"At most {limits.MaxNewMessages} new messages are accepted.");
                break;
            }

            switch (message)
            {
                case AGUIUserMessage user:
                    // Text only: Chapter 17's media need its type checks, URL guard and a much larger body limit.
                    if (user.Content.Value is IEnumerable<AGUIInputContent> parts && parts.Any(p => p is AGUIMediaInputContent))
                    {
                        Error($"{path}.content", "This endpoint accepts text only; media needs Chapter 17's rules and body limit.");
                    }

                    if (TextLength(user.Content) > limits.MaxUserMessageChars)
                    {
                        Error($"{path}.content", $"A user message may hold at most {limits.MaxUserMessageChars} characters.");
                    }

                    break;

                case AGUIToolMessage tool:
                    CheckId(tool.ToolCallId, $"{path}.toolCallId", required: true);
                    if (TextLength(tool.Content) > limits.MaxToolResultChars)
                    {
                        Error($"{path}.content", $"A tool result may hold at most {limits.MaxToolResultChars} characters.");
                    }

                    break;

                default:
                    // Assistant, system, developer, reasoning and activity messages are never trusted, but they
                    // are still parsed, so they are still bounded.
                    if (OtherLength(message) > limits.MaxOtherMessageChars)
                    {
                        Error($"{path}", $"A {message.Role} message may hold at most {limits.MaxOtherMessageChars} characters.");
                    }

                    break;
            }
        }

        var tools = input.Tools ?? [];
        if (tools.Count > limits.MaxTools)
        {
            Error("tools", $"At most {limits.MaxTools} frontend tools are accepted.");
        }

        for (var i = 0; i < tools.Count; i++)
        {
            if (!limits.AllowedClientTools.Contains(tools[i].Name, StringComparer.Ordinal))
            {
                Error($"tools[{i}].name", "Not one of Beacon's frontend tools.");
            }

            if ((tools[i].Parameters?.GetRawText().Length ?? 0) > limits.MaxToolSchemaChars)
            {
                Error($"tools[{i}].parameters", $"A tool schema may hold at most {limits.MaxToolSchemaChars} characters.");
            }
        }

        var context = input.Context ?? [];
        if (context.Count > limits.MaxContextEntries)
        {
            Error("context", $"At most {limits.MaxContextEntries} context entries are accepted.");
        }

        for (var i = 0; i < context.Count; i++)
        {
            if ((context[i].Description?.Length ?? 0) + (context[i].Value?.Length ?? 0) > limits.MaxContextChars)
            {
                Error($"context[{i}]", $"A context entry may hold at most {limits.MaxContextChars} characters.");
            }
        }

        if (input.ForwardedProperties is { } forwarded && forwarded.GetRawText().Length > limits.MaxForwardedPropsChars)
        {
            Error("forwardedProps", $"Forwarded properties may hold at most {limits.MaxForwardedPropsChars} characters.");
        }

        if (input.State is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } state)
        {
            if (state.GetRawText().Length > limits.MaxStateChars)
            {
                Error("state", $"State may hold at most {limits.MaxStateChars} characters.");
            }
            else
            {
                DeskState.Parse(state, Error);
            }
        }

        var resume = input.Resume ?? [];
        if (resume.Count > limits.MaxResumeEntries)
        {
            Error("resume", $"At most {limits.MaxResumeEntries} resume entries are accepted.");
        }

        for (var i = 0; i < resume.Count; i++)
        {
            CheckId(resume[i].InterruptId, $"resume[{i}].interruptId", required: true);
            if (resume[i].Status is not ("resolved" or "cancelled"))
            {
                Error($"resume[{i}].status", "Status must be 'resolved' or 'cancelled'.");
            }

            if ((resume[i].Payload?.GetRawText().Length ?? 0) > limits.MaxResumePayloadChars)
            {
                Error($"resume[{i}].payload", $"A resume payload may hold at most {limits.MaxResumePayloadChars} characters.");
            }
        }

        return errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal);

        bool CheckId(string? value, string path, bool required)
        {
            if (string.IsNullOrEmpty(value))
            {
                if (required)
                {
                    Error(path, "Required.");
                }

                return false;
            }

            if (value.Length > limits.MaxIdLength || !IdFormat().IsMatch(value))
            {
                Error(path, $"Must be 1 to {limits.MaxIdLength} letters, digits or . _ : - characters.");
                return false;
            }

            return true;
        }
    }

    private static int TextLength(AGUIContent content) => content.Value switch
    {
        string text => text.Length,
        IEnumerable<AGUIInputContent> parts => parts.Sum(p => p is AGUITextInputContent t ? t.Text?.Length ?? 0 : 0),
        _ => 0,
    };

    private static int OtherLength(AGUIMessage message) => message switch
    {
        AGUIAssistantMessage a => (a.Content?.Length ?? 0)
            + (a.ToolCalls?.Sum(c => (c.Function?.Name?.Length ?? 0) + (c.Function?.Arguments?.Length ?? 0)) ?? 0),
        AGUISystemMessage s => s.Content?.Length ?? 0,
        AGUIDeveloperMessage d => d.Content?.Length ?? 0,
        AGUIReasoningMessage r => (r.Content?.Length ?? 0) + (r.EncryptedValue?.Length ?? 0),
        AGUIActivityMessage act => act.Content.ValueKind == JsonValueKind.Undefined ? 0 : act.Content.GetRawText().Length,
        _ => 0,
    };

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:\-]*$")]
    private static partial Regex IdFormat();
}

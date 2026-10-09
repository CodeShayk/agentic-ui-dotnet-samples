using System.Collections.Immutable;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace Beacon.Client.Core;

// MESSAGES_SNAPSHOT: the producer restates the conversation. It reconciles rather than overwrites,
// because the client may hold messages no producer tracks:
//
//   - a restated message replaces the local copy with the same ID, in place;
//   - a message the client hasn't seen is appended, in snapshot order;
//   - a local message absent from the snapshot is dropped, except activity and reasoning messages,
//     unless the snapshot itself carries a message of that role.
public static partial class ConversationReducer
{
    private static ConversationView ReconcileMessages(ConversationView view, IList<AGUIMessage> snapshot)
    {
        var restated = new Dictionary<string, AGUIMessage>(StringComparer.Ordinal);
        foreach (var message in snapshot)
        {
            restated.TryAdd(message.Id, message);
        }

        var coversActivity = snapshot.Any(m => m is AGUIActivityMessage);
        var coversReasoning = snapshot.Any(m => m is AGUIReasoningMessage);

        var messages = ImmutableList.CreateBuilder<MessageView>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        int replaced = 0, changed = 0, dropped = 0, kept = 0;

        // 1. Walk the local transcript: replace in place, keep client-only material, drop the rest.
        foreach (var local in view.Messages)
        {
            if (restated.TryGetValue(local.Id, out var message) && FromSnapshot(view, message, local) is { } replacement)
            {
                messages.Add(replacement);
                placed.Add(local.Id);
                replaced++;
                changed += SameContent(local, replacement) ? 0 : 1;
            }
            else if ((local is ActivityMessageView && !coversActivity) || (local is ReasoningMessageView && !coversReasoning))
            {
                messages.Add(local);
                kept++;
            }
            else
            {
                dropped++;
            }
        }

        // 2. Append what the client hasn't seen, in the snapshot's order.
        var added = 0;
        foreach (var message in snapshot)
        {
            if (placed.Add(message.Id) && FromSnapshot(view, message, local: null) is { } fresh)
            {
                messages.Add(fresh);
                added++;
            }
        }

        // 3. Tool calls follow the messages that carry them.
        var reconciled = messages.ToImmutable();
        return view with
        {
            Messages = reconciled,
            ToolCalls = ReconcileToolCalls(view, reconciled, snapshot),
            LastReconciliation = new ReconciliationSummary(replaced, changed, added, dropped, kept),
        };
    }

    private static MessageView? FromSnapshot(ConversationView view, AGUIMessage message, MessageView? local)
    {
        MessageView? converted = message switch
        {
            AGUIUserMessage user => new UserMessageView(user.Id, user.Content.ToString())
            {
                Name = user.Name,
                Attachments = AttachmentsOf(user),
            },
            AGUIAssistantMessage assistant => new AssistantMessageView(assistant.Id)
            {
                Text = assistant.Content ?? string.Empty,
                Name = assistant.Name,
                ToolCallIds = [.. (assistant.ToolCalls ?? []).Select(c => c.Id)],
                // "Stopped before it finished" is the client's note, not content; it survives a restatement of the same text.
                IsPartial = local is AssistantMessageView { IsPartial: true } partial && partial.Text == (assistant.Content ?? string.Empty),
            },
            AGUIToolMessage tool => new ToolMessageView(tool.Id, tool.ToolCallId, tool.Content.ToString()) { Error = tool.Error },
            AGUIReasoningMessage reasoning => new ReasoningMessageView(reasoning.Id) { Text = reasoning.Content ?? string.Empty },
            AGUIActivityMessage activity => new ActivityMessageView(activity.Id, activity.ActivityType, JsonValues.ToNode(activity.Content) as JsonObject ?? new JsonObject()),
            AGUISystemMessage system => new SystemMessageView(system.Id, system.Content ?? string.Empty, IsDeveloper: false),
            AGUIDeveloperMessage developer => new SystemMessageView(developer.Id, developer.Content ?? string.Empty, IsDeveloper: true),
            _ => null,
        };

        // The snapshot is authoritative for ownership and for encrypted values: a producer that still
        // needs an encrypted value restates it.
        return converted is null ? null : converted with
        {
            RunId = local?.RunId ?? view.CurrentRun?.RunId,
            SubagentRunId = message.SubagentRunId,
            EncryptedValue = message switch
            {
                AGUIAssistantMessage m => m.EncryptedValue,
                AGUIUserMessage m => m.EncryptedValue,
                AGUIToolMessage m => m.EncryptedValue,
                AGUIReasoningMessage m => m.EncryptedValue,
                AGUISystemMessage m => m.EncryptedValue,
                AGUIDeveloperMessage m => m.EncryptedValue,
                _ => null,
            },
        };
    }

    private static ImmutableList<ToolCallView> ReconcileToolCalls(ConversationView view, ImmutableList<MessageView> messages, IList<AGUIMessage> snapshot)
    {
        var results = new Dictionary<string, AGUIToolMessage>(StringComparer.Ordinal);
        foreach (var tool in snapshot.OfType<AGUIToolMessage>())
        {
            results[tool.ToolCallId] = tool;
        }

        var restated = new Dictionary<string, ToolCallView>(StringComparer.Ordinal);
        foreach (var assistant in snapshot.OfType<AGUIAssistantMessage>())
        {
            foreach (var call in assistant.ToolCalls ?? [])
            {
                var existing = view.FindToolCall(call.Id);
                results.TryGetValue(call.Id, out var result);

                restated[call.Id] = new ToolCallView(call.Id, call.Function.Name)
                {
                    ParentMessageId = assistant.Id,
                    RunId = existing?.RunId ?? view.CurrentRun?.RunId,
                    SubagentRunId = assistant.SubagentRunId,
                    Executor = existing?.Executor ?? ToolExecutor.Unknown,
                    ArgumentsText = call.Function.Arguments ?? string.Empty,
                    Arguments = JsonValues.TryParseObject(call.Function.Arguments),
                    Status = result is not null ? ToolCallStatus.Completed
                        : existing is { Status: not ToolCallStatus.Completed } inFlight ? inFlight.Status
                        : ToolCallStatus.AwaitingClient,
                    Result = result?.Content.ToString(),
                    Error = result?.Error,
                    EncryptedValue = call.EncryptedValue,
                };
            }
        }

        // Calls are listed in transcript order; a call whose message was dropped goes with it.
        var calls = ImmutableList.CreateBuilder<ToolCallView>();
        foreach (var assistant in messages.OfType<AssistantMessageView>())
        {
            foreach (var id in assistant.ToolCallIds)
            {
                if ((restated.GetValueOrDefault(id) ?? view.FindToolCall(id)) is { } call)
                {
                    calls.Add(call);
                }
            }
        }

        // Results for calls the client never saw stay as long as their tool message does.
        foreach (var orphan in view.ToolCalls.Where(c => c.ParentMessageId is null))
        {
            if (messages.Any(m => m is ToolMessageView t && t.ToolCallId == orphan.Id))
            {
                calls.Add(orphan);
            }
        }

        return calls.ToImmutable();
    }

    private static ImmutableList<AIContent> AttachmentsOf(AGUIUserMessage user)
    {
        if (user.Content.IsText)
        {
            return [];
        }

        // The SDK's own conversion turns media parts into DataContent and UriContent.
        return [.. new AGUIMessage[] { user }.AsChatMessages().SelectMany(m => m.Contents).Where(c => c is not TextContent)];
    }

    private static bool SameContent(MessageView local, MessageView restated) => (local, restated) switch
    {
        (UserMessageView a, UserMessageView b) => a.Text == b.Text,
        (AssistantMessageView a, AssistantMessageView b) => a.Text == b.Text && a.ToolCallIds.SequenceEqual(b.ToolCallIds),
        (ToolMessageView a, ToolMessageView b) => a.ToolCallId == b.ToolCallId && SameJsonOrText(a.Content, b.Content),
        (ReasoningMessageView a, ReasoningMessageView b) => a.Text == b.Text,
        (ActivityMessageView a, ActivityMessageView b) => a.ActivityType == b.ActivityType && JsonNode.DeepEquals(a.Content, b.Content),
        (SystemMessageView a, SystemMessageView b) => a.Text == b.Text && a.IsDeveloper == b.IsDeveloper,
        _ => false,
    };

    // Tool results are usually JSON, and two producers may format the same value differently.
    private static bool SameJsonOrText(string a, string b)
    {
        if (a == b)
        {
            return true;
        }

        var left = JsonValues.TryParse(a);
        var right = JsonValues.TryParse(b);
        return left is not null && right is not null && JsonNode.DeepEquals(left, right);
    }
}

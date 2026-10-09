using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Ch26.Beacon.Agent.Approvals;

/// <summary>An approval the server asked a dispatcher for: one exact call, on one thread.</summary>
public sealed record PendingApproval(string Id, string ThreadId, string ToolCallId, string ToolName, string Arguments, decimal Amount);

/// <summary>
/// The server's record of the approvals it requested, a compact version of Chapter 14's approval store.
/// A resume entry can carry any tool call (AGUI.Server 1.0.0 turns it into an approved request and
/// response), so the tool runs only when this ledger holds a matching request, and each request is
/// spent once. Chapter 23 adds the user's identity to the binding; a thread ID alone is not identity.
/// </summary>
public sealed class ApprovalLedger
{
    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new(StringComparer.Ordinal);

    /// <summary>Records a request for <paramref name="call"/> and returns it with a server-issued ID.</summary>
    public PendingApproval Propose(string threadId, FunctionCallContent call, decimal amount)
    {
        var approval = new PendingApproval($"apr_{Guid.NewGuid():N}", threadId, call.CallId, call.Name, Canonical(call.Arguments), amount);
        _pending[approval.Id] = approval;
        return approval;
    }

    /// <summary>Spends the approval for exactly this call on this thread, if the server asked for one.</summary>
    public bool TryConsume(string threadId, FunctionCallContent call)
    {
        var arguments = Canonical(call.Arguments);
        foreach (var (id, approval) in _pending)
        {
            if (approval.ThreadId == threadId && approval.ToolCallId == call.CallId && approval.ToolName == call.Name
                && approval.Arguments == arguments && _pending.TryRemove(id, out _))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The approvals still waiting for a decision.</summary>
    public IReadOnlyCollection<PendingApproval> Pending => [.. _pending.Values];

    // The same arguments in any property order, and whatever .NET type carries them, compare equal.
    private static string Canonical(IDictionary<string, object?>? arguments)
    {
        var node = JsonSerializer.SerializeToNode(
            arguments ?? new Dictionary<string, object?>(),
            AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(IDictionary<string, object?>)));
        return Sorted(node)?.ToJsonString() ?? "null";
    }

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value)))),
        JsonArray array => new JsonArray([.. array.Select(Sorted)]),
        _ => node?.DeepClone(),
    };
}

namespace Ch03.StreamValidator;

/// <summary>The three families that stream long values.</summary>
internal enum ItemKind
{
    Text,
    ToolCall,
    Reasoning,
}

/// <summary>
/// Chunk expansion: turns TEXT_MESSAGE_CHUNK, TOOL_CALL_CHUNK and REASONING_MESSAGE_CHUNK into the
/// start, content and end events they stand for, so the ordering rules only ever see the triad.
/// A chunk stream has no end event of its own; it ends when something else arrives in its lane
/// (the parent agent, or one subagent invocation), and the end event is synthesized then.
/// </summary>
internal sealed class ChunkExpander(Func<ItemKind, string, string?> ownerOfOpenItem)
{
    private sealed record ChunkStream(ItemKind Kind, string Id, string? Owner)
    {
        public string Role { get; init; } = "assistant";

        public string? Name { get; init; }

        public string? ToolCallName { get; init; }

        public string? ParentMessageId { get; init; }
    }

    // At most one open chunk stream per lane; insertion order makes "close everything" deterministic.
    private readonly List<ChunkStream> _streams = [];

    public IEnumerable<WireEvent> Expand(WireEvent evt, DiagnosticSink sink)
    {
        var output = new List<WireEvent>();
        switch (evt.Type)
        {
            case "TEXT_MESSAGE_CHUNK":
                ExpandChunk(ItemKind.Text, evt, evt.MessageId, sink, output);
                return output;
            case "TOOL_CALL_CHUNK":
                ExpandChunk(ItemKind.ToolCall, evt, evt.ToolCallId, sink, output);
                return output;
            case "REASONING_MESSAGE_CHUNK":
                ExpandChunk(ItemKind.Reasoning, evt, evt.MessageId, sink, output);
                return output;

            // Run-level events close every lane.
            case "RUN_STARTED" or "RUN_FINISHED" or "RUN_ERROR" or "MESSAGES_SNAPSHOT":
                foreach (var stream in _streams.ToList())
                {
                    Close(stream, evt.Index, output);
                }

                break;

            // A subagent's terminal event closes that subagent's lane.
            case "SUBAGENT_FINISHED" or "SUBAGENT_ERROR":
                if (Lane(evt.SubagentRunId) is { } subagentStream)
                {
                    Close(subagentStream, evt.Index, output);
                }

                break;

            // These stand aside from assembly and close nothing.
            case "RAW" or "ACTIVITY_SNAPSHOT" or "ACTIVITY_DELTA" or "REASONING_ENCRYPTED_VALUE" or "SUBAGENT_STARTED":
                break;

            // Message, tool-call, step, state, custom and reasoning events end the stream in their lane.
            default:
                CloseLaneFor(evt, sink, output);
                break;
        }

        output.Add(evt);
        return output;
    }

    private void CloseLaneFor(WireEvent evt, DiagnosticSink sink, List<WireEvent> output)
    {
        // An explicit start, content or end naming an item a chunk stream is assembling does not
        // continue it: it ends the shorthand, and mixing the two forms in one item is malformed.
        var (kind, id) = evt.Type switch
        {
            "TEXT_MESSAGE_START" or "TEXT_MESSAGE_CONTENT" or "TEXT_MESSAGE_END" => (ItemKind.Text, evt.MessageId),
            "TOOL_CALL_START" or "TOOL_CALL_ARGS" or "TOOL_CALL_END" => (ItemKind.ToolCall, evt.ToolCallId),
            "REASONING_MESSAGE_START" or "REASONING_MESSAGE_CONTENT" or "REASONING_MESSAGE_END" => (ItemKind.Reasoning, evt.MessageId),
            _ => (ItemKind.Text, null),
        };

        if (id is not null && _streams.FirstOrDefault(s => s.Kind == kind && s.Id == id) is { } assembling)
        {
            sink.Report(Rules.FormsMixed,
                $"'{id}' is being assembled from chunks; an explicit {evt.Type} for it mixes the two forms in one item.");
            Close(assembling, evt.Index, output);
            return;
        }

        // Otherwise the event belongs to its tagged owner, to the owner of the item it continues, or
        // to the parent agent.
        var owner = evt.Owner ?? (id is null ? null : ownerOfOpenItem(kind, id));
        if (Lane(owner) is { } open)
        {
            Close(open, evt.Index, output);
        }
    }

    private void ExpandChunk(ItemKind kind, WireEvent chunk, string? id, DiagnosticSink sink, List<WireEvent> output)
    {
        var target = FindStreamToContinue(kind, chunk, id, sink, out var ambiguous);
        if (ambiguous)
        {
            return;
        }

        if (target is not null)
        {
            Continue(target, chunk, sink, output);
            return;
        }

        // Not a continuation, so this chunk opens an item and must carry what opening one requires.
        if (id is null)
        {
            var field = kind == ItemKind.ToolCall ? "toolCallId" : "messageId";
            sink.Report(Rules.FirstChunkIncomplete, $"The first {chunk.Type} of an item must carry {field}; there is no open stream to continue.");
            return;
        }

        if (kind == ItemKind.ToolCall && chunk.ToolCallName is null)
        {
            sink.Report(Rules.FirstChunkIncomplete, $"The first TOOL_CALL_CHUNK for '{id}' must carry toolCallName.");
            return;
        }

        // A chunk naming a different item closes whatever its lane was assembling.
        if (Lane(chunk.Owner) is { } previous)
        {
            Close(previous, chunk.Index, output);
        }

        var stream = new ChunkStream(kind, id, chunk.Owner)
        {
            Role = kind == ItemKind.Reasoning ? "reasoning" : chunk.Role ?? "assistant",
            Name = chunk.Name,
            ToolCallName = chunk.ToolCallName,
            ParentMessageId = chunk.ParentMessageId,
        };
        _streams.Add(stream);

        output.Add(Synthesize(stream, chunk.Index, StartType(kind)) with
        {
            Role = kind == ItemKind.ToolCall ? null : stream.Role,
            Name = stream.Name,
            ToolCallName = stream.ToolCallName,
            ParentMessageId = stream.ParentMessageId,
        });

        if (chunk.Delta is not null)
        {
            output.Add(Synthesize(stream, chunk.Index, ContentType(kind)) with { Delta = chunk.Delta });
        }
    }

    private ChunkStream? FindStreamToContinue(ItemKind kind, WireEvent chunk, string? id, DiagnosticSink sink, out bool ambiguous)
    {
        ambiguous = false;

        if (id is not null)
        {
            var named = _streams.FirstOrDefault(s => s.Kind == kind && s.Id == id);
            if (named is not null && chunk.Owner is not null && chunk.Owner != named.Owner)
            {
                sink.Report(Rules.OwnerMismatch,
                    $"The chunk is attributed to '{chunk.Owner}', but '{id}' was opened by {OwnerName(named.Owner)}.");
            }

            return named;
        }

        if (chunk.Owner is not null)
        {
            return Lane(chunk.Owner) is { } tagged && tagged.Kind == kind ? tagged : null;
        }

        // Neither an id nor a tag: the parent agent's open stream of this kind, else the only one.
        if (Lane(null) is { } parent && parent.Kind == kind)
        {
            return parent;
        }

        var candidates = _streams.Where(s => s.Owner is not null && s.Kind == kind).ToList();
        if (candidates.Count > 1)
        {
            sink.Report(Rules.ChunkAmbiguous,
                $"{candidates.Count} subagents have open {kind} chunk streams; a producer running parallel subagents must attribute its continuations.");
            ambiguous = true;
            return null;
        }

        return candidates.SingleOrDefault();
    }

    private static void Continue(ChunkStream stream, WireEvent chunk, DiagnosticSink sink, List<WireEvent> output)
    {
        // A continuation may repeat what its opener established, but only with the same value.
        if (stream.Kind == ItemKind.Text && chunk.Role is { } role && role != stream.Role)
        {
            sink.Report(Rules.ChunkContradicts, $"role '{role}' does not match the open stream's role '{stream.Role}'.", "/role");
        }

        if (stream.Kind == ItemKind.Text && chunk.Name is { } name && stream.Name is { } established && name != established)
        {
            sink.Report(Rules.ChunkContradicts, $"name '{name}' does not match the open stream's name '{established}'.", "/name");
        }

        if (stream.Kind == ItemKind.ToolCall && chunk.ToolCallName is { } toolName && toolName != stream.ToolCallName)
        {
            sink.Report(Rules.ChunkContradicts,
                $"toolCallName '{toolName}' does not match the open stream's toolCallName '{stream.ToolCallName}'.", "/toolCallName");
        }

        if (stream.Kind == ItemKind.ToolCall && chunk.ParentMessageId is { } parent && stream.ParentMessageId is { } opener && parent != opener)
        {
            sink.Report(Rules.ChunkContradicts,
                $"parentMessageId '{parent}' does not match the open stream's parentMessageId '{opener}'.", "/parentMessageId");
        }

        // A continuation carrying only metadata is legal: it adds no content.
        if (chunk.Delta is not null)
        {
            output.Add(Synthesize(stream, chunk.Index, ContentType(stream.Kind)) with { Delta = chunk.Delta });
        }
    }

    private void Close(ChunkStream stream, int index, List<WireEvent> output)
    {
        _streams.Remove(stream);
        output.Add(Synthesize(stream, index, EndType(stream.Kind)));
    }

    private ChunkStream? Lane(string? owner) => _streams.FirstOrDefault(s => s.Owner == owner);

    private static WireEvent Synthesize(ChunkStream stream, int index, string type) =>
        new(index, type, default)
        {
            MessageId = stream.Kind == ItemKind.ToolCall ? null : stream.Id,
            ToolCallId = stream.Kind == ItemKind.ToolCall ? stream.Id : null,
            Owner = stream.Owner,
            Synthesized = true,
        };

    private static string StartType(ItemKind kind) => kind switch
    {
        ItemKind.Text => "TEXT_MESSAGE_START",
        ItemKind.ToolCall => "TOOL_CALL_START",
        _ => "REASONING_MESSAGE_START",
    };

    private static string ContentType(ItemKind kind) => kind switch
    {
        ItemKind.Text => "TEXT_MESSAGE_CONTENT",
        ItemKind.ToolCall => "TOOL_CALL_ARGS",
        _ => "REASONING_MESSAGE_CONTENT",
    };

    private static string EndType(ItemKind kind) => kind switch
    {
        ItemKind.Text => "TEXT_MESSAGE_END",
        ItemKind.ToolCall => "TOOL_CALL_END",
        _ => "REASONING_MESSAGE_END",
    };

    private static string OwnerName(string? owner) => owner is null ? "the parent agent" : $"'{owner}'";
}

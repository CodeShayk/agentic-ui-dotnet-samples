using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Server;
using Beacon.Domain;
using Ch12.Beacon.Agent.Sheet;
using Microsoft.Extensions.AI;

namespace Ch12.Beacon.Agent;

/// <summary>
/// Makes the work-order sheet a shared document for one run. It reads the state the client sent,
/// repairs what the server owns, shows the sheet and the dispatcher's context to the model as labeled
/// data, gives the model the tools that change the sheet and, when the run patched it, settles the run
/// with a snapshot.
/// </summary>
/// <remarks>
/// Place it above <c>UseFunctionInvocation()</c>: its tools must be on the options the function
/// invoker sees, and its settling snapshot must come after the invoker's last update. State events
/// go to the client through <see cref="ChatResponseUpdate.RawRepresentation"/>, which
/// <c>AsAGUIEventStreamAsync</c> emits verbatim.
/// </remarks>
internal sealed partial class SharedSheetChatClient(IChatClient innerClient, IFieldServiceStore store, TimeSpan toolDelay, ILogger logger)
    : DelegatingChatClient(innerClient)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options ??= new ChatOptions();

        // 1. The state this run starts from. The endpoint already rejected state that doesn't fit the
        //    schema, so this only reads it. No state means the empty sheet.
        options.TryGetRunAgentState(SheetJsonContext.Default.SheetState, out var sent);

        // 2. Repair what the server owns. If anything changed, the client hears it before anything else,
        //    because every delta this run sends is computed against the repaired sheet.
        var start = WorkOrderSheets.Reconcile(sent ?? new SheetState(), store, out var repaired);
        if (sent is not null && repaired)
        {
            LogRepaired(logger, start.WorkOrder?.Id ?? "(none)");
            yield return StateEvent(new StateSnapshotEvent { Snapshot = SheetJson.ToElement(start) });
        }

        // 3. Ground the model and give it the tools that change the sheet. The instructions get only the
        //    server's own rules; the sheet and the context came from the client, so they go in as data.
        var session = new SheetSession(start, store);
        options.TryGetRunAgentInput(out var input);
        var runOptions = options.Clone();
        runOptions.Instructions = string.Join("\n\n", options.Instructions, SheetPrompt.Rules);
        runOptions.Tools = [.. options.Tools ?? [], .. new SheetTools(session).Create(toolDelay)];
        var grounded = SheetPrompt.WithData(messages, start, input?.Context);

        await foreach (var update in base.GetStreamingResponseAsync(grounded, runOptions, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }

        // 4. Settle. The dispatcher can edit while the run works, so a delta may have failed on her copy;
        //    the server can't know. A snapshot of the sheet as the agent left it gives her client a baseline.
        if (session.NeedsSettling)
        {
            yield return StateEvent(new StateSnapshotEvent { Snapshot = SheetJson.ToElement(session.Current) });
        }
    }

    private static ChatResponseUpdate StateEvent(BaseEvent evt) => new() { RawRepresentation = evt };

    [LoggerMessage(EventId = 1201, Level = LogLevel.Information,
        Message = "Repaired the shared sheet for {WorkOrderId}: the client's copy disagreed with the work-order system or used an older schema.")]
    private static partial void LogRepaired(ILogger logger, string workOrderId);
}

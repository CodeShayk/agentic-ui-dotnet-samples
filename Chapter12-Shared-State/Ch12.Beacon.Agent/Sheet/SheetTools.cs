using System.ComponentModel;
using System.Text.Json;
using AGUI.Server;
using Microsoft.Extensions.AI;

namespace Ch12.Beacon.Agent.Sheet;

/// <summary>
/// The tools that change the sheet. Each returns a <see cref="JsonElement"/> that is both the tool
/// result the model reads and the state event the client applies: a whole sheet for
/// <c>open_work_order_sheet</c>, a JSON Patch for the others. <see cref="MapToState"/> tells the stream
/// converter which is which.
/// </summary>
internal sealed class SheetTools(SheetSession session)
{
    public const string OpenSheetName = "open_work_order_sheet";
    public const string SetChecklistItemName = "set_checklist_item";
    public const string AddChecklistItemName = "add_checklist_item";
    public const string AddNoteName = "add_work_order_note";

    // AIJsonUtilities.DefaultOptions writes indented JSON, and AIFunctionFactory re-serializes a JsonElement
    // result with the options it's given, so without these TOOL_CALL_RESULT would carry pretty-printed JSON.
    private static readonly JsonSerializerOptions CompactJson = new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };

    /// <summary>Names a client may not use for its own tools.</summary>
    public static IReadOnlySet<string> Names { get; } =
        new HashSet<string>(StringComparer.Ordinal) { OpenSheetName, SetChecklistItemName, AddChecklistItemName, AddNoteName };

    /// <summary>
    /// Registers how each tool's result reaches the client. The converter emits the mapped event right
    /// after the tool's <c>TOOL_CALL_RESULT</c>; the result must be a <see cref="JsonElement"/>, or the
    /// run ends with <c>RUN_ERROR</c>.
    /// </summary>
    public static AGUIStreamOptions MapToState(AGUIStreamOptions options) => options
        .MapResultAsStateSnapshot(OpenSheetName)
        .MapResultAsStateDelta(SetChecklistItemName)
        .MapResultAsStateDelta(AddChecklistItemName)
        .MapResultAsStateDelta(AddNoteName);

    /// <summary>The tools for one run, optionally slowed down so a run in progress is visible in the Desk.</summary>
    public IList<AITool> Create(TimeSpan delay)
    {
        AIFunction[] tools =
        [
            AIFunctionFactory.Create(OpenSheet, OpenSheetName,
                "Opens a work order on the sheet the dispatcher sees and returns the whole sheet. An unknown ID leaves the sheet unchanged.", CompactJson),
            AIFunctionFactory.Create(SetChecklistItem, SetChecklistItemName,
                "Ticks or clears one checklist item on the open work order. Returns the JSON Patch applied to the sheet; an empty array means nothing changed.", CompactJson),
            AIFunctionFactory.Create(AddChecklistItem, AddChecklistItemName,
                "Adds an item to the open work order's checklist. Returns the JSON Patch applied to the sheet.", CompactJson),
            AIFunctionFactory.Create(AddNote, AddNoteName,
                "Adds a note to the open work order. Returns the JSON Patch applied to the sheet.", CompactJson),
        ];

        return [.. tools.Select(tool => delay > TimeSpan.Zero ? new DelayedTool(tool, delay) : tool)];
    }

    private JsonElement OpenSheet(
        [Description("The work order ID, for example WO-1001.")] string workOrderId) =>
        SheetJson.ToElement(session.Open(workOrderId));

    private JsonElement SetChecklistItem(
        [Description("The checklist item's ID from the sheet, for example C3.")] string itemId,
        [Description("True to tick the item, false to clear it.")] bool done) =>
        SheetJson.ToElement(session.SetItemDone(itemId, done));

    private JsonElement AddChecklistItem(
        [Description("What needs doing, in a few words.")] string text) =>
        SheetJson.ToElement(session.AddItem(text));

    private JsonElement AddNote(
        [Description("The note, one or two sentences.")] string text) =>
        SheetJson.ToElement(session.AddNote(text));
}

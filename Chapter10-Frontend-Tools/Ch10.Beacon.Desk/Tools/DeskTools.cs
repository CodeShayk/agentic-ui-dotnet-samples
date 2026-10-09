using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Beacon.Domain;
using Ch10.Beacon.Desk.Screen;
using Microsoft.Extensions.AI;

namespace Ch10.Beacon.Desk.Tools;

/// <summary>What <c>open_work_order</c> reports back: whether the work order is now on the dispatcher's screen.</summary>
public sealed record OpenWorkOrderResult(string Status, string? WorkOrderId = null);

/// <summary>What <c>highlight_schedule_slot</c> reports back.</summary>
public sealed record HighlightSlotResult(string Status, string? TechnicianId = null, DateTimeOffset? Start = null);

/// <summary>What <c>prefill_part_order</c> reports back, including when the dispatcher said no.</summary>
public sealed record PrefillPartOrderResult(string Status, string? WorkOrderId = null, string? Sku = null, int? Quantity = null);

/// <summary>Source-generated serialization for the Desk tools' results.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OpenWorkOrderResult))]
[JsonSerializable(typeof(HighlightSlotResult))]
[JsonSerializable(typeof(PrefillPartOrderResult))]
public sealed partial class DeskToolsJsonContext : JsonSerializerContext;

/// <summary>
/// Beacon Desk's frontend tools: things only the dispatcher's screen can do. The agent calls them,
/// <c>AGUIChatClient</c> runs them here, and their results go back to the agent in the next run.
/// </summary>
/// <remarks>
/// Arguments are model output: each tool checks them against the Desk's own data before acting. A tool
/// that changes something the dispatcher is working on asks her first, inside the tool, and reports a
/// refusal as a result rather than an error: "she said no" is an answer the agent can work with.
/// </remarks>
public sealed partial class DeskTools(DeskScreen screen, IFieldServiceStore store)
{
    public const string OpenWorkOrderName = "open_work_order";
    public const string HighlightScheduleSlotName = "highlight_schedule_slot";
    public const string PrefillPartOrderName = "prefill_part_order";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    /// <summary>The tools, as <see cref="AIFunction"/>s for <c>ConversationOptions.Tools</c>.</summary>
    public IList<AITool> Create() =>
    [
        AIFunctionFactory.Create(OpenWorkOrder, OpenWorkOrderName,
            "Opens a work order in the dispatcher's work area so she can see it. Use it when she asks to see, open or pull up a work order.",
            JsonOptions),
        AIFunctionFactory.Create(HighlightScheduleSlot, HighlightScheduleSlotName,
            "Highlights one slot on today's schedule in the dispatcher's work area.",
            JsonOptions),
        AIFunctionFactory.Create(PrefillPartOrderAsync, PrefillPartOrderName,
            "Fills in the dispatcher's part order form for her to review. It doesn't order anything; " +
            "the dispatcher confirms the change first and saves the draft herself.",
            JsonOptions),
    ];

    public OpenWorkOrderResult OpenWorkOrder(
        [Description("The work order ID, for example WO-1001.")] string workOrderId)
    {
        if (!WorkOrderId().IsMatch(workOrderId) || store.GetWorkOrder(workOrderId) is null)
        {
            return new OpenWorkOrderResult("not-found");
        }

        screen.OpenWorkOrder(workOrderId, byAgent: true);
        return new OpenWorkOrderResult("opened", workOrderId);
    }

    public HighlightSlotResult HighlightScheduleSlot(
        [Description("The technician ID, for example TECH-1.")] string technicianId,
        [Description("When the slot starts, as an ISO 8601 date and time with offset.")] DateTimeOffset start)
    {
        var slot = store.GetSchedule(SeedData.Today, technicianId).FirstOrDefault(s => s.Start == start);
        if (slot is null)
        {
            return new HighlightSlotResult("not-found");
        }

        screen.Highlight(new SlotRef(slot.TechnicianId, slot.Start), byAgent: true);
        return new HighlightSlotResult("highlighted", slot.TechnicianId, slot.Start);
    }

    public async Task<PrefillPartOrderResult> PrefillPartOrderAsync(
        [Description("The work order the parts are for, for example WO-1001.")] string workOrderId,
        [Description("The part SKU, for example CAP-45-5.")] string sku,
        [Description("How many to order, from 1 to 10.")] int quantity,
        CancellationToken cancellationToken)
    {
        // 1. Check the arguments before bothering the dispatcher with them.
        if (store.GetWorkOrder(workOrderId) is null || store.GetPart(sku) is not { } part || quantity is < 1 or > 10)
        {
            return new PrefillPartOrderResult("not-found");
        }

        // 2. Ask, showing exactly what will change. Stop cancels the question along with the turn.
        var draft = screen.State.Draft;
        var answer = await screen.ConfirmAsync(new ConfirmationRequest(
            Id: $"confirm_{Guid.NewGuid():N}",
            Title: "Beacon wants to fill in the part order form",
            Details:
            [
                $"Work order: {workOrderId}",
                $"Part: {part.Sku}, {part.Name}",
                $"Quantity: {quantity.ToString(CultureInfo.InvariantCulture)} at {part.UnitPrice.ToString("C", CultureInfo.GetCultureInfo("en-US"))} each",
                draft.IsEmpty ? "The form is empty." : $"This replaces your current draft ({draft.Quantity} x {draft.Sku} for {draft.WorkOrderId}).",
            ],
            ConfirmLabel: "Fill in the form",
            DeclineLabel: "Not now"), cancellationToken);

        // 3. Act only on a yes, and say what happened either way.
        switch (answer)
        {
            case ConfirmationAnswer.Confirmed:
                screen.Prefill(new PartOrderDraft(workOrderId, part.Sku, quantity));
                return new PrefillPartOrderResult("prefilled", workOrderId, part.Sku, quantity);
            case ConfirmationAnswer.Declined:
                return new PrefillPartOrderResult("declined");
            default:
                return new PrefillPartOrderResult("no-answer");
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        // Microsoft.Extensions.AI's defaults indent JSON; a result goes over the wire, so keep it compact.
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions) { WriteIndented = false };
        options.TypeInfoResolverChain.Insert(0, DeskToolsJsonContext.Default);
        options.MakeReadOnly();
        return options;
    }

    [GeneratedRegex(@"^WO-\d{4}$")]
    private static partial Regex WorkOrderId();
}

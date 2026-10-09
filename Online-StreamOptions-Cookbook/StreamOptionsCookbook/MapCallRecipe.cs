using System.ComponentModel;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using AGUI.Server;
using Microsoft.Extensions.AI;

namespace StreamOptionsCookbook;

/// <summary>What the part-order tool reports once it has placed the order.</summary>
public sealed record PartOrderPlaced(string OrderId, string WorkOrderId, string Sku, int Quantity, string Status);

/// <summary>Recipe: <see cref="AGUIStreamOptions.MapCall"/> shows what a tool is about to do before it runs.</summary>
public static class MapCallRecipe
{
    public const string PlacePartOrderName = "place_part_order";

    public static AIFunction PlacePartOrder { get; } = AIFunctionFactory.Create(
        ([Description("The work order, for example WO-1001.")] string workOrderId,
         [Description("The part's SKU, for example CAP-45-5.")] string sku,
         [Description("How many to order.")] int quantity) =>
            new PartOrderPlaced("PO-7001", workOrderId, sku, quantity, "placed"),
        PlacePartOrderName,
        "Orders parts for a work order.",
        CookbookJson.ToolOptions);

    public static AGUIStreamOptions Apply(AGUIStreamOptions options) => options
        .MapCall(PlacePartOrderName, call =>
        [
            // Predictive state: the order as the model wrote it, sent after TOOL_CALL_END and before the
            // tool runs. It is display only; the tool validates the same arguments when it runs.
            new StateDeltaEvent
            {
                Delta = CookbookJson.ToElement(new JsonArray(new JsonObject
                {
                    ["op"] = "add",
                    ["path"] = "/pendingOrder",
                    ["value"] = new JsonObject
                    {
                        ["sku"] = CookbookJson.Argument(call, "sku"),
                        ["quantity"] = CookbookJson.Argument(call, "quantity"),
                        ["status"] = "placing",
                    },
                })),
            },
        ]);
}

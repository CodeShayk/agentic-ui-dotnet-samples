using System.Globalization;
using Ch11.Beacon.Components;

namespace Ch11.Beacon.ComponentViews;

/// <summary>
/// The part card, ready to draw: text already formatted, so Blazor and .NET MAUI templates only lay it
/// out. Fields that haven't streamed yet are <see langword="null"/>, and a template shows a placeholder.
/// </summary>
public sealed record PartCardView : IComponentView
{
    private static readonly CultureInfo Money = CultureInfo.GetCultureInfo("en-US");

    public string? Sku { get; init; }

    public string? Name { get; init; }

    /// <summary>The unit price, formatted: <c>$38.50</c>.</summary>
    public string? Price { get; init; }

    /// <summary>Stock and location: <c>24 in stock · Portsmouth</c>, or <c>Out of stock</c>.</summary>
    public string? Stock { get; init; }

    public bool IsOutOfStock { get; init; }

    public string? WorkOrderId { get; init; }

    public string? Note { get; init; }

    public string Summary =>
        $"Part card: {Sku ?? "unknown part"}" +
        (Name is null ? string.Empty : $", {Name}") +
        (Price is null ? string.Empty : $", {Price}") +
        (Stock is null ? string.Empty : $", {Stock.Replace(" · ", " at ", StringComparison.Ordinal)}") +
        (WorkOrderId is null ? "." : $", for {WorkOrderId}.");

    public static PartCardView From(PartCardProps props) => new()
    {
        Sku = props.Sku,
        Name = props.Name,
        Price = props.UnitPrice is { } price ? price.ToString("C", Money) : null,
        Stock = props.InStock switch
        {
            null => null,
            0 => "Out of stock",
            var count => props.Warehouse is { } warehouse ? $"{count} in stock · {warehouse}" : $"{count} in stock",
        },
        IsOutOfStock = props.InStock == 0,
        WorkOrderId = props.WorkOrderId,
        Note = props.Note,
    };
}

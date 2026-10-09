using System.Globalization;
using Ch11.Beacon.Components;

namespace Ch11.Beacon.ComponentViews;

/// <summary>
/// The fault history chart, laid out as a dot plot: one row per fault code, time along the x axis.
/// Positions are fractions of the plot (0 to 1), so the SVG in Beacon Desk and the drawable in Beacon
/// Field place every dot and tick the same way.
/// </summary>
/// <remarks>
/// A dot plot fits the data: a handful of dated events in a few categories. Identity comes from the row
/// labels, not from color, so the chart needs one color and no legend, and a table carries the same data.
/// </remarks>
public sealed record FaultHistoryChartView : IComponentView
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    public required string Title { get; init; }

    public string? EquipmentId { get; init; }

    /// <summary>Fault codes, top to bottom.</summary>
    public IReadOnlyList<string> Rows { get; init; } = [];

    public IReadOnlyList<FaultMarker> Markers { get; init; } = [];

    public IReadOnlyList<ChartTick> Ticks { get; init; } = [];

    /// <summary>The most recent fault, which the chart labels directly.</summary>
    public FaultMarker? Latest { get; init; }

    public string Summary =>
        Markers.Count == 0
            ? $"{Title}: no faults yet."
            : $"{Title}: {Markers.Count} fault{(Markers.Count == 1 ? string.Empty : "s")} from {Markers.Min(m => m.Date):MMMM yyyy} to {Markers.Max(m => m.Date):MMMM yyyy}. " +
              $"Latest: {Latest!.Code}{(Latest.Description is null ? string.Empty : $", {Latest.Description}")}, on {Latest.DateLabel}.";

    public static FaultHistoryChartView From(FaultHistoryChartProps props)
    {
        var faults = (props.Faults ?? [])
            .Select(f => (Fault: f, Date: DateOnly.TryParseExact(f.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateOnly?)null))
            .Where(f => f.Date is not null && !string.IsNullOrEmpty(f.Fault.Code)) // a fault still streaming waits for its date and code
            .ToList();

        var title = props.Title ?? (props.EquipmentId is null ? "Fault history" : $"{props.EquipmentId} fault history");
        if (faults.Count == 0)
        {
            return new FaultHistoryChartView { Title = title, EquipmentId = props.EquipmentId };
        }

        // The time axis: from the month before the first fault to the month after the last.
        var first = faults.Min(f => f.Date!.Value);
        var last = faults.Max(f => f.Date!.Value);
        var start = new DateOnly(first.Year, first.Month, 1).AddMonths(-1);
        var end = new DateOnly(last.Year, last.Month, 1).AddMonths(2);
        double days = end.DayNumber - start.DayNumber;
        double X(DateOnly date) => (date.DayNumber - start.DayNumber) / days;

        var rows = faults.Select(f => f.Fault.Code!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var markers = faults
            .OrderBy(f => f.Date)
            .Select(f => new FaultMarker(f.Date!.Value, f.Date.Value.ToString("MMMM d, yyyy", English), f.Fault.Code!, f.Fault.Description, X(f.Date.Value), rows.IndexOf(f.Fault.Code!)))
            .ToList();

        return new FaultHistoryChartView
        {
            Title = title,
            EquipmentId = props.EquipmentId,
            Rows = rows,
            Markers = markers,
            Ticks = AxisTicks(start, end, X),
            Latest = markers[^1],
        };
    }

    // Monthly marks for a short history, quarters for up to two years, years beyond that. A tick names
    // its year the first time and every January; the others need only the month. The axis ends in
    // padding, so the end itself gets no tick (its label would crowd the last real one).
    private static List<ChartTick> AxisTicks(DateOnly start, DateOnly end, Func<DateOnly, double> x)
    {
        var months = (end.Year - start.Year) * 12 + end.Month - start.Month;
        var step = months <= 8 ? 1 : months <= 24 ? 3 : 12;
        List<ChartTick> ticks = [];
        for (var month = start; month < end; month = month.AddMonths(1))
        {
            if ((month.Month - 1) % step != 0)
            {
                continue;
            }

            var label = ticks.Count == 0 || month.Month == 1 ? month.ToString("MMM yyyy", English) : month.ToString("MMM", English);
            ticks.Add(new ChartTick(label, x(month)));
        }

        return ticks;
    }
}

/// <summary>One fault on the chart: <paramref name="X"/> along the time axis (0 to 1) in row <paramref name="Row"/>.</summary>
public sealed record FaultMarker(DateOnly Date, string DateLabel, string Code, string? Description, double X, int Row)
{
    public string Tooltip => $"{Code} on {DateLabel}{(Description is null ? string.Empty : $": {Description}")}";
}

/// <summary>A labeled mark on the time axis.</summary>
public sealed record ChartTick(string Label, double X);

using System.Globalization;
using Ch11.Beacon.Components;

namespace Ch11.Beacon.ComponentViews;

/// <summary>
/// The schedule timeline, laid out: each block's position and width as fractions of the visible day, so
/// a Blazor template turns them into percentages and a .NET MAUI drawable into pixels, with the same math.
/// </summary>
public sealed record ScheduleTimelineView : IComponentView
{
    public required string Title { get; init; }

    /// <summary>The day, as people read it: <c>Monday, November 16</c>.</summary>
    public string? DateLabel { get; init; }

    /// <summary>Hour marks along the axis.</summary>
    public IReadOnlyList<TimelineTick> Ticks { get; init; } = [];

    public IReadOnlyList<TimelineLaneView> Lanes { get; init; } = [];

    public string Summary =>
        $"{Title}{(DateLabel is null ? string.Empty : $", {DateLabel}")}. " +
        string.Join(" ", Lanes.Select(l => $"{l.Name}: {(l.Blocks.Count == 0 ? "nothing scheduled" : string.Join(", ", l.Blocks.Select(b => b.Description)))}."));

    /// <summary>The visible day: 7:00 to 18:00, widened to fit any slot outside it.</summary>
    public static (int StartHour, int EndHour) Window(IEnumerable<TimelineSlot> slots)
    {
        var times = slots.SelectMany(s => new[] { Time(s.Start), Time(s.End) }).OfType<TimeOnly>().ToList();
        var start = times.Count == 0 ? 7 : Math.Min(7, times.Min().Hour);
        var end = times.Count == 0 ? 18 : Math.Max(18, times.Max() is var last && last.Minute > 0 ? last.Hour + 1 : last.Hour);
        return (start, Math.Min(end, 24));
    }

    public static ScheduleTimelineView From(ScheduleTimelineProps props)
    {
        var lanes = props.Lanes ?? [];
        var (startHour, endHour) = Window(lanes.SelectMany(l => l.Slots ?? []));
        var minutes = (endHour - startHour) * 60.0;

        double Offset(TimeOnly time) => ((time.Hour - startHour) * 60 + time.Minute) / minutes;

        return new ScheduleTimelineView
        {
            Title = props.Title ?? "Schedule",
            DateLabel = DateOnly.TryParseExact(props.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date.ToString("dddd, MMMM d", CultureInfo.GetCultureInfo("en-US"))
                : null,
            Ticks = [.. Enumerable.Range(startHour, endHour - startHour + 1)
                .Where(h => (h - startHour) % 2 == 0)
                .Select(h => new TimelineTick($"{h}:00", ((h - startHour) * 60) / minutes))],
            Lanes = [.. lanes.Select((lane, i) => new TimelineLaneView(
                lane.TechnicianId ?? $"lane-{i}",
                lane.TechnicianName ?? lane.TechnicianId ?? "Technician",
                [.. (lane.Slots ?? [])
                    .Select(s => (Slot: s, Start: Time(s.Start), End: Time(s.End)))
                    .Where(s => s.Start is not null && s.End is not null && s.End > s.Start) // a slot still streaming waits for its end
                    .Select(s => new TimelineBlock(
                        Clock(s.Start!.Value),
                        Clock(s.End!.Value),
                        Offset(s.Start.Value),
                        Offset(s.End.Value) - Offset(s.Start.Value),
                        s.Slot.WorkOrderId ?? s.Slot.Label ?? "Free",
                        IsFree: s.Slot.WorkOrderId is null))]))],
        };
    }

    private static TimeOnly? Time(string? text) =>
        TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;

    private static string Clock(TimeOnly time) => time.ToString("H:mm", CultureInfo.InvariantCulture);
}

/// <summary>An hour mark, at <paramref name="Offset"/> (0 to 1) along the axis.</summary>
public sealed record TimelineTick(string Label, double Offset);

/// <summary>One technician's lane.</summary>
public sealed record TimelineLaneView(string Key, string Name, IReadOnlyList<TimelineBlock> Blocks);

/// <summary>A block in a lane: where it starts and how wide it is, as fractions of the visible day.</summary>
public sealed record TimelineBlock(string Start, string End, double Offset, double Width, string Label, bool IsFree)
{
    /// <summary>The narrowest block, as a fraction of the day, that has room for its label inside it.</summary>
    public const double MinLabeledWidth = 0.15;

    public string Description => $"{(IsFree ? "free" : Label)} {Start} to {End}";

    /// <summary>Whether to draw the label inside the block. A narrower block keeps it in its tooltip and accessible text rather than clipping it.</summary>
    public bool ShowsLabel => Width >= MinLabeledWidth;
}

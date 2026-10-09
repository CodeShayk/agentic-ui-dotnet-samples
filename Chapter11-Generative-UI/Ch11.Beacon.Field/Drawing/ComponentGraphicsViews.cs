using Ch11.Beacon.ComponentViews;
using Microsoft.Maui.Graphics;

namespace Ch11.Beacon.Field.Drawing;

/// <summary>
/// The fault history chart in .NET MAUI: a <see cref="GraphicsView"/> that draws the shared
/// <see cref="FaultHistoryChartView"/>. Every position comes from the view model as a fraction of the
/// plot, the same numbers Beacon Desk's SVG scales to its viewBox.
/// </summary>
public sealed class FaultChartGraphicsView : GraphicsView
{
    public static readonly BindableProperty ChartProperty = BindableProperty.Create(
        nameof(Chart), typeof(FaultHistoryChartView), typeof(FaultChartGraphicsView),
        propertyChanged: (view, _, value) => ((FaultChartGraphicsView)view).Show((FaultHistoryChartView?)value));

    public FaultHistoryChartView? Chart
    {
        get => (FaultHistoryChartView?)GetValue(ChartProperty);
        set => SetValue(ChartProperty, value);
    }

    private void Show(FaultHistoryChartView? chart)
    {
        Drawable = chart is null ? null : new FaultChartDrawable(chart);
        HeightRequest = FaultChartDrawable.HeightFor(chart);
        SemanticProperties.SetDescription(this, chart?.Summary); // what a screen reader says instead of the drawing
        Invalidate();
    }
}

/// <summary>The schedule timeline in .NET MAUI, drawn from the shared <see cref="ScheduleTimelineView"/>.</summary>
public sealed class TimelineGraphicsView : GraphicsView
{
    public static readonly BindableProperty TimelineProperty = BindableProperty.Create(
        nameof(Timeline), typeof(ScheduleTimelineView), typeof(TimelineGraphicsView),
        propertyChanged: (view, _, value) => ((TimelineGraphicsView)view).Show((ScheduleTimelineView?)value));

    public ScheduleTimelineView? Timeline
    {
        get => (ScheduleTimelineView?)GetValue(TimelineProperty);
        set => SetValue(TimelineProperty, value);
    }

    private void Show(ScheduleTimelineView? timeline)
    {
        Drawable = timeline is null ? null : new TimelineDrawable(timeline);
        HeightRequest = TimelineDrawable.HeightFor(timeline);
        SemanticProperties.SetDescription(this, timeline?.Summary);
        Invalidate();
    }
}

internal static class Palette
{
    private static bool Dark => Application.Current?.RequestedTheme == AppTheme.Dark;

    public static Color Accent => Dark ? Color.FromArgb("#3B9BF0") : Color.FromArgb("#0067B8");

    public static Color Text => Dark ? Color.FromArgb("#F2F2F2") : Color.FromArgb("#1F1F1F");

    public static Color Muted => Dark ? Color.FromArgb("#B4B6BB") : Color.FromArgb("#595959");

    public static Color Grid => Dark ? Color.FromArgb("#3A3C42") : Color.FromArgb("#D9DBE0");

    public static Color Surface => Dark ? Color.FromArgb("#222327") : Colors.White;
}

internal sealed class FaultChartDrawable(FaultHistoryChartView chart) : IDrawable
{
    private const float Left = 44, Right = 16, Top = 22, RowHeight = 32, Bottom = 28;

    public static double HeightFor(FaultHistoryChartView? chart) => Top + Math.Max(chart?.Rows.Count ?? 1, 1) * RowHeight + Bottom;

    public void Draw(ICanvas canvas, RectF rect)
    {
        var plotRight = rect.Width - Right;
        var plotBottom = Top + Math.Max(chart.Rows.Count, 1) * RowHeight;
        float X(double fraction) => Left + (float)fraction * (plotRight - Left);
        float Y(int row) => Top + row * RowHeight + RowHeight / 2f;

        canvas.FontSize = 11;
        canvas.StrokeSize = 1;
        canvas.StrokeColor = Palette.Grid;

        foreach (var tick in chart.Ticks)
        {
            canvas.DrawLine(X(tick.X), Top, X(tick.X), plotBottom);
            canvas.FontColor = Palette.Muted;
            canvas.DrawString(tick.Label, LabelLeft(X(tick.X), tick.X), plotBottom + 6, LabelWidth, 16, Align(tick.X), VerticalAlignment.Top);
        }

        for (var row = 0; row < chart.Rows.Count; row++)
        {
            canvas.StrokeColor = Palette.Grid;
            canvas.DrawLine(Left, Y(row), plotRight, Y(row));
            canvas.FontColor = Palette.Text;
            canvas.DrawString(chart.Rows[row], 0, Y(row) - 8, Left - 8, 16, HorizontalAlignment.Right, VerticalAlignment.Center);
        }

        // Dots with a ring in the surface color, so overlapping marks stay distinct.
        foreach (var marker in chart.Markers)
        {
            canvas.FillColor = Palette.Surface;
            canvas.FillCircle(X(marker.X), Y(marker.Row), 7);
            canvas.FillColor = Palette.Accent;
            canvas.FillCircle(X(marker.X), Y(marker.Row), 5);
        }

        if (chart.Latest is { } latest)
        {
            canvas.FontColor = Palette.Text;
            canvas.DrawString(latest.Date.ToString("MMM d", System.Globalization.CultureInfo.GetCultureInfo("en-US")),
                LabelLeft(X(latest.X), latest.X), Y(latest.Row) - 26, LabelWidth, 14, Align(latest.X), VerticalAlignment.Bottom);
        }
    }

    private const float LabelWidth = 80;

    // A label near either edge grows inward, like the Desk's SVG text-anchor: its box ends (or starts) at the mark.
    private static float LabelLeft(float x, double fraction) =>
        fraction > 0.9 ? x - LabelWidth : fraction < 0.1 ? x : x - LabelWidth / 2;

    private static HorizontalAlignment Align(double fraction) =>
        fraction > 0.9 ? HorizontalAlignment.Right : fraction < 0.1 ? HorizontalAlignment.Left : HorizontalAlignment.Center;
}

internal sealed class TimelineDrawable(ScheduleTimelineView timeline) : IDrawable
{
    private const float NameWidth = 96, AxisHeight = 18, LaneHeight = 26, Gap = 6;

    public static double HeightFor(ScheduleTimelineView? timeline) =>
        AxisHeight + Math.Max(timeline?.Lanes.Count ?? 1, 1) * (LaneHeight + Gap);

    public void Draw(ICanvas canvas, RectF rect)
    {
        var trackWidth = rect.Width - NameWidth;
        float X(double fraction) => NameWidth + (float)fraction * trackWidth;

        canvas.FontSize = 11;
        canvas.FontColor = Palette.Muted;
        foreach (var tick in timeline.Ticks)
        {
            canvas.DrawString(tick.Label, X(tick.Offset) - 30, 0, 60, AxisHeight, HorizontalAlignment.Center, VerticalAlignment.Top);
        }

        for (var i = 0; i < timeline.Lanes.Count; i++)
        {
            var lane = timeline.Lanes[i];
            var top = AxisHeight + i * (LaneHeight + Gap);

            canvas.FontColor = Palette.Text;
            canvas.DrawString(lane.Name, 0, top, NameWidth - 6, LaneHeight, HorizontalAlignment.Left, VerticalAlignment.Center);
            canvas.StrokeColor = Palette.Grid;
            canvas.DrawRoundedRectangle(NameWidth, top, trackWidth, LaneHeight, 4);

            foreach (var block in lane.Blocks)
            {
                var bounds = new RectF(X(block.Offset) + 1, top + 2, (float)block.Width * trackWidth - 2, LaneHeight - 4);
                if (block.IsFree)
                {
                    canvas.StrokeColor = Palette.Muted;
                    canvas.DrawRoundedRectangle(bounds, 4);
                    canvas.FontColor = Palette.Muted;
                }
                else
                {
                    canvas.FillColor = Palette.Accent;
                    canvas.FillRoundedRectangle(bounds, 4);
                    canvas.FontColor = Colors.White;
                }

                if (block.ShowsLabel) // the same rule as the Desk: no clipped labels
                {
                    canvas.DrawString(block.Label, bounds.X + 4, bounds.Y, bounds.Width - 8, bounds.Height, HorizontalAlignment.Left, VerticalAlignment.Center);
                }
            }
        }
    }
}

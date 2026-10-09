using System.Globalization;
using AGUI.Abstractions;
using Beacon.Domain;

namespace Ch12.Beacon.Desk.Sheet;

/// <summary>
/// What the dispatcher's board shows that the agent may need to know but must never change: the day
/// she has selected. It travels as <c>RunAgentInput.context</c> on every run, not as state, because it
/// belongs to the Desk, flows one way and is meant for the model to read.
/// </summary>
public sealed class DispatcherBoard
{
    private int _selectedDay = SeedData.Today.DayNumber; // an int, so a run on another thread reads it whole

    /// <summary>The day selected on the board. Beacon's data starts on Monday, November 16, 2026.</summary>
    public DateOnly SelectedDate
    {
        get => DateOnly.FromDayNumber(Volatile.Read(ref _selectedDay));
        set => Volatile.Write(ref _selectedDay, value.DayNumber);
    }

    /// <summary>The context entries for one run's input.</summary>
    public IList<AGUIContext> ToContext() =>
    [
        new AGUIContext
        {
            Description = "The day selected on the dispatcher's board (ISO 8601 date)",
            Value = SelectedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        },
    ];
}
